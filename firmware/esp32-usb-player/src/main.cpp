#include <Arduino.h>
#include <ArduinoJson.h>
#include <Preferences.h>
#include <WiFi.h>
#include <WebServer.h>
#include <ESPmDNS.h>
#include <USB.h>
#include <USBCDC.h>
#include <esp_system.h>
#include <esp_wifi.h>
#include "storage.h"
#include "player.h"
#include "firmware_update.h"
#include "discovery.h"
#include "web_settings.h"
#include "web_auth.h"
#include "web_page.h"

namespace {
USBCDC console;
Storage storage;
Player player(storage);
WebServer server(80);
String ssid, password, token, serialLine, bootError;
FirmwareUpdate updater(server,player,storage,token);
HarpDiscovery discovery;
WebSettings web;
WebAuth webAuth;
CardPins pins;
bool serialOverflow = false, ready = false;
String deviceId;

bool validToken(const String &s) {
    if (s.length() < 32 || s.length() > 64) return false;
    for (unsigned i = 0; i < s.length(); ++i) if (!isAlphaNumeric(s[i])) return false;
    return true;
}
void readConfig() {
    Preferences p; p.begin("delta-harp", false);
    DynamicJsonDocument doc(2048);
    deserializeJson(doc, p.getString("config", "{}"));
    ssid = doc["ssid"].as<String>(); password = doc["password"].as<String>();
    pins.mmc = String(doc["sdMode"] | (PLAYER_SD_MMC ? "sdmmc" : "spi")) == "sdmmc";
    pins.clk = doc["clk"] | PLAYER_SD_SCK; pins.cmd = doc["cmd"] | PLAYER_SD_MOSI;
    pins.d0 = doc["d0"] | PLAYER_SD_MISO; pins.cs = doc["cs"] | PLAYER_SD_CS;
    token = p.getString("token", "");
    if (!validToken(token)) {
        token = "";
        for (int i = 0; i < 4; ++i) { char buf[9]; snprintf(buf,sizeof(buf),"%08x",esp_random()); token += buf; }
        p.putString("token", token);
    }
    p.end();
}
void statusJson(JsonDocument &doc) {
    const auto state = player.status();
    doc["ok"] = true; doc["firmware"] = HARP_FIRMWARE_VERSION; doc["deviceId"] = deviceId;
    updater.status(doc.createNestedObject("update"));
    doc["state"] = state.state; doc["file"] = state.file;
    doc["error"] = state.error.isEmpty() ? bootError : state.error;
    doc["busy"] = state.busy; doc["elapsedMs"] = state.elapsedMs; doc["durationMs"] = state.durationMs;
    doc["countdownMs"] = state.countdownMs; doc["track"] = state.track; doc["channel"] = state.channel;
    doc["baseOctave"] = state.baseOctave; doc["speed"] = state.speed;
    doc["transpose"] = state.transpose; doc["loop"] = state.loop; doc["notes"] = state.notes;
    doc["dryRun"] = state.dryRun; doc["emittedNotes"] = state.emittedNotes;
    doc["usbConnected"] = player.usbConnected.load(); doc["storage"] = storage.ownerName();
    doc["sharedLibrary"] = true;
    doc["pairingSeconds"] = discovery.pairingSeconds();
    doc["startMs"] = web.startMs;
    doc["cardId"] = storage.identity(); doc["cardSectors"] = storage.sectors();
    doc["ip"] = WiFi.localIP().toString(); doc["apIp"] = WiFi.softAPIP().toString();
    doc["apClients"] = WiFi.softAPgetStationNum();
    doc["sdPinsConfigured"] = pins.valid(); doc["sdMode"] = pins.mmc ? "sdmmc" : "spi";
    auto p = doc.createNestedObject("pins"); p["clk"] = pins.clk; p["cmd"] = pins.cmd; p["d0"] = pins.d0; p["cs"] = pins.cs;
    doc["freeHeap"] = ESP.getFreeHeap();
}
void respond(int code, JsonDocument &doc) {
    String text; serializeJson(doc,text); server.sendHeader("Cache-Control","no-store");
    server.send(code,"application/json; charset=utf-8",text);
}
void error(int code, const char *message) {
    StaticJsonDocument<192> doc; doc["ok"] = false; doc["error"] = message; respond(code,doc);
}
bool authorized() {
    if (server.header("Authorization") == String("Bearer ") + token) return true;
    String header=server.header("Authorization");
    if(header.startsWith("Bearer ") && webAuth.authorized(header.substring(7),millis())) return true;
    error(401,"login_required"); return false;
}
bool jsonBody(JsonDocument &doc) {
    String data = server.arg("plain");
    if (data.length() > 1024 || deserializeJson(doc,data) || !doc.is<JsonObject>()) {
        error(400,"invalid_json"); return false;
    }
    return true;
}
bool body(JsonDocument &doc) { return authorized() && jsonBody(doc); }
void accept(const PlayerRequest &r) {
    if (updater.busy()) { error(409,"update_in_progress"); return; }
    if (!player.submit(r)) { error(503,"command_queue_full"); return; }
    StaticJsonDocument<128> doc; doc["ok"] = true; doc["accepted"] = true; respond(202,doc);
}
bool validPath(const String &path) {
    return path.startsWith("/") && path.length() < 192 && path.indexOf("..") < 0 && path.indexOf('\\') < 0
        && path.indexOf('\n') < 0 && path.indexOf('\r') < 0;
}
bool options(JsonDocument &doc, PlayerRequest &r) {
    const char *ints[] = {"speed","transpose","countdown","track","channel"};
    for (auto name : ints) if (doc.containsKey(name) && !doc[name].is<int>()) return false;
    if (doc.containsKey("loop") && !doc["loop"].is<bool>()) return false;
    if (doc.containsKey("dryRun") && !doc["dryRun"].is<bool>()) return false;
    r.speed = doc["speed"] | r.speed; r.transpose = doc["transpose"] | r.transpose;
    r.countdown = doc["countdown"] | r.countdown; r.track = doc["track"] | -1; r.channel = doc["channel"] | -1;
    r.loop = doc["loop"] | r.loop;
    r.dryRun = doc["dryRun"] | false;
    if(doc.containsKey("startMs") && (!doc["startMs"].is<uint32_t>() || doc["startMs"].as<uint32_t>()>3600000)) return false;
    r.startMs=doc["startMs"] | r.startMs;
    return r.speed >= 50 && r.speed <= 200 && r.transpose >= -24 && r.transpose <= 24 && r.countdown >= 0
        && r.countdown <= 30 && r.track >= -1 && r.track < int(Harp::MidiFile::MaxTracks) && r.channel >= -1 && r.channel <= 15;
}
void routes() {
    const char *headers[] = {"Authorization"}; server.collectHeaders(headers,1);
    updater.begin();
    discovery.begin(server,deviceId,token);
    server.on("/",HTTP_GET,[] {
        server.sendHeader("Cache-Control","no-store");
        server.sendHeader("X-Content-Type-Options","nosniff");
        server.sendHeader("Content-Security-Policy","default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'");
        server.sendHeader("Content-Encoding","gzip");
        server.send_P(200,"text/html; charset=utf-8",reinterpret_cast<const char *>(HARP_WEB_PAGE),sizeof(HARP_WEB_PAGE));
    });
    server.on("/api/v1/web/login",HTTP_POST,[] {
        if(!webAuth.configured()) { error(503,"password_not_configured"); return; }
        if(webAuth.limited(millis())) { error(429,"too_many_attempts"); return; }
        StaticJsonDocument<256> d; if(!jsonBody(d)) return;
        if(!d["password"].is<const char *>()) { error(400,"invalid_password"); return; }
        String session=webAuth.login(d["password"].as<String>(),millis());
        if(session.isEmpty()) { error(401,"invalid_password"); return; }
        d.clear(); d["ok"]=true; d["session"]=session; respond(200,d);
    });
    server.on("/api/v1/web/logout",HTTP_POST,[] {
        if(!webAuth.logout(server.header("Authorization").substring(7))) { error(500,"save_failed"); return; }
        StaticJsonDocument<64> d; d["ok"]=true; respond(200,d);
    });
    server.on("/api/v1/web/pairing",HTTP_POST,[] {
        StaticJsonDocument<128> d; if(!body(d)) return;
        if(!d["enabled"].is<bool>()) { error(400,"invalid_options"); return; }
        if(updater.busy()) { error(409,"update_in_progress"); return; }
        if(d["enabled"].as<bool>()) discovery.openPairing(); else discovery.closePairing();
        d.clear(); d["ok"]=true; d["pairingSeconds"]=discovery.pairingSeconds(); respond(200,d);
    });
    // Provision privately using the existing device credential; a browser
    // session alone cannot directly change the password or install firmware.
    server.on("/api/v1/web/password",HTTP_POST,[] {
        if(server.header("Authorization")!=String("Bearer ")+token) { error(401,"device_auth_required"); return; }
        StaticJsonDocument<256> d; if(!jsonBody(d)) return;
        String value=d["password"] | "";
        if(value.length()<8 || value.length()>64) { error(400,"invalid_password_length"); return; }
        if(!webAuth.setPassword(value)) { error(500,"save_failed"); return; }
        d.clear(); d["ok"]=true; respond(200,d);
    });
    server.on("/api/v1/preferences",HTTP_GET,[] {
        if(!authorized()) return;
        StaticJsonDocument<256> d; d["ok"]=true; d["speed"]=web.speed; d["transpose"]=web.transpose;
        d["countdown"]=web.countdown; d["loop"]=web.loop; d["startMs"]=web.startMs; respond(200,d);
    });
    server.on("/api/v1/preferences",HTTP_POST,[] {
        DynamicJsonDocument d(1024); if(!body(d)) return;
        PlayerRequest r; r.command=PlayerCommand::Settings;
        r.speed=web.speed; r.transpose=web.transpose; r.countdown=web.countdown; r.loop=web.loop; r.startMs=web.startMs;
        if(!options(d,r)) { error(400,"invalid_options"); return; }
        if(updater.busy()) { error(409,"update_in_progress"); return; }
        if(!web.savePlayback(r.speed,r.transpose,r.countdown,r.loop,r.startMs)) { error(500,"save_failed"); return; }
        if(!d.containsKey("speed") && !d.containsKey("transpose") && !d.containsKey("loop")) {
            d.clear(); d["ok"]=true; respond(200,d); return;
        }
        accept(r);
    });
    server.on("/api/v1/favorites",HTTP_GET,[] {
        if(!authorized()) return;
        int offset=server.hasArg("offset")?server.arg("offset").toInt():0;
        if(offset<0 || offset>64) { error(400,"invalid_offset"); return; }
        DynamicJsonDocument d(20000); d["ok"]=true; d["total"]=web.favorites.size(); d["offset"]=offset;
        auto rows=d.createNestedArray("songs");
        for(size_t i=offset;i<web.favorites.size() && i<size_t(offset+32);++i) {
            const auto &path=web.favorites[i]; auto row=rows.createNestedObject();
            row["path"]=path; row["name"]=path.substring(path.lastIndexOf('/')+1); row["favorite"]=true;
        }
        respond(200,d);
    });
    server.on("/api/v1/favorites",HTTP_POST,[] {
        DynamicJsonDocument d(1024); if(!body(d)) return;
        String path=d["file"] | "";
        if(!validPath(path) || !d["favorite"].is<bool>()) { error(400,"invalid_favorite"); return; }
        if(updater.busy()) { error(409,"update_in_progress"); return; }
        if(!web.favorite(path,d["favorite"].as<bool>())) { error(409,"favorites_limit_or_save_failed"); return; }
        d.clear(); d["ok"]=true; d["total"]=web.favorites.size(); respond(200,d);
    });
    server.on("/api/v1/status",HTTP_GET,[] { if (authorized()) { DynamicJsonDocument d(2048); statusJson(d); respond(200,d); } });
    server.on("/api/v1/songs",HTTP_GET,[] {
        if (!authorized()) return;
        int offset = server.hasArg("offset") ? server.arg("offset").toInt() : 0;
        if (offset < 0 || offset > 500) { error(400,"invalid_offset"); return; }
        size_t total; auto songs = player.library(offset, total); DynamicJsonDocument d(20000);
        d["ok"] = true; d["total"] = total; d["offset"] = offset; d["storage"] = storage.ownerName();
        auto array = d.createNestedArray("songs");
        for (size_t i = 0; i < songs.size(); ++i) {
            auto e = array.createNestedObject(); e["path"] = songs[i].path; e["name"] = songs[i].name; e["bytes"] = songs[i].bytes;
            e["favorite"]=web.contains(songs[i].path);
        }
        respond(200,d);
    });
    server.on("/api/v1/library/refresh",HTTP_POST,[] { if (authorized()) { PlayerRequest r; r.command=PlayerCommand::Library; accept(r); } });
    server.on("/api/v1/play",HTTP_POST,[] {
        DynamicJsonDocument d(1024); if (!body(d)) return;
        PlayerRequest r; r.command=PlayerCommand::Play;
        r.speed=web.speed; r.transpose=web.transpose; r.loop=web.loop; r.countdown=web.countdown; r.startMs=web.startMs;
        String path = d["file"] | "";
        if (!validPath(path) || !options(d,r)) { error(400,"invalid_file_or_options"); return; }
        if (!player.usbConnected.load()) { error(409,"usb_host_not_connected"); return; }
        if (storage.owner() == CardOwner::Missing) { error(409,"sd_not_available"); return; }
        strlcpy(r.path,path.c_str(),sizeof(r.path)); accept(r);
    });
    server.on("/api/v1/pause",HTTP_POST,[] { if (authorized()) { PlayerRequest r; r.command=PlayerCommand::Pause; accept(r); } });
    server.on("/api/v1/resume",HTTP_POST,[] { if (authorized()) { PlayerRequest r; r.command=PlayerCommand::Resume; accept(r); } });
    server.on("/api/v1/stop",HTTP_POST,[] { if (authorized()) { PlayerRequest r; accept(r); } });
    server.on("/api/v1/seek",HTTP_POST,[] {
        DynamicJsonDocument d(256); if(!body(d)) return;
        auto s=player.status();
        if(s.state!="playing" && s.state!="paused" && s.state!="countdown") { error(409,"no_song_loaded"); return; }
        if(!d["positionMs"].is<uint32_t>() || d["positionMs"].as<uint32_t>()>s.durationMs) { error(400,"seek_out_of_range"); return; }
        PlayerRequest r; r.command=PlayerCommand::Seek; r.positionMs=d["positionMs"]; accept(r);
    });
    server.on("/api/v1/settings",HTTP_POST,[] {
        DynamicJsonDocument d(1024); if (!body(d)) return;
        auto s=player.status(); PlayerRequest r; r.command=PlayerCommand::Settings;
        r.speed=s.speed; r.transpose=s.transpose; r.loop=s.loop;
        if (!options(d,r)) { error(400,"invalid_options"); return; } accept(r);
    });
    server.on("/api/v1/storage/usb",HTTP_POST,[] { if (authorized()) { PlayerRequest r; r.command=PlayerCommand::Export; accept(r); } });
    server.onNotFound([] { error(404,"not_found"); });
    server.begin();
}
void serialReply(JsonDocument &d) { serializeJson(d,console); console.println(); }
void serialCommand(const String &line) {
    DynamicJsonDocument d(2048);
    if (deserializeJson(d,line)) { console.println("{\"ok\":false,\"error\":\"invalid_json\"}"); return; }
    String command = d["command"] | "";
    if (command=="info") { d.clear(); statusJson(d); serialReply(d); return; }
    if (command=="pairing") {
        d.clear(); d["ok"]=true; d["apiKey"]=token; d["apSsid"]="DeltaHarp-"+deviceId.substring(deviceId.length()-6);
        d["apPassword"]=token.substring(0,12); serialReply(d); return;
    }
    if (updater.busy()) { console.println("{\"ok\":false,\"error\":\"update_in_progress\"}"); return; }
    if (command=="control") {
        String action=d["action"] | "", message;
        PlayerRequest r;
        if (action=="play") {
            auto s=player.status(); r.speed=s.speed; r.transpose=s.transpose; r.loop=s.loop;
            String path=d["file"] | "";
            r.command=PlayerCommand::Play;
            r.startMs=web.startMs;
            if (!validPath(path) || !options(d,r)) message="invalid_file_or_options";
            else if (!player.usbConnected.load()) message="usb_host_not_connected";
            else strlcpy(r.path,path.c_str(),sizeof(r.path));
        } else if (action=="settings") {
            auto s=player.status(); r.speed=s.speed; r.transpose=s.transpose; r.loop=s.loop;
            r.command=PlayerCommand::Settings;
            if (!options(d,r)) message="invalid_options";
        } else if (action=="pause") r.command=PlayerCommand::Pause;
        else if (action=="resume") r.command=PlayerCommand::Resume;
        else if (action=="stop") r.command=PlayerCommand::Stop;
        else if (action=="refresh") r.command=PlayerCommand::Library;
        else if (action=="usb") r.command=PlayerCommand::Export;
        else message="unknown_action";
        bool ok=message.isEmpty() && player.submit(r);
        d.clear(); d["ok"]=ok; d["accepted"]=ok;
        if (!ok) d["error"]=message.isEmpty()?"command_queue_full":message;
        serialReply(d); return;
    }
    if (command=="songs") {
        int offset=d["offset"] | 0;
        if (offset<0 || offset>500) { console.println("{\"ok\":false,\"error\":\"invalid_offset\"}"); return; }
        size_t total; auto songs=player.library(offset,total);
        DynamicJsonDocument list(20000); list["ok"]=true; list["total"]=total; list["offset"]=offset;
        auto rows=list.createNestedArray("songs");
        for (size_t i=0;i<songs.size();++i) {
            auto item=rows.createNestedObject(); item["path"]=songs[i].path; item["name"]=songs[i].name;
        }
        serialReply(list); return;
    }
    if (command=="format-exfat") {
        if (String(d["cardId"] | "") != storage.identity() || storage.identity().isEmpty()
            || String(d["confirm"] | "") != "ERASE_SD_CARD") {
            console.println("{\"ok\":false,\"error\":\"card_identity_and_confirmation_required\"}"); return;
        }
        PlayerRequest r; r.command=PlayerCommand::Format;
        d.clear(); d["ok"]=player.submit(r); d["accepted"]=true; serialReply(d); return;
    }
    if (command=="configure") {
        if (storage.owner()==CardOwner::Host) {
            console.println("{\"ok\":false,\"error\":\"eject_sd_on_computer_first\"}"); return;
        }
        if ((d.containsKey("ssid") && (!d["ssid"].is<const char *>() || d["ssid"].as<String>().length()>32))
            || (d.containsKey("password") && (!d["password"].is<const char *>() || d["password"].as<String>().length()>64))) {
            console.println("{\"ok\":false,\"error\":\"invalid_wifi_configuration\"}"); return;
        }
        String newSsid=d["ssid"] | ssid, newPassword=d["password"] | password;
        CardPins next=pins;
        if (d.containsKey("sdMode")) {
            String mode=d["sdMode"] | "";
            if (mode!="spi" && mode!="sdmmc") { console.println("{\"ok\":false,\"error\":\"invalid_sd_mode\"}"); return; }
            next.mmc=mode=="sdmmc"; next.clk=d["clk"] | -1; next.cmd=d["cmd"] | -1;
            next.d0=d["d0"] | -1; next.cs=d["cs"] | -1;
            if (!next.valid()) { console.println("{\"ok\":false,\"error\":\"invalid_sd_pins\"}"); return; }
        }
        d.clear(); d["ssid"]=newSsid; d["password"]=newPassword; d["sdMode"]=next.mmc?"sdmmc":"spi";
        d["clk"]=next.clk; d["cmd"]=next.cmd; d["d0"]=next.d0; d["cs"]=next.cs;
        String saved; serializeJson(d,saved); Preferences p; bool ok=p.begin("delta-harp",false);
        if (ok) ok=p.putString("config",saved)==saved.length(); p.end();
        console.println(ok?"{\"ok\":true,\"restart\":true}":"{\"ok\":false,\"error\":\"save_failed\"}");
        if (ok) { console.flush(); delay(300); ESP.restart(); } return;
    }
    console.println("{\"ok\":false,\"error\":\"unknown_command\"}");
}
}
void setup() {
    readConfig(); web.begin(); webAuth.begin(); deviceId=WiFi.macAddress(); deviceId.replace(":",""); deviceId.toLowerCase();
    bool card=storage.begin(pins); if (!card) bootError=storage.error();
    ready=player.begin();
    if(ready) {
        PlayerRequest r; r.command=PlayerCommand::Settings;
        r.speed=web.speed; r.transpose=web.transpose; r.loop=web.loop; player.submit(r);
    }
    USB.productName("DeltaCrafting Harp + SD"); USB.manufacturerName("DeltaCrafting");
    USB.serialNumber(deviceId.c_str());
    USB.onEvent([](void *,esp_event_base_t,int32_t event,void *) {
        if (event==ARDUINO_USB_STARTED_EVENT || event==ARDUINO_USB_RESUME_EVENT) player.usbConnected=true;
        if (event==ARDUINO_USB_STOPPED_EVENT || event==ARDUINO_USB_SUSPEND_EVENT) player.usbConnected=false;
    });
    console.begin(115200); USB.begin();
    if (!ready) { console.println("player_init_failed"); return; }
    WiFi.persistent(false); WiFi.mode(WIFI_AP_STA); WiFi.setHostname("delta-harp");
    WiFi.setSleep(false);
    if (!WiFi.softAP(("DeltaHarp-"+deviceId.substring(deviceId.length()-6)).c_str(),token.substring(0,12).c_str()))
        bootError="wifi_ap_start_failed";
    esp_wifi_set_bandwidth(WIFI_IF_AP,WIFI_BW_HT20);
    if (!ssid.isEmpty()) { WiFi.setAutoReconnect(true); WiFi.begin(ssid.c_str(),password.c_str()); }
    if (MDNS.begin("delta-harp")) MDNS.addService("http","tcp",80);
    routes();
    console.println("{\"ready\":true,\"firmware\":\"" HARP_FIRMWARE_VERSION "\"}");
}
void loop() {
    if (!ready) { delay(100); return; }
    for (unsigned n=0;n<128 && console.available();++n) {
        char c=console.read(); if (c=='\r') continue;
        if (c=='\n') {
            if (serialOverflow) console.println("{\"ok\":false,\"error\":\"line_too_long\"}");
            else if (!serialLine.isEmpty()) serialCommand(serialLine);
            serialLine=""; serialOverflow=false;
        } else if (serialLine.length()<2048) serialLine+=c; else serialOverflow=true;
    }
    server.handleClient(); updater.tick(); discovery.tick(); delay(1);
}
