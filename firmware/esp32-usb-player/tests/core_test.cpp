#include "midi_file.h"
#include "harp_mapping.h"
#include "firmware_image.h"
#include "read_window.h"
#include "playback_seek.h"
#include "playback_silence.h"
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
void silenceTests() {
    using namespace Harp;
    SilenceMap rests;
    auto event=[&](uint32_t ms,uint8_t type,uint8_t a,uint8_t b,uint8_t track=0,uint8_t channel=0) {
        rests.observe({uint64_t(ms)*1000,track,channel,type,a,b},0,0);
    };
    auto target=[&](uint32_t ms,bool enabled=true,uint32_t threshold=3000) {
        return rests.target(uint64_t(ms)*1000,enabled,threshold)/1000;
    };
    event(5000,0x90,60,80); event(6000,0x80,60,0);
    // Unselected tracks/channels cannot interrupt the selected melody's rest.
    event(7000,0x90,72,80,1); event(8000,0x90,72,80,0,1);
    event(16000,0x90,62,80); event(17000,0x90,62,0);
    event(19000,0x90,64,80); event(20000,0x80,64,0);
    event(23000,0x90,65,80); event(24000,0x80,65,0);
    assert(target(1000)==1000 && target(5500)==5500); // leading/held
    assert(target(6499)==6499 && target(6500)==16000 && target(15999)==16000);
    assert(target(16000)==16000 && target(18000)==18000); // boundary/short rest
    assert(target(21000)==21000 && target(21000,true,2000)==23000); // strict threshold
    assert(target(8000,false)==8000 && target(8000,true,10000)==8000);
    assert(target(8000,true,9999)==16000 && target(25000)==25000); // trailing untouched
    assert(target(7000)==16000); // seek/repeat can move backwards
    rests.clear();
    event(0,0x90,60,80); event(1000,0xb0,64,127); event(2000,0x80,60,0);
    event(12000,0xb0,64,0); event(22000,0x90,62,80);
    assert(target(5000)==5000 && target(12499)==12499 && target(12500)==22000);
    rests.clear();
    event(0,0x90,60,80); event(500,0x90,64,80); event(1000,0x80,64,0);
    event(10000,0x80,60,0); event(10000,0x90,62,80); event(20000,0x80,62,0);
    event(30000,0x90,64,80);
    assert(target(5000)==5000 && target(15000)==15000 && target(20500)==30000);
    rests.clear();
    event(0,0x90,60,80); event(500,0xb0,123,0); event(10000,0x90,62,80);
    assert(target(1000)==10000); // all notes off
    rests.clear();
    assert(target(1000)==1000);

    // Parse actual SMF timing: one-second note, ten-second rest, then another
    // note. Tempo changes and unrelated track events must not shorten holds.
    auto midi=smf({{0,0xff,0x51,3,0x0f,0x42,0x40,0x90,0x40,0xff,0x51,3,7,0xa1,0x20,0,0xff,0x2f,0},
        {0,0x91,60,80,0x83,0x60,0x81,60,0,0xc3,0x40,0x91,62,80,0x83,0x60,0x81,62,0,0,0xff,0x2f,0}});
    MidiFile file; assert(file.open(midi)); Event e;
    uint64_t nextOnset=0;
    while(file.next(e)) {
        rests.observe(e,1,1);
        if(e.track==1 && e.type==0x90 && e.a==62) nextOnset=e.us;
    }
    assert(nextOnset>5000000);
    assert(rests.target(1500000,true,3000)==nextOnset);
    Voice voice; Event next;
    assert(seekEvents(file,voice,next,500000,1,1) && voice.highest()==60);
    assert(seekEvents(file,voice,next,2000000,1,1) && voice.highest()==-1);
    assert(seekEvents(file,voice,next,nextOnset,1,1) && voice.highest()==62);
    file.close();
}
int main() {
    silenceTests();
    Harp::ReadWindow view;
    assert(!view.begin(3,5000,4500,false));
    assert(!view.begin(3,5000,0,true));
    assert(view.begin(3,5000,0,false));
    assert(view.valid(3,5001,false));
    assert(!view.valid(4,5001,false));
    assert(!view.valid(3,5001,true));
    assert(!view.valid(3,15000,false));
    assert(view.begin(4,8000,5500,false));
    assert(view.valid(4,8001,false));
    assert(view.begin(5,1000,UINT32_MAX-2000,false));
    assert(view.valid(5,1001,false));
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
    Voice seekVoice; Event following;
    assert(seekEvents(file,seekVoice,following,250000,1,0));
    assert(seekVoice.highest()==60 && following.us==500000);
    assert(seekEvents(file,seekVoice,following,750000,1,0));
    assert(seekVoice.highest()==62 && following.us==1500000);
    assert(seekEvents(file,seekVoice,following,0,1,0) && seekVoice.highest()==60);
    assert(!seekEvents(file,seekVoice,following,1500000,1,0) && seekVoice.highest()==-1);
    auto saved=m.data; m.data.clear();
    // Once loaded, playback is independent of the SD file and later host writes.
    file.rewind(); assert(file.next(e)); file.close(); m.data=std::move(saved);
    auto pedal=smf({{0,0x90,60,80,0x81,0x70,0xb0,64,127,0,0x80,60,0,0x81,0x70,0xb0,64,0,0,0xff,0x2f,0}});
    assert(file.open(pedal));
    assert(seekEvents(file,seekVoice,following,300000,0,0) && seekVoice.highest()==60);
    assert(!seekEvents(file,seekVoice,following,500000,0,0) && seekVoice.highest()==-1);
    assert(seekEvents(file,seekVoice,following,0,0,0) && seekVoice.highest()==60);file.close();
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
    puts("MIDI parsing, mapping, seeking and long-rest skipping (holds/pedal/chords/thresholds) passed");
}
