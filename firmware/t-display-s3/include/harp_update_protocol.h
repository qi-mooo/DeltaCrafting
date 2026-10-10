#pragma once
#include <stdint.h>
#include <stddef.h>

namespace HarpUpdate {
inline uint32_t revision(const char *version) {
    const char *prefix="delta-harp-v";
    if (!version) return 0;
    while (*prefix) if (*version++!=*prefix++) return 0;
    if (*version<'1' || *version>'9') return 0;
    uint32_t n=0;
    while (*version) {
        if (*version<'0' || *version>'9' || n>100000) return 0;
        n=n*10+(*version++-'0');
    }
    return n;
}
enum class Result { Complete, BeginFailed, DownloadFailed, UploadFailed, HashFailed, CommitFailed, RebootUnknown };
// Transport owns a fixed-size buffer. A lost chunk response is resolved before
// retrying so the same bytes cannot be appended twice. Commit is never retried.
template<class Transport> Result relay(Transport &io, uint32_t size) {
    if (!io.start()) return Result::BeginFailed;
    for (uint32_t offset=0;offset<size;) {
        size_t count=(size-offset)>4096?4096:(size-offset);
        if (!io.read(count)) { io.abort(); return Result::DownloadFailed; }
        if (!io.send(offset,count)) {
            int64_t stored=io.received();
            if (stored==offset) {
                if (!io.send(offset,count) && io.received()!=int64_t(offset+count)) {
                    io.abort(); return Result::UploadFailed;
                }
            } else if (stored!=int64_t(offset+count)) { io.abort(); return Result::UploadFailed; }
        }
        offset+=count; io.progress(offset,size);
    }
    if (!io.verifyHash()) { io.abort(); return Result::HashFailed; }
    int committed=io.commit(); // 1=accepted, 0=explicit refusal, -1=response lost
    if (!committed) { io.abort(); return Result::CommitFailed; }
    return io.verifyRestart()?Result::Complete:Result::RebootUnknown;
}
}
