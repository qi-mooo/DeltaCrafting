#pragma once
#include <Arduino.h>
#include <array>

class WebAuth {
public:
    void begin();
    bool configured() const { return salt.length()==32 && verifier.length()==64; }
    bool setPassword(const String &password);
    String login(const String &password,uint32_t now);
    bool authorized(const String &session,uint32_t now);
    void logout(const String &session);
    bool limited(uint32_t now) const { return blocked && uint32_t(now-blockedAt)<30000; }
private:
    struct Session { String token; uint32_t touched=0; };
    std::array<Session,4> sessions;
    String salt,verifier;
    unsigned failures=0;
    uint32_t blockedAt=0;
    bool blocked=false;
};
