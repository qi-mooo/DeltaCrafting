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
    bool logout(const String &session);
    bool limited(uint32_t now) const { return blocked && uint32_t(now-blockedAt)<30000; }
private:
    using Sessions=std::array<String,8>;
    Sessions sessions;
    bool save(const String &nextSalt,const String &nextVerifier,const Sessions &nextSessions);
    String salt,verifier;
    unsigned failures=0;
    uint32_t blockedAt=0;
    bool blocked=false;
};
