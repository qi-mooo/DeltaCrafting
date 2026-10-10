"""Opt-in device test. Does not start playback or change device credentials.

python tests/web_auth_test.py --config player.local.json
Password is prompted privately or supplied in HARP_TEST_PASSWORD.
"""
import argparse
import getpass
import json
import os
import secrets
import socket
import time
from pathlib import Path
import urllib.error
import urllib.request
import urllib.parse

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--config', type=Path, required=True)
args = parser.parse_args()
config = json.loads(args.config.read_text())
password = os.environ.get('HARP_TEST_PASSWORD') or getpass.getpass('Web password: ')


def request(path, body=None, token=''):
    headers = {'Content-Type': 'application/json'}
    if token:
        headers['Authorization'] = 'Bearer ' + token
    req = urllib.request.Request(config['url'] + '/api/v1/' + path,
        data=None if body is None else json.dumps(body).encode(), headers=headers)
    try:
        response = urllib.request.urlopen(req, timeout=15)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        return response.status, json.load(response)


assert request('status')[0] == 401
assert request('web/password', {'password': 'not-a-real-password'})[0] == 401
assert request('web/pairing', {'enabled': True})[0] == 401
assert request('web/login', {'password': password + '!wrong'})[0] == 401
code, result = request('web/login', {'password': password})
assert code == 200
session = result['session']
assert len(session) == 64 and session != config['apiKey']
try:
    assert request('status', token=session)[0] == 200
    assert request('favorites', token=session)[0] == 200
    assert request('preferences', token=session)[0] == 200
    assert request('web/password', {'password': 'not-a-real-password'}, session)[0] == 401
    assert request('firmware', token=session)[0] == 401
    # Device authentication still works independently of browser sessions.
    assert request('firmware', token=config['apiKey'])[0] == 200
    assert request('web-pair')[0] == 404
    code, opened = request('web/pairing', {'enabled': True}, session)
    assert code == 200 and 0 < opened['pairingSeconds'] <= 60
    try:
        nonce = secrets.token_hex(16)
        host = urllib.parse.urlparse(config['url']).hostname
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as udp:
            udp.settimeout(4)
            udp.sendto(json.dumps({'type': 'delta-harp-discover', 'protocol': 1, 'nonce': nonce}).encode(), (host, 40110))
            discovered = json.loads(udp.recv(2048))
        assert discovered['nonce'] == nonce and discovered['pairable']
        pairing = {'deviceId': discovered['deviceId'], 'challenge': discovered['challenge']}
        code, paired = request('pair', pairing)
        assert code == 200 and paired['apiKey'] == config['apiKey']
    finally:
        assert request('web/pairing', {'enabled': False}, session)[0] == 200
    assert request('pair', pairing)[0] == 403
finally:
    assert request('web/logout', {}, session)[0] == 200
assert request('status', token=session)[0] == 401
for _ in range(5):
    assert request('web/login', {'password': password + '!wrong'})[0] == 401
assert request('web/login', {'password': password})[0] == 429
time.sleep(31)
code, result = request('web/login', {'password': password})
assert code == 200
assert request('web/logout', {}, result['session'])[0] == 200
print('Password login, wrong-password rejection, control access, web pairing without BOOT, closing pairing, logout and rate-limit recovery passed.')
