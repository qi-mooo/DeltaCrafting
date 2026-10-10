#pragma once
#include <Arduino.h>
#include <vector>
namespace HarpNetwork {
struct Device { String id,name,url,version,challenge; bool pairable=false; };
std::vector<Device> scan();
bool pair(const Device &device,String &key,String &error);
}
