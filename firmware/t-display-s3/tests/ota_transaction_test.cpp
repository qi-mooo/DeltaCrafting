#include "ota_transaction.h"
#include <cassert>
#include <stdexcept>

using DeltaOta::Stage;

struct Flash {
    Stage stage = Stage::None;
    bool runningAudio = false, nextAudio = false;
    bool audioValid = true, monitorValid = true;
    bool audioNew = false, monitorNew = false;
    int cut = -1, steps = 0;

    void point() { if (steps++ == cut) throw std::runtime_error("Power lost"); }
    bool monitorRunning() { return !runningAudio; }
    bool audioRunningAndVerified() { return runningAudio && audioValid && audioNew && stage != Stage::None; }
    bool save(Stage value) { point(); stage = value; point(); return true; }
    bool writeAudio() {
        assert(!runningAudio && !nextAudio);
        point(); audioValid = false; point(); // A reset during erase/write must still boot the monitor.
        audioValid = audioNew = true; point(); return true;
    }
    bool writeMonitor() {
        assert(runningAudio && nextAudio);
        point(); monitorValid = false; point();
        monitorValid = monitorNew = true; point(); return true;
    }
    bool monitorMatches() { return monitorValid && monitorNew; }
    bool bootAudio() { point(); assert(audioValid); nextAudio = true; point(); return true; }
    bool bootMonitor() { point(); assert(monitorValid && monitorNew); nextAudio = false; point(); return true; }
    void reboot() { assert(nextAudio ? audioValid : monitorValid); runningAudio = nextAudio; cut = -1; }
};

int main()
{
    for (int failure = 0; failure < 12; ++failure) {
        Flash f; f.cut = failure;
        try { DeltaOta::audioTransaction(f); } catch (const std::runtime_error &) {}
        f.reboot(); // Every interruption leaves a valid boot target.
        if (!f.runningAudio) assert(DeltaOta::audioTransaction(f));
        f.reboot();
        assert(f.runningAudio && f.stage == Stage::Monitor);
        assert(DeltaOta::monitorTransaction(f));
        f.reboot();
        assert(!f.runningAudio && f.audioNew && f.monitorNew && f.stage == Stage::Done);
    }
    for (int failure = 0; failure < 12; ++failure) {
        Flash f;
        assert(DeltaOta::audioTransaction(f)); f.reboot();
        f.steps = 0; f.cut = failure;
        try { DeltaOta::monitorTransaction(f); } catch (const std::runtime_error &) {}
        f.reboot();
        if (f.runningAudio) assert(DeltaOta::monitorTransaction(f));
        f.reboot();
        assert(!f.runningAudio && f.monitorNew && f.audioNew);
    }
    Flash wrong;
    assert(!DeltaOta::monitorTransaction(wrong) && wrong.steps == 0);
    wrong.runningAudio = true;
    assert(!DeltaOta::audioTransaction(wrong) && wrong.steps == 0);
    assert(!DeltaOta::monitorTransaction(wrong) && wrong.steps == 0);
}
