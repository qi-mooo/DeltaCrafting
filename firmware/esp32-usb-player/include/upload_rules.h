#pragma once
#include <stddef.h>
#include <stdint.h>
#include <string.h>

namespace Harp {
constexpr uint32_t UploadMaxBytes=512*1024;
constexpr size_t UploadMaxFiles=64;
inline bool uploadNameValid(const char *name) {
    size_t n=strlen(name);
    if(n<5 || n>120 || name[0]=='.' || name[n-1]==' ' || strstr(name,"..")) return false;
    for(size_t i=0;i<n;++i) {
        unsigned char c=name[i];
        if(c<32 || c==127 || strchr("/\\:*?\"<>|",c)) return false;
        if(c<128) continue;
        unsigned count=c>=0xc2 && c<=0xdf?1:c>=0xe0 && c<=0xef?2:c>=0xf0 && c<=0xf4?3:0;
        if(!count || i+count>=n) return false;
        unsigned char second=name[i+1];
        if((c==0xe0 && second<0xa0) || (c==0xed && second>=0xa0)
            || (c==0xf0 && second<0x90) || (c==0xf4 && second>=0x90)) return false;
        while(count--) if((static_cast<unsigned char>(name[++i])&0xc0)!=0x80) return false;
    }
    auto ends=[&](const char *ext) {
        size_t len=strlen(ext); if(n<=len) return false;
        for(size_t i=0;i<len;++i) { char c=name[n-len+i]; if(c>='A' && c<='Z') c+='a'-'A'; if(c!=ext[i]) return false; }
        return true;
    };
    return ends(".mid") || ends(".midi") || ends(".kar");
}
}
