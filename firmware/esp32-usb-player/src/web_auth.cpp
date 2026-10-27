#include "web_auth.h"
#include <ArduinoJson.h>
#include <Preferences.h>
#include <esp_system.h>
#include <mbedtls/md.h>
#include <mbedtls/pkcs5.h>

namespace {
String randomHex(unsigned bytes) {
    String result; result.reserve(bytes*2);
    for(unsigned i=0;i<bytes;++i) { char hex[3]; snprintf(hex,sizeof(hex),"%02x",unsigned(esp_random()&255)); result+=hex; }
    return result;
}
String sessionHash(const String &token) {
    unsigned char hash[32];
    if(mbedtls_md(mbedtls_md_info_from_type(MBEDTLS_MD_SHA256),
        reinterpret_cast<const unsigned char *>(token.c_str()),token.length(),hash)) return "";
    String result; result.reserve(64);
    for(auto b:hash) { char hex[3]; snprintf(hex,sizeof(hex),"%02x",b); result+=hex; }
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
    StaticJsonDocument<1536> d;
    if(!deserializeJson(d,p.getString("auth","{}"))) {
        salt=d["salt"] | ""; verifier=d["hash"] | "";
        unsigned i=0;
        for(auto value:d["sessions"].as<JsonArray>()) {
            String hash=value | "";
            if(hash.length()==64 && i<sessions.size()) sessions[i++]=hash;
        }
    }
    p.end();
}
bool WebAuth::save(const String &nextSalt,const String &nextVerifier,const Sessions &nextSessions) {
    StaticJsonDocument<1536> d; d["salt"]=nextSalt; d["hash"]=nextVerifier;
    auto rows=d.createNestedArray("sessions");
    for(const auto &hash:nextSessions) if(!hash.isEmpty()) rows.add(hash);
    if(d.overflowed()) return false;
    String value; serializeJson(d,value);
    Preferences p; if(!p.begin("harp-web",false)) return false;
    bool saved=p.putString("auth",value)==value.length(); p.end(); return saved;
}
bool WebAuth::setPassword(const String &password) {
    if(password.length()<8 || password.length()>64) return false;
    String nextSalt=randomHex(16),nextHash=derive(password,nextSalt);
    if(nextHash.isEmpty()) return false;
    if(!save(nextSalt,nextHash,{})) return false;
    salt=nextSalt; verifier=nextHash; failures=0; blocked=false;
    for(auto &s:sessions) s="";
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
    String token=randomHex(32),hash=sessionHash(token);
    if(hash.isEmpty()) return "";
    Sessions next=sessions;
    for(unsigned i=next.size()-1;i>0;--i) next[i]=next[i-1];
    next[0]=hash;
    if(!save(salt,verifier,next)) return "";
    sessions=std::move(next); return token;
}
bool WebAuth::authorized(const String &session,uint32_t) {
    if(session.length()!=64) return false;
    String hash=sessionHash(session);
    for(const auto &saved:sessions) if(!saved.isEmpty() && equal(saved,hash)) return true;
    return false;
}
bool WebAuth::logout(const String &session) {
    if(session.length()!=64) return true;
    String hash=sessionHash(session); Sessions next=sessions; bool found=false;
    for(auto &saved:next) if(!saved.isEmpty() && equal(saved,hash)) { saved=""; found=true; }
    if(!found) return true;
    if(!save(salt,verifier,next)) return false;
    sessions=std::move(next); return true;
}
