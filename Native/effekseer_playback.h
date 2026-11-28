#pragma once
#include <Effekseer.h>
#include <algorithm>
#include <cmath>
#include <stdexcept>
#include <vector>

// Each imported model has its own timeline. Triggers keep the emitter transform
// at creation, while the frame, auto and test instances follow the controller.
struct EffectPlayback {
    Effekseer::ManagerRef manager;
    Effekseer::EffectRef effect;
    using Handle = Effekseer::Handle;
    Handle manual = -1, automatic = -1, test = -1;
    std::vector<Handle> triggers;
    int lifetime = 0;
    float lastTimeline = 0, lastManual = -1;
    bool triggered = false, exporting = false, started = false, falling = false, wasAutomatic = false;

    Handle spawn() {
        return manager->Play(effect, 0, 0, 0);
    }
    void stop(Handle& handle) {
        if (handle >= 0) manager->StopEffect(handle);
        handle = -1;
    }
    void clearTriggers() {
        for (auto h : triggers) manager->StopEffect(h);
        triggers.clear();
    }
    void reset() {
        stop(manual); stop(automatic); stop(test); clearTriggers();
        lastManual = -1; triggered = false; started = false; falling = false; wasAutomatic = false;
    }
    void update(const float* c, const Effekseer::Matrix43& center, const Effekseer::Matrix43& base) {
        const auto on = [](float v){ return v >= .99999f; };
        const float timeline = c[0], target = std::floor(c[1] + .00001f);
        const bool automaticMode = on(c[6]), rootStopped = on(c[8]);
        if (exporting != on(c[11])) reset();
        exporting = on(c[11]);
        const bool rewind = started && timeline < lastTimeline;
        const float delta = std::max(0.f, timeline - (started ? lastTimeline : 0.f));
        if (rewind) { stop(automatic); clearTriggers(); }
        if (on(c[5])) clearTriggers();
        auto pose = [&](Handle h) {
            if (h < 0) return;
            manager->SetMatrix(h, center);
            manager->SetScale(h, c[2], c[2], c[2]);
            manager->SetBaseMatrix(h, base);
        };
        if (!rewind && !on(c[5]) && on(c[4]) && (!triggered || (!started && timeline == 0))) {
            auto h = spawn();
            if (h >= 0) { pose(h); triggers.push_back(h); }
        }
        triggered = on(c[4]);
        if (rewind) { stop(manual); lastManual = -1; falling = false; }
        if (!started || target != lastManual || timeline != lastTimeline)
            falling = lastManual >= 0 && target < lastManual;
        bool newManual = false;
        if (automaticMode) {
            stop(manual); lastManual = -1;
            if ((!wasAutomatic || (rewind && timeline == 0) || on(c[7])) && !manager->Exists(automatic))
                automatic = spawn();
        } else {
            stop(automatic);
            if (falling || target < 0) stop(manual);
            else if (manual < 0 && !rootStopped) { manual = spawn(); newManual = true; }
        }
        if (!on(c[9])) stop(test);
        else if (!manager->Exists(test) && !rootStopped) test = spawn();

        float manualDelta = newManual ? target : target - lastManual;
        float speed = std::max(0.f, c[3]);
        std::vector<std::pair<Handle,double>> steps;
        auto append = [&](Handle h, float age) {
            if (h < 0 || !manager->Exists(h)) return;
            if (age > lifetime) { manager->StopEffect(h); return; }
            // A typed position or speed can traverse more than the whole editor
            // timeline in one draw. Never use an unbounded float as a loop count.
            if (!std::isfinite(age) || age > 600000) throw std::out_of_range("Effekseer simulation frame");
            steps.emplace_back(h,std::max(0.f,age));
        };
        append(manual,manualDelta);
        append(automatic,delta*speed);
        append(test,(exporting ? (!started ? timeline*2 : delta*2) : c[10])*speed);
        for (auto h : triggers) append(h,delta*speed);
        manager->BeginUpdate();
        pose(manual); pose(automatic); pose(test);
        if (rootStopped) {
            for (auto h : {manual, automatic, test}) if (h >= 0) manager->StopRoot(h);
            for (auto h : triggers) manager->StopRoot(h);
        }
        auto gpu = manager->GetGpuParticleSystem();
        // Step all live emitters together. A held bone gets a zero delta even while
        // another handle in the same effect is emitting or running its test loop.
        bool more;
        do {
            bool moved = false;
            more = false;
            for (auto& entry : steps) {
                if (manager->GetInstanceCount(entry.first) == 0) entry.second=0;
                float step=static_cast<float>(std::min(1.0,entry.second));
                manager->UpdateHandle(entry.first,step);
                moved |= step>0; entry.second-=step;
                more |= entry.second>0;
            }
            if (moved && gpu) gpu->ComputeFrame({true});
        } while (more);
        if (!automaticMode) lastManual = target;
        manager->EndUpdate();
        manager->Flip();
        triggers.erase(std::remove_if(triggers.begin(), triggers.end(),
            [&](Handle h){ return !manager->Exists(h); }), triggers.end());
        lastTimeline = timeline; started = true; wasAutomatic = automaticMode;
    }
};
