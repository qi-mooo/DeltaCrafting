#pragma once

namespace DeltaOta {
enum class Stage { None, Audio, Monitor, Done, Invalid };

// Keep durable writes and boot selection in one testable order. No transaction
// writes its running partition or selects a partially downloaded image.
template<class Operations> bool audioTransaction(Operations &ops)
{
    return ops.monitorRunning() && ops.save(Stage::Audio) && ops.writeAudio()
        && ops.save(Stage::Monitor) && ops.bootAudio();
}

template<class Operations> bool monitorTransaction(Operations &ops)
{
    return ops.audioRunningAndVerified() && ops.bootAudio()
        && (ops.monitorMatches() || ops.writeMonitor())
        && ops.save(Stage::Done) && ops.bootMonitor();
}
}
