#include "harp_update_protocol.h"
#include <assert.h>
#include <vector>
#include <utility>

struct Transport {
    bool startOk=true, readOk=true, hashOk=true, rebootOk=true, aborted=false;
    int commitCode=1, sendFailures=0, commits=0;
    bool lostAck=false, wrongSession=false;
    uint32_t stored=0, readBytes=0, progressBytes=0;
    std::vector<std::pair<uint32_t,size_t>> chunks;
    bool start() { return startOk; }
    bool read(size_t n) { readBytes+=n; return readOk; }
    bool send(uint32_t off,size_t n) {
        chunks.push_back({off,n});
        if(sendFailures-- > 0) { if(lostAck) stored=off+n; return false; }
        assert(stored==off); stored+=n; return true;
    }
    int64_t received() { return wrongSession?-1:stored; }
    void progress(uint32_t n,uint32_t) { progressBytes=n; }
    bool verifyHash() { return hashOk; }
    int commit() { ++commits; return commitCode; }
    bool verifyRestart() { return rebootOk; }
    void abort() { aborted=true; }
};
int main() {
    using namespace HarpUpdate;
    assert(revision("delta-harp-v2")==2 && revision("delta-harp-v12")==12);
    for(auto s:{"", "delta", "delta-harp-v02", "delta-harp-v2x", "axeuh-tools-v28", "delta-harp-v999999999999"}) assert(!revision(s));
    Transport good; assert(relay(good,9000)==Result::Complete);
    assert(good.chunks.size()==3 && good.chunks.back().second==808 && good.readBytes==9000 && good.commits==1 && !good.aborted);
    Transport lost; lost.sendFailures=1; lost.lostAck=true;
    assert(relay(lost,9000)==Result::Complete && lost.chunks.size()==3 && lost.readBytes==9000);
    Transport retry; retry.sendFailures=1;
    assert(relay(retry,9000)==Result::Complete && retry.chunks.size()==4 && retry.readBytes==9000);
    Transport upload; upload.sendFailures=2;
    assert(relay(upload,9000)==Result::UploadFailed && upload.aborted && !upload.commits);
    Transport session; session.sendFailures=1; session.wrongSession=true;
    assert(relay(session,9000)==Result::UploadFailed && session.aborted);
    Transport download; download.readOk=false;
    assert(relay(download,9000)==Result::DownloadFailed && download.aborted && !download.commits);
    Transport hash; hash.hashOk=false;
    assert(relay(hash,9000)==Result::HashFailed && hash.aborted && !hash.commits);
    Transport denied; denied.commitCode=0;
    assert(relay(denied,9000)==Result::CommitFailed && denied.aborted && denied.commits==1);
    Transport unknown; unknown.commitCode=-1;
    assert(relay(unknown,9000)==Result::Complete && unknown.commits==1);
    Transport failedBoot; failedBoot.rebootOk=false;
    assert(relay(failedBoot,9000)==Result::RebootUnknown && !failedBoot.aborted && failedBoot.commits==1);
    Transport start; start.startOk=false;
    assert(relay(start,9000)==Result::BeginFailed && !start.aborted && !start.commits);
}
