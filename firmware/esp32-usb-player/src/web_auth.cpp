#include "web_auth.h"
#include <ArduinoJson.h>
#include <Preferences.h>
#include <esp_system.h>
#include <mbedtls/md.h>
#include <mbedtls/pkcs5.h>

namespace {
constexpr uint32_t SessionMs=12u*60*60*1000;
String randomHex(unsigned bytes) {
    String result; result.reserve(bytes*2);
    for(unsigned i=0;i<bytes;++i) { char hex[3]; snprintf(hex,sizeof(hex),"%02x",unsigned(esp_random()&255)); result+=hex; }
    return result;
}
String derive(const String &password,const String &salt) {
    mbedtls_md_context_t context; mbedtls_md_init(&context);
    int result=mbedtls_md_setup(&context,mbedtls_md_info_from_type(MBEDTLS_MD_SHA256),1);
    unsigned char hash[32];
    if(!result) result=mbedtls_pkcs5_pbkdf2_hmac(&context,
        reinterpret_cast<const unsigned char *>(password.c_str()),password.length(),
        reinterpret_cast<const unsigned char *>(salt.c_str()),salt.length(),20000,sizeof(hash),hash);
    mbedtls_md_free(&context);
    if(result) return "";
    String text; text.reserve(64);
    for(auto b:hash) { char hex[3]; snprintf(hex,sizeof(hex),"%02x",b); text+=hex; }
    return text;
}
bool equal(const String &a,const String &b) {
    if(a.length()!=b.length()) return false;
    uint8_t difference=0;
    for(unsigned i=0;i<a.length();++i) difference|=a[i]^b[i];
    return difference==0;
}
}
void WebAuth::begin() {
    Preferences p; if(!p.begin("harp-web",true)) return;
    StaticJsonDocument<256> d;
    if(!deserializeJson(d,p.getString("auth","{}"))) {
        salt=d["salt"] | ""; verifier=d["hash"] | "";
    }
    p.end();
}
bool WebAuth::setPassword(const String &password) {
    if(password.length()<8 || password.length()>64) return false;
    String nextSalt=randomHex(16),nextHash=derive(password,nextSalt);
    if(nextHash.isEmpty()) return false;
    StaticJsonDocument<256> d; d["salt"]=nextSalt; d["hash"]=nextHash;
    String value; serializeJson(d,value);
    Preferences p; if(!p.begin("harp-web",false)) return false;
    bool saved=p.putString("auth",value)==value.length(); p.end();
    if(!saved) return false;
    salt=nextSalt; verifier=nextHash; failures=0; blocked=false;
    for(auto &s:sessions) s={};
    return true;
}
String WebAuth::login(const String &password,uint32_t now) {
    if(!configured() || limited(now)) return "";
    if(blocked) { blocked=false; failures=0; }
    if(password.length()<8 || password.length()>64 || !equal(derive(password,salt),verifier)) {
        if(++failures>=5) { blocked=true; blockedAt=now; }
        return "";
    }
    failures=0;
    Session *slot=&sessions[0];
    for(auto &s:sessions) {
        if(s.token.isEmpty() || uint32_t(now-s.touched)>=SessionMs) { slot=&s; break; }
        if(uint32_t(now-s.touched)>uint32_t(now-slot->touched)) slot=&s;
    }
    slot->token=randomHex(32); slot->touched=now; return slot->token;
}
bool WebAuth::authorized(const String &session,uint32_t now) {
    if(session.length()!=64) return false;
    for(auto &s:sessions) if(!s.token.isEmpty() && equal(s.token,session)) {
        if(uint32_t(now-s.touched)>=SessionMs) { s={}; return false; }
        s.touched=now; return true;
    }
    return false;
}
void WebAuth::logout(const String &session) {
    for(auto &s:sessions) if(!s.token.isEmpty() && equal(s.token,session)) s={};
}
