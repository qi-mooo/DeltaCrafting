#pragma once
#include <stdint.h>

constexpr uint8_t OUTPUT = 1;
inline uint8_t backlightPin = 0;
inline int backlightDuty = -1;
inline void pinMode(uint8_t, uint8_t) {}
inline void analogWrite(uint8_t pin, int duty) { backlightPin = pin; backlightDuty = duty; }
