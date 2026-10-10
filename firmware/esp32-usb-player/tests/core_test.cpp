#include "midi_file.h"
#include "harp_mapping.h"
#include "firmware_image.h"
#include <assert.h>
#include <string.h>
#include <vector>
#include <stdio.h>

struct Memory : Harp::Reader {
    std::vector<uint8_t> data;
    uint32_t size() const override { return data.size(); }
    bool read(uint32_t pos, void *out, size_t n) override {
        if (pos > data.size() || n > data.size() - pos) return false;
        memcpy(out, data.data()+pos, n); return true;
    }
};
void be32(std::vector<uint8_t> &v, uint32_t n) { for (int i=3;i>=0;--i) v.push_back(n>>(i*8)); }
Memory smf(std::vector<std::vector<uint8_t>> tracks) {
    Memory m; m.data={'M','T','h','d',0,0,0,6,0,uint8_t(tracks.size()>1),0,uint8_t(tracks.size()),1,0xe0};
    for (auto &track : tracks) {
        m.data.insert(m.data.end(),{'M','T','r','k'}); be32(m.data,track.size());
        m.data.insert(m.data.end(),track.begin(),track.end());
    }
    return m;
}
int main() {
    FirmwareImageCheck image, wrong;
    uint8_t header[24]={0xe9}; header[12]=9;
    image.add(header,24); wrong.add(header,24);
    const char *marker=HARP_IMAGE_MARKER;
    for (size_t i=0;i<strlen(marker);++i) image.add(reinterpret_cast<const uint8_t *>(marker+i),1);
    assert(image.valid() && !wrong.valid());
    header[12]=0; FirmwareImageCheck otherChip; otherChip.add(header,24);
    otherChip.add(reinterpret_cast<const uint8_t *>(marker),strlen(marker)); assert(!otherChip.valid());
    using namespace Harp;
    assert(mapPitch(60,4).usage==0x1d && mapPitch(60,4).mouse==0);
    assert(mapPitch(48,4).mouse==1 && mapPitch(72,4).mouse==2);
    assert(mapPitch(73,4).mouse==6 && mapPitch(49,4).mouse==5);
    assert(mapPitch(84,4).usage==0x36 && mapPitch(85,4).mouse==6);
    assert(!mapPitch(47,4).usage && !mapPitch(86,4).usage && !mapPitch(-1,4).usage);
    unsigned valid=0; for (int p=0;p<128;++p) valid+=mapPitch(p,4).usage!=0; assert(valid==38);
    uint32_t hist[128]={}; hist[48]=hist[60]=hist[72]=1; assert(autoOctave(hist,0)==4);
    Voice v; v.event(0x90,60,80); v.event(0x90,64,80); assert(v.highest()==64);
    v.event(0x80,64,0); assert(v.highest()==60);
    v.event(0xb0,64,127); v.event(0x90,60,0); assert(v.highest()==60);
    v.event(0xb0,64,0); assert(v.highest()==-1);
    v.event(0x90,61,80); v.event(0xb0,123,0); assert(v.highest()==-1);
    auto m=smf({{0,0xff,0x51,3,7,0xa1,0x20,0x83,0x60,0xff,0x51,3,0x0f,0x42,0x40,0x83,0x60,0xff,0x2f,0},
        {0,0x90,60,100,0x83,0x60,60,0,0,0x90,62,100,0x83,0x60,0x80,62,0,0,0xff,0x2f,0}});
    MidiFile file; assert(file.open(m)); Event e; std::vector<Event> notes;
    while(file.next(e)) if(e.type==0x90 || e.type==0x80) notes.push_back(e);
    assert(notes.size()==4 && notes[0].track==1 && notes[0].us==0);
    assert(notes[1].us==500000 && notes[2].us==500000 && notes[3].us==1500000);
    file.rewind(); assert(file.next(e)); file.close();
    for (size_t n=0;n<m.data.size();++n) {
        Memory cut=m; cut.data.resize(n); assert(!file.open(cut));
    }
    auto longDelay=smf({{0,0x90,60,100,0xff,0xff,0xff,0x7f,0x80,60,0,0,0xff,0x2f,0}});
    longDelay.data[12]=0; longDelay.data[13]=1; assert(!file.open(longDelay));
    auto zero=smf({{0,0xff,0x51,3,0,0,0,0,0xff,0x2f,0}}); assert(!file.open(zero));
    auto status=smf({{0,60,100,0,0xff,0x2f,0}}); assert(!file.open(status));
    auto sysex=smf({{0,0xf0,0xff,0xff,0xff,0xff,0,0xff,0x2f,0}}); assert(!file.open(sysex));
    auto meta=smf({{0,0xff,1,0xff,0xff,0xff,0xff,0,0xff,0x2f,0}}); assert(!file.open(meta));
    auto smpte=m; smpte.data[12]=0xe7; assert(!file.open(smpte));
    auto format2=m; format2.data[9]=2; assert(!file.open(format2));
    std::vector<uint8_t> huge;
    for (int i=0;i<20000;++i) huge.insert(huge.end(),{0,0x90,60,80});
    huge.insert(huge.end(),{0,0xff,0x2f,0}); auto large=smf({huge});
    for (int i=0;i<3;++i) { assert(!file.open(large)); assert(file.open(m)); file.close(); }
    puts("MIDI tempo/track/running-status, malformed input, memory recovery, mapping and sustain passed");
}
