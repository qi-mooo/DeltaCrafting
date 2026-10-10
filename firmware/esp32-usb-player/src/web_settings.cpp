#include "web_settings.h"
#include <algorithm>

namespace {
bool writeValue(const char *key,const String &value) {
    Preferences p;
    if(!p.begin("harp-web",false)) return false;
    bool ok=p.putString(key,value)==value.length(); p.end(); return ok;
}
}
bool WebSettings::contains(const String &path) const {
    return std::find(favorites.begin(),favorites.end(),path)!=favorites.end();
}
void WebSettings::begin() {
    Preferences p; if(!p.begin("harp-web",true)) return;
    DynamicJsonDocument d(12288);
    if(!deserializeJson(d,p.getString("favorites","[]")) && d.is<JsonArray>())
        for(auto v:d.as<JsonArray>()) {
            if(!v.is<const char *>()) continue;
            String path=v.as<String>();
            if(favorites.size()<64 && path.startsWith("/") && path.length()<192 && !contains(path)) favorites.push_back(path);
        }
    d.clear();
    if(!deserializeJson(d,p.getString("playback","{}"))) {
        int s=d["speed"] | 100,t=d["transpose"] | 0,c=d["countdown"] | 3;
        if(s>=50 && s<=200 && t>=-24 && t<=24 && c>=0 && c<=30) {
            speed=s; transpose=t; countdown=c; loop=d["loop"] | false;
        }
    }
    p.end();
}
bool WebSettings::savePlayback(int s,int t,int c,bool l) {
    if(s<50 || s>200 || t< -24 || t>24 || c<0 || c>30) return false;
    StaticJsonDocument<192> d; d["speed"]=s; d["transpose"]=t; d["countdown"]=c; d["loop"]=l;
    String value; serializeJson(d,value);
    if(!writeValue("playback",value)) return false;
    speed=s; transpose=t; countdown=c; loop=l; return true;
}
bool WebSettings::favorite(const String &path,bool selected) {
    if(contains(path)==selected) return true;
    auto next=favorites;
    if(selected) { if(next.size()>=64) return false; next.push_back(path); }
    else next.erase(std::remove(next.begin(),next.end(),path),next.end());
    DynamicJsonDocument d(12288); auto array=d.to<JsonArray>();
    for(const auto &name:next) array.add(name);
    String value; serializeJson(d,value);
    if(d.overflowed() || value.length()>8192 || !writeValue("favorites",value)) return false;
    favorites=std::move(next); return true;
}
