#!/usr/bin/env python3
"""Opt-in device test. Uses a temporary MIDI and dry-run only; preserves user songs/settings."""
import argparse
import base64
import importlib.util
import json
import time
from pathlib import Path

root=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('control',root/'control.py')
control=importlib.util.module_from_spec(spec);spec.loader.exec_module(control)
p=argparse.ArgumentParser(description=__doc__);p.add_argument('--config',type=Path,required=True);args=p.parse_args()
config=json.loads(args.config.read_text())
def api(path,body=None): return control.api(config,path,body)
def wait_stopped():
 for _ in range(50):
  s=api('status')
  if s['state']=='stopped' and not s['busy']: return s
  time.sleep(.1)
 raise AssertionError('Player did not stop')
def rejected(path,body):
 try: api(path,body)
 except RuntimeError as e:
  assert 'HTTP 4' in str(e);return
 raise AssertionError('Invalid request accepted: '+path)
def upload(name,data):
 wait_stopped();start=api('uploads/begin',{'name':name,'size':len(data)})
 try:
  for off in range(0,len(data),start['chunkBytes']):
   api('uploads/chunk',{'uploadId':start['uploadId'],'offset':off,'data':base64.b64encode(data[off:off+start['chunkBytes']]).decode()})
  return api('uploads/finish',{'uploadId':start['uploadId']})
 except Exception:
  try:api('uploads/abort',{'uploadId':start['uploadId']})
  except RuntimeError:pass
  raise

initial=api('status');assert initial['state']=='stopped' and not initial['busy'],'Player active; no tests started'
prefs=api('preferences');original=api('uploads')['songs'];favorites=api('favorites')
name='Delta-Upload-Test-'+str(int(time.time()))+'.mid';path='/@web/'+name
# One-second note, ten-second silence, then one-second note at 120 BPM.
track=bytes([0,0x90,60,100])+control.vlq(960)+bytes([0x80,60,0])+control.vlq(9600)+bytes([0x90,64,100])+control.vlq(960)+bytes([0x80,64,0,0,0xff,0x2f,0])
data=b'MThd'+control.struct.pack('>IHHH',6,0,1,480)+b'MTrk'+control.struct.pack('>I',len(track))+track
try:
 for bad in ['../x.mid','x/y.mid','.hidden.mid','x.mp3']:
  rejected('uploads/begin',{'name':bad,'size':100})
 for size in [0,525000,-1,'100']:
  rejected('uploads/begin',{'name':name,'size':size})
 start=api('uploads/begin',{'name':name,'size':len(data)})
 rejected('uploads/chunk',{'uploadId':'wrong','offset':0,'data':base64.b64encode(data).decode()})
 rejected('uploads/chunk',{'uploadId':start['uploadId'],'offset':1,'data':base64.b64encode(data).decode()})
 rejected('uploads/finish',{'uploadId':start['uploadId']})
 api('uploads/abort',{'uploadId':start['uploadId']});wait_stopped()
 try: upload(name,b'x'*100)
 except RuntimeError: pass
 else:raise AssertionError('Malformed MIDI saved')
 assert api('uploads')['songs']==original
 upload(name,data);wait_stopped()
 rejected('uploads/begin',{'name':name,'size':len(data)})
 for enabled in [False,True]:
  api('play',{'file':path,'dryRun':True,'startMs':0,'countdown':0,'skipSilence':enabled,'loop':False})
  end=time.monotonic()+6;observed=None
  while time.monotonic()<end:
   observed=api('status');assert observed['dryRun'] and not observed['error']
   if enabled and observed['skippedSilenceMs']>8000:break
   if not enabled and observed['elapsedMs']>=2200:break
   time.sleep(.1)
  assert observed['storage']=='usb'
  if enabled:assert observed['elapsedMs']>=11000 and observed['skippedSilenceMs']>8000
  else:assert observed['elapsedMs']<4000 and observed['skippedSilenceMs']==0
  api('stop',{});wait_stopped()
 print('Upload validation, cancellation, duplicate protection, internal playback and real long-rest skipping passed (dry-run only).')
finally:
 s=api('status')
 if s['file']==path and s['dryRun']:api('stop',{});wait_stopped()
 if any(x['path']==path for x in api('uploads')['songs']):api('uploads/remove',{'file':path});wait_stopped()
 assert api('uploads')['songs']==original
 assert api('preferences')==prefs and api('favorites')==favorites
