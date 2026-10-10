#pragma once
#include <Arduino.h>
#include <ArduinoJson.h>
#include <Preferences.h>
#include <vector>

struct WebSettings {
    int speed=100, transpose=0, countdown=3;
    bool loop=false;
    std::vector<String> favorites;
    bool contains(const String &path) const;
    void begin();
    bool savePlayback(int nextSpeed,int nextTranspose,int nextCountdown,bool nextLoop);
    bool favorite(const String &path,bool selected);
};
