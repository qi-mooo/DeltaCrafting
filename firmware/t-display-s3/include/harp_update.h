#pragma once
#include <Arduino.h>
namespace HarpUpdate {
struct View {
    String current, version, detail;
    bool busy=false, ready=false;
    int percent=0;
};
using Progress=void (*)(const View &);
void check(const String &playerUrl,const String &playerId,Progress progress);
void install(const String &playerUrl,const String &playerId,Progress progress);
void poll(const String &playerUrl,const String &playerId,Progress progress);
}
