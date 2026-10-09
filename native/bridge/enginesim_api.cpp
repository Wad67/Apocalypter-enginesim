#include "enginesim_api.h"
#include "engine_builder.h"

#include "exhaust_system.h"
#include "fuel.h"
#include "ignition_module.h"
#include "piston.h"
#include "cylinder_bank.h"
#include "cylinder_head.h"
#include "impulse_response.h"
#include "constants.h"
#include "crankshaft.h"
#include "effects.h"
#include "induction.h"
#include "simulator.h"
#include "piston_engine_simulator.h"
#include "synthesizer.h"
#include "units.h"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <random>
#include <string>
#include <vector>

namespace {

thread_local std::string g_lastError;
constexpr float kLevelerTarget = 20000.0f;   // engine-sim: 30000

void setError(const std::string &e) { g_lastError = e; }

// 16-bit PCM mono/stereo RIFF (engine-sim's impulse responses); stereo is downmixed
bool readWav16(const std::string &path, std::vector<int16_t> *samples, std::string *error) {
    std::ifstream f(path, std::ios::binary);
    if (!f) { *error = "cannot open " + path; return false; }
    std::vector<char> data((std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
    auto u32 = [&](size_t o) { uint32_t v; memcpy(&v, &data[o], 4); return v; };
    auto u16 = [&](size_t o) { uint16_t v; memcpy(&v, &data[o], 2); return v; };
    if (data.size() < 12 || memcmp(&data[0], "RIFF", 4) != 0 || memcmp(&data[8], "WAVE", 4) != 0) {
        *error = path + " is not a WAV file";
        return false;
    }

    int channels = 0, bits = 0;
    for (size_t o = 12; o + 8 <= data.size();) {
        const uint32_t size = u32(o + 4);
        const size_t body = o + 8;
        if (body + size > data.size()) break;
        if (memcmp(&data[o], "fmt ", 4) == 0 && size >= 16) {
            channels = u16(body + 2);
            bits = u16(body + 14);
        }
        else if (memcmp(&data[o], "data", 4) == 0) {
            if (bits != 16 || channels < 1) { *error = path + ": only 16-bit PCM is supported"; return false; }
            const size_t frames = size / (2 * channels);
            samples->resize(frames);
            for (size_t i = 0; i < frames; ++i) {
                int sum = 0;
                for (int c = 0; c < channels; ++c) sum += (int16_t)u16(body + (i * channels + c) * 2);
                (*samples)[i] = (int16_t)(sum / channels);
            }
            return true;
        }
        o = body + size + (size & 1);
    }
    *error = path + ": no data chunk";
    return false;
}

} // namespace

struct es_session {
    BuiltEngine built;
    Simulator *simulator = nullptr;
    double targetRpm = 0;
    double throttle = 0;
    bool ignition = true;

    // wear (es_set_condition)
    double condition = 1.0;
    double appliedCondition = -1.0;
    std::vector<double> baseBlowby;
    std::vector<double> weakness;     // per cylinder, fixed for the session
    double baseRandomness = 0, baseEfficiency = 0;
    std::vector<double> misfireLeft;  // seconds a cylinder stays dead in its current misfire streak
    std::vector<int> failOrder;       // cylinders in the order they fail completely (weakest first)
    std::vector<double> baseSound;    // each cylinder's sound weight in the synthesizer, as built
    int deadCount = 0;
    double wearIntensity = 1.0;
    double hunt = 0, huntTarget = 0, huntTimer = 0;   // idle hunting, fraction of the target rpm
    double stumble = 0;            // rpm lost to recent misfires, fraction of the target rpm

    // rev limiter (es_set_rev_limiter): spark cut and bounce at the game's limit
    double revLimitRpm = 0, limiterCut = 0.08;
    int limiterPhase = 0;          // 0 off, 1 cut, 2 refire
    double limiterTimer = 0, limiterDrop = 0;   // drop: fraction below the limit the held rpm is pulled to

    double boost = 1.0;            // intake pressure ratio (es_set_boost)
    double idleAir = 0.0;          // throttle floor (es_set_idle_air)

    // drive mode (es_set_drive_mode): the engine powers the game's drivetrain
    bool drive = false;
    bool starter = false;
    double idleRpm = 0;            // the game's idle speed, held by the governor
    double governorI = 0, governorRpm = 0;
    double torque = 0;             // cycle-smoothed engine torque at the crank, N m (+ = driving)
    double rawTorque = 0;          // engine torque at the crank, this frame's average (no cycle smoothing)
    double redlineRpm = 6000;
    Effects effects;               // backfires, valve tick, low-rpm filter
    Induction induction;           // superchargers and turbos as machines (drive mode, es_set_induction)
    double inductionDrag = 0;      // the supercharger's drag on the crank over the last update, N m
    double starterLoad = 0;        // starter current / its stall current (0..1): 1 at rest, 0 at its free speed
    double alpha = 0, lastSpeed = 0;   // held speed's acceleration, rad/s2
    std::mt19937 rng;

    // starting (drive mode, es_set_starting)
    double hardStart = 1.0;        // 0 = every charge lights on the first turn (engine-sim alone)
    double heat = 0;               // engine temperature, 0 cold .. 1 at running temperature (es_set_heat)
    double prime = 0;              // fuel wetting the ports from cranking, 0 dry .. 1 primed
    bool fuel = true;              // fuel reaching the engine (es_set_fuel)
    std::vector<double> rollAngle; // each cylinder's compression start in the 720 degree cycle, rad
    std::vector<char> startMiss;   // this cycle's charge in the cylinder won't light
    double lastCycle = -1;
    bool diesel = false;
    double startWeight = 0;        // how far the starting model has taken over from the wear misfires
    bool giveUp = true, jammed = false;
    double jamTimer = 0;
};

// ---------------------------------------------------------------------
//  Wear. w = 1 - condition, scaled by the wear intensity setting.
//    blow-by   : x(1 + 30 w^2), per-cylinder weakness, one bad cylinder x2
//    combustion: burn-to-burn variation 0.5 -> 1.0, efficiency -0.2 at w = 1
//    misfires  : from 70 % condition down, in streaks of 1-4 firing events
//                (a stumble you hear, rather than scattered roughness); share of
//                events misfiring ~25 % at 50 %, ~40 % at 40 %, up to 60 % (more and
//                it stops sounding like a stumble), bad cylinder x2. An idle and light
//                load thing: x1.5 at a closed throttle, x0.3 at full throttle (under load
//                every misfire is a gap and a bang, and a worn engine sounded clipped)
//    burns     : the burn-to-burn variation eases under load the same way (to 40 %)
//    stumble   : each misfire streak drags the held rpm down (recovering over ~150 ms):
//                the dyno holds the game's rpm, so without this a misfire is only a
//                missing pulse, never the crank losing speed
//    hunting   : the held rpm wanders up to +-12 % at idle when badly worn
//    dead      : 4+ cylinders lose whole cylinders, the weakest first: one under 50 %
//                condition, two under 30 %, and on 6+ cylinders a third under 10 %.
//                A dead cylinder has no spark and contributes nothing to the sound
//                (its weight in the synthesizer's exhaust sum is 0); it still turns
//                with the crank
//    dying     : under 10 % condition the engine is finished: cylinders keep dropping out
//                until at 0 % one is left (on any engine), firing about a third of the
//                time with poor combustion. It coughs on the starter or when turned by the
//                wheels, and cannot idle or pull
// ---------------------------------------------------------------------
static void initWear(es_session *s, const es_engine_params &p) {
    Engine *engine = s->built.engine;
    const int n = engine->getCylinderCount();
    s->rng.seed(std::random_device{}());
    std::uniform_real_distribution<double> spread(0.6, 1.4);
    for (int i = 0; i < n; ++i) {
        s->baseBlowby.push_back(engine->getPiston(i)->getBlowbyK());
        s->weakness.push_back(spread(s->rng));
        s->misfireLeft.push_back(0.0);
    }
    s->weakness[std::uniform_int_distribution<int>(0, n - 1)(s->rng)] *= 2.0;
    for (int i = 0; i < n; ++i) {
        s->failOrder.push_back(i);
        Piston *piston = engine->getPiston(i);
        s->baseSound.push_back(engine->getHead(piston->getCylinderBank()->getIndex())->getSoundAttenuation(piston->getCylinderIndex()));
    }
    std::sort(s->failOrder.begin(), s->failOrder.end(), [s](int a, int b) { return s->weakness[a] > s->weakness[b]; });
    for (int i = 0; i < n; ++i) {
        const double a = std::fmod(std::fmod(p.firingAngleDeg[i] - 180.0, 720.0) + 720.0, 720.0);
        s->rollAngle.push_back(a * constants::pi / 180);
        s->startMiss.push_back(0);
    }
    s->baseRandomness = engine->getFuel()->getBurningEfficiencyRandomness();
    s->baseEfficiency = engine->getFuel()->getMaxBurningEfficiency();
}

static double effectiveWear(const es_session *s) {
    return std::min(1.0, std::max(0.0, (1.0 - s->condition) * s->wearIntensity));
}

// how badly worn, 0 above 70 % condition .. 1 at 0 %
static double wearSeverity(const es_session *s) {
    return std::min(1.0, std::max(0.0, (effectiveWear(s) - 0.3) / 0.7));
}

// how far gone, 0 above 10 % condition .. 1 at 0 %
static double dyingSeverity(const es_session *s) {
    return std::min(1.0, std::max(0.0, (effectiveWear(s) - 0.9) / 0.1));
}

static void applyWear(es_session *s, double dt) {
    Engine *engine = s->built.engine;
    const double w = effectiveWear(s);
    const int n = engine->getCylinderCount();

    if (w != s->appliedCondition) {
        const double c = 1.0 - w;
        s->deadCount = (n < 4) ? 0 : (c < 0.10 && n >= 6) ? 3 : (c < 0.30) ? 2 : (c < 0.50) ? 1 : 0;
        s->deadCount = std::max(s->deadCount, (int)std::floor(dyingSeverity(s) * (n - 1) + 1e-9));
        for (int i = 0; i < n; ++i) {
            engine->getPiston(i)->setBlowbyK(s->baseBlowby[i] * (1 + 30 * w * w * s->weakness[i]));
        }
        for (int i = 0; i < n; ++i) {
            const bool dead = std::find(s->failOrder.begin(), s->failOrder.begin() + s->deadCount, i) != s->failOrder.begin() + s->deadCount;
            Piston *piston = engine->getPiston(i);
            engine->getHead(piston->getCylinderBank()->getIndex())->setSoundAttenuation(
                piston->getCylinderIndex(), dead ? 0.0 : s->baseSound[i]);
        }

        engine->getFuel()->setMaxBurningEfficiency(s->baseEfficiency - 0.2 * w - 0.25 * dyingSeverity(s));
        s->appliedCondition = w;
    }

    const double x = wearSeverity(s);
    const double rpm = std::max(300.0, engine->getRpm());
    const double eventPeriod = 120.0 / rpm;                 // one firing per cylinder per two turns
    const double events = dt / eventPeriod;                 // firing events per cylinder this frame
    const double lightLoad = 1.5 - 1.2 * s->throttle;   // x1.5 at a closed throttle, x0.3 at full
    engine->getFuel()->setBurningEfficiencyRandomness(
        std::min(1.0, s->baseRandomness + 0.5 * w * (1.0 - 0.6 * s->throttle)));
    std::uniform_real_distribution<double> roll(0.0, 1.0);

    for (int i = 0; i < n; ++i) {
        bool dead = false;
        if (std::find(s->failOrder.begin(), s->failOrder.begin() + s->deadCount, i) != s->failOrder.begin() + s->deadCount) {
            dead = true;   // gone for good
        }
        else if (s->misfireLeft[i] > 0) {
            s->misfireLeft[i] -= dt;
            dead = true;
        }
        else if (x > 0) {
            // share of events to misfire, and the chance a streak (mean 2.5 events) starts per event
            // capped at 60 %: a stumble needs firing to contrast with; near 100 % the engine just
            // stops firing and sounds evenly motored, smoother than a stumbling one
            double share = std::min(0.6, 1.0 * std::pow(x, 1.2) * lightLoad * std::min(2.0, s->weakness[i]));
            share *= 1 - s->startWeight;   // on a slow crank the starting model decides
            share += (0.65 - share) * dyingSeverity(s);   // finished: the last cylinders fire about a third of the time
            const double startPerEvent = share / (2.5 * (1.0 - share) + share);
            const double startThisFrame = 1.0 - std::pow(1.0 - startPerEvent, events);
            if (roll(s->rng) < startThisFrame) {
                const int streak = std::uniform_int_distribution<int>(1, 4)(s->rng);
                s->misfireLeft[i] = streak * eventPeriod;
                // a lost power stroke slows the crank: more for fewer cylinders, longer streaks, light load
                s->stumble = std::min(0.15, s->stumble + 0.06 * streak / n * lightLoad);
                dead = true;
            }
        }
        engine->getIgnitionModule()->setPlugEnabled(i, !dead && !s->startMiss[i]);
    }

    // idle hunting: a smoothed random wander, retargeted every 0.3-1.2 s
    s->huntTimer -= dt;
    if (s->huntTimer <= 0) {
        s->huntTimer = 0.3 + 0.9 * roll(s->rng);
        s->huntTarget = (2 * roll(s->rng) - 1);
    }
    const double amplitude = 0.12 * std::pow(x, 1.2) * (1.0 - s->throttle);
    s->hunt += (s->huntTarget * amplitude - s->hunt) * std::min(1.0, dt * 3.0);
    s->stumble *= std::exp(-dt / 0.15);
}

// ---------------------------------------------------------------------
//  Synth pump: Synthesizer::renderAudio() without the condition-variable
//  wait, run on the sim thread. Upstream renders on its own thread and
//  writes m_audioBuffer outside m_lock0 while readAudioOutput reads it under
//  the lock; keeping both on one thread removes that race altogether.
// ---------------------------------------------------------------------
static void pumpSynthesizer(Synthesizer &s) {
    if (s.m_inputChannelCount == 0) return;
    const int n = std::min(
        std::max(0, 2000 - (int)s.m_audioBuffer.size()),
        (int)s.m_inputChannels[0].data.size());
    if (n <= 0) return;

    for (int i = 0; i < s.m_inputChannelCount; ++i) {
        s.m_inputChannels[i].data.read(n, s.m_inputChannels[i].transferBuffer);
    }
    s.m_inputSamplesRead = n;
    s.m_processed = true;

    for (int i = 0; i < s.m_inputChannelCount; ++i) {
        s.m_filters[i].airNoiseLowPass.setCutoffFrequency(
            static_cast<float>(s.m_audioParameters.airNoiseFrequencyCutoff), s.m_audioSampleRate);
        s.m_filters[i].jitterFilter.setJitterScale(s.m_audioParameters.inputSampleNoise);
    }

    for (int i = 0; i < n; ++i) {
        s.m_audioBuffer.write(s.renderAudio(i));
    }
}

extern "C" {

ES_API int32_t es_desc_size(void) { return (int32_t)sizeof(es_engine_desc); }

ES_API int32_t es_default_params(int32_t cylinders, int32_t layout, float bankAngleDeg, float displacementL,
    int32_t diesel, es_engine_params *out) {
    return defaultParams(cylinders, layout, bankAngleDeg, displacementL, diesel, out) ? 1 : 0;
}

ES_API es_session *es_create(const es_engine_desc *desc, const char *irDirectory) {
    if (desc == nullptr) { setError("no engine description"); return nullptr; }
    const std::string irDir = (irDirectory != nullptr) ? irDirectory : ".";

    es_session *s = new es_session;
    std::string error;
    if (!buildEngine(*desc, irDir, &s->built, &error)) {
        s->built.destroy();
        delete s;
        setError("build: " + error);
        return nullptr;
    }

    // as EngineSimApplication::loadEngine, minus the rendering thread
    Engine *engine = s->built.engine;
    s->simulator = engine->createSimulator(s->built.vehicle, s->built.transmission);
    s->simulator->setSimulationFrequency((int)engine->getSimulationFrequency());
    // gas dynamics sub-steps per physics step: the biggest single cost (engine-sim uses 8)
    static_cast<PistonEngineSimulator *>(s->simulator)->setFluidSimulationSteps(
        stableFluidSteps(*desc, engine->getSimulationFrequency(), (desc->fluidSteps > 0) ? (int)desc->fluidSteps : 8));

    Synthesizer::AudioParameters ap = s->simulator->synthesizer().getAudioParameters();
    ap.inputSampleNoise = static_cast<float>(engine->getInitialJitter());
    ap.airNoise = static_cast<float>(engine->getInitialNoise());
    ap.dF_F_mix = static_cast<float>(engine->getInitialHighFrequencyGain());
    ap.levelerTarget = kLevelerTarget;   // 30000 leaves no int16 headroom (~2 % of samples clip)
    s->simulator->synthesizer().setAudioParameters(ap);
    // The raw signal reaches ~2e9 at high rpm (short pipes: the synth scales pulses by 1/L^2),
    // past what engine-sim's 1e-5 minimum gain can bring down to the target, so it clipped.
    // setAudioParameters doesn't reach the leveller's limits (only initialize copies them).
    s->simulator->synthesizer().m_levelingFilter.p_minLevel = 1e-9f;

    for (int i = 0; i < engine->getExhaustSystemCount(); ++i) {
        ImpulseResponse *response = engine->getExhaustSystem(i)->getImpulseResponse();
        std::vector<int16_t> samples;
        if (!readWav16(response->getFilename(), &samples, &error)) {
            es_destroy(s);
            setError("impulse response: " + error);
            return nullptr;
        }
        s->simulator->synthesizer().initializeImpulseResponse(
            samples.data(), (unsigned int)samples.size(), (float)response->getVolume(), i);
    }
    s->redlineRpm = desc->redlineRpm > 500 ? desc->redlineRpm : 6000;
    s->effects.idleRpm = desc->idleRpm > 100 ? desc->idleRpm : 800;
    s->effects.initialize(engine, desc->engine, s->simulator->getSimulationFrequency());

    s->simulator->m_dyno.m_hold = true;
    engine->getIgnitionModule()->m_enabled = true;
    initWear(s, desc->engine);
    s->diesel = desc->engine.diesel != 0;
    return s;
}

ES_API void es_destroy(es_session *s) {
    if (s == nullptr) return;
    if (s->simulator != nullptr) {
        s->simulator->releaseSimulation();
        delete s->simulator;
        s->simulator = nullptr;
    }
    s->built.destroy();
    delete s;
}

ES_API void es_set_target_rpm(es_session *s, double rpm) { if (s != nullptr) s->targetRpm = rpm; }
ES_API void es_set_throttle(es_session *s, double throttle) { if (s != nullptr) s->throttle = std::min(1.0, std::max(0.0, throttle)); }
ES_API void es_set_ignition(es_session *s, int32_t on) { if (s != nullptr) s->ignition = on != 0; }

ES_API void es_set_condition(es_session *s, double condition) {
    if (s != nullptr) s->condition = std::min(1.0, std::max(0.0, condition));
}

ES_API void es_set_rev_limiter(es_session *s, double rpm, double cutSeconds) {
    if (s == nullptr) return;
    s->revLimitRpm = rpm;
    s->limiterCut = std::min(0.3, std::max(0.03, cutSeconds));
}

ES_API void es_set_boost(es_session *s, double pressureRatio) {
    if (s != nullptr) s->boost = std::min(4.0, std::max(0.5, pressureRatio));
}

ES_API void es_set_idle_air(es_session *s, double idleAir) {
    if (s != nullptr) s->idleAir = std::min(0.3, std::max(0.0, idleAir));
}

ES_API void es_set_wear_intensity(es_session *s, double intensity) {
    if (s != nullptr) s->wearIntensity = std::min(3.0, std::max(0.0, intensity));
}

ES_API void es_set_target_latency(es_session *s, double seconds) {
    if (s != nullptr) s->simulator->setTargetSynthesizerLatency(std::min(0.5, std::max(0.01, seconds)));
}

ES_API void es_set_volume(es_session *s, double volume) {
    if (s == nullptr) return;
    Synthesizer::AudioParameters ap = s->simulator->synthesizer().getAudioParameters();
    ap.volume = (float)std::max(0.0, volume);
    s->simulator->synthesizer().setAudioParameters(ap);
}

// ---------------------------------------------------------------------
//  Rev limiter. The game clamps its rpm at the limit, which held by the dyno
//  sounds like an engine pegged short of where it could rev. At the limit
//  under throttle, cut the sparks and let the held rpm fall ~4 %, then refire
//  and climb back: the bounce of a real ignition-cut limiter (~8-12 Hz).
// ---------------------------------------------------------------------
static bool updateLimiter(es_session *s, double dt) {
    const double drop = 0.04;
    const bool atLimit = s->revLimitRpm > 0 && s->targetRpm >= 0.985 * s->revLimitRpm && s->throttle > 0.1 && s->ignition;
    if (s->limiterPhase == 0) {
        if (!atLimit) { s->limiterDrop = 0; return false; }
        s->limiterPhase = 1;
        s->limiterTimer = s->limiterCut;
    }
    s->limiterTimer -= dt;
    if (s->limiterPhase == 1) {          // cut: rpm falls
        s->limiterDrop = std::min(drop, s->limiterDrop + drop * dt / s->limiterCut);
        if (s->limiterTimer <= 0) { s->limiterPhase = 2; s->limiterTimer = 0.6 * s->limiterCut; }
        return true;
    }
    // refire: rpm climbs back to the limit, then cut again if still held there
    s->limiterDrop = std::max(0.0, s->limiterDrop - drop * dt / (0.6 * s->limiterCut));
    if (s->limiterTimer <= 0) s->limiterPhase = 0;
    return false;
}

// ---------------------------------------------------------------------
//  Starting (drive mode). engine-sim fills every cylinder with a perfect mixture from the first turn,
//  and lights every charge, so any engine fired on its first compression. On a real slow crank:
//    priming : fuel reaches the cylinders as the ports and manifold wet (with the key on, the crank
//              turning, however it is turned, and fuel in the tank); until then the charges are
//              lean (none light under a third primed). Revolutions to prime: 1 warm, 4 cold, times 1 + 3 w (worn: poor vacuum at
//              cranking speed draws little fuel through). It dries off over a minute stood. With the
//              tank empty what is left in the lines and float bowl is used up over ~40 revolutions:
//              it runs lean and stops firing.
//    lighting: a primed charge on a slow crank still fails to light some of the time: cold (fuel
//              that doesn't vaporise, a cold, leaky compression) and worn (low compression, fouled
//              plugs): ~60 % of charges light cold and healthy, ~100 % warm, ~30 % cold at 0 %. A
//              diesel lights on compression heat alone, so cold and wear cost it more: ~45 % cold and
//              healthy, ~15 % cold at 0 %
//    catching: priming holds at any speed (a bump start turns the engine faster, but the fuel still
//              has to get there); the lighting failures fade out from half idle to 0.8 x idle; below
//              that it stands in for the wear misfires (an idle and load stumble). Dead cylinders stay dead
//    heat    : the game's engine temperature (es_set_heat)
//  Each cylinder rolls once per cycle, as its compression begins, for its charge.
// ---------------------------------------------------------------------
static void updateStarting(es_session *s, double dt) {
    Engine *engine = s->built.engine;
    const int n = engine->getCylinderCount();
    const double rpm = std::max(0.0, s->targetRpm), idle = std::max(300.0, s->idleRpm);
    const double w = effectiveWear(s);

    const double revs = rpm / 60 * dt;
    if (rpm > 30 && !s->fuel) s->prime = std::max(0.0, s->prime - revs / 40);
    else if (rpm > 30 && s->ignition) s->prime = std::min(1.0, s->prime + revs / ((1 + 3 * (1 - s->heat)) * (1 + 3 * w)));
    else if (rpm <= 30) s->prime *= std::exp(-dt / 60.0);

    const double light = s->diesel
        ? (1 - 0.7 * std::pow(w, 1.2)) * (0.45 + 0.55 * s->heat)
        : (1 - 0.5 * std::pow(w, 1.5)) * (0.6 + 0.4 * s->heat);
    // rich enough to light (under a third primed, too lean at all), at any speed: however the crank is
    // turned (starter, wheels, a push), the fuel has to get there first
    const double rich = std::pow(std::max(0.0, (s->prime - 0.35) / 0.65), 1.5);
    // lighting fails on a slow crank only: turned faster, the charge leaks less, is hotter and better mixed
    const double slow = std::min(1.0, std::max(0.0, (0.8 * idle - rpm) / (0.3 * idle)));
    const double lights = 1 - slow * (1 - light);
    s->startWeight = s->hardStart * slow;
    const double missChance = std::min(1.0, s->hardStart * (1 - rich * lights));

    const double cycle = engine->getOutputCrankshaft()->getCycleAngle();
    if (s->lastCycle >= 0 && missChance > 0) {
        const double fourPi = 4 * constants::pi;
        double from = s->lastCycle, to = cycle;
        if (to < from) to += fourPi;
        if (to - from < constants::pi) {   // not a jump
            std::uniform_real_distribution<double> roll(0.0, 1.0);
            for (int i = 0; i < n; ++i) {
                double a = s->rollAngle[i];
                if (a <= from) a += fourPi;
                if (a <= to) s->startMiss[i] = roll(s->rng) < missChance;
            }
        }
    }
    else if (missChance <= 0) std::fill(s->startMiss.begin(), s->startMiss.end(), 0);
    s->lastCycle = cycle;
}

static double kGovP = 0.5, kGovI = 0.4, kGovD = 0.08;

// Drive mode: the dyno holds the crank at the game's engine speed, and what it takes to hold it
// there is the engine's torque, which the game's drivetrain then runs on. Starter, idle and stalling
// are the engine's own.
static double updateDrive(es_session *s, double dt) {
    Simulator *sim = s->simulator;
    const double rpm = s->targetRpm;
    const double idle = std::max(300.0, s->idleRpm);

    // idle governor: opens the throttle as the speed falls below idle (a real idle air valve)
    // (PI on the speed error, damped by the speed's rate of change: the torque it gets back lags
    // by about half a cycle)
    double governor = 0;
    const double rate = (rpm - s->governorRpm) / dt / idle;
    s->governorRpm = rpm;
    if (s->ignition) {
        // the torque lags by about half a cycle, longer at a slow idle: gentler gains there
        const double slow = std::min(1.0, idle / 900.0);
        const double error = (idle - rpm) / idle;
        s->governorI = std::min(0.3, std::max(-0.05, s->governorI + kGovI * slow * slow * error * dt));
        governor = std::min(0.6, std::max(0.0, slow * (kGovP * error - kGovD * rate) + s->governorI));
    }
    else s->governorI = 0;

    // es_set_ignition is the key: with it on the plugs fire whenever the crank turns, however it is
    // turned (starter, or the wheels in a push / roll start). Whether it runs is the simulation's.
    // The starter is a DC motor: its torque falls linearly from stall (at rest) to nothing at its free
    // speed, and its current, which sags the battery, with it. engine-sim's starter is a speed-capped
    // constraint (full torque below its speed, none above), so its limit is set along that line.
    const double starterFree = s->built.engine->getStarterSpeed() / units::rpm(1);
    const double starterShare = std::min(1.0, std::max(0.0, 1.0 - rpm / std::max(1.0, starterFree)));
    // Held against more than it can turn (in gear, a seized engine), it stays near stall, its current
    // pulls the battery down past what holds the solenoid in: the solenoid drops out, the battery
    // recovers, it pulls in again, and the starter only clicks until the key is let go.
    if (s->starter && s->giveUp && !s->jammed) {
        s->jamTimer = (rpm < 0.1 * starterFree) ? s->jamTimer + dt : 0;
        if (s->jamTimer > 0.35) s->jammed = true;
    }
    else if (!s->starter) { s->jammed = false; s->jamTimer = 0; }
    const bool turning = s->starter && !s->jammed;
    sim->m_starterMotor.m_enabled = turning;
    sim->m_starterMotor.m_maxTorque = s->built.engine->getStarterTorque() * std::max(0.02, starterShare);
    s->starterLoad = turning ? starterShare : 0.0;
    sim->m_dyno.m_fixedNegative = true;
    sim->m_dyno.m_enabled = true;
    sim->m_dyno.m_hold = true;
    sim->m_dyno.m_rotationSpeed = units::rpm(std::max(0.0, rpm));

    // the held speed's acceleration: the dyno spends I * alpha on the sim's own crank, which the
    // game's engine inertia already accounts for, so it is added back
    const double speed = units::rpm(std::max(0.0, rpm));
    const double alphaNow = (speed - s->lastSpeed) / dt;
    s->lastSpeed = speed;
    const double tau = std::min(0.08, std::max(0.015, 0.5 * 120.0 / std::max(60.0, rpm)));   // half a cycle
    s->alpha += (alphaNow - s->alpha) * (1 - std::exp(-dt / tau));
    return governor;
}

ES_API int32_t es_update(es_session *s, double dt) {
    if (s == nullptr) return 0;
    Simulator *sim = s->simulator;
    Engine *engine = s->built.engine;
    dt = std::min(std::max(dt, 1 / 1000.0), 1 / 20.0);

    if (s->drive) {
        const double governor = updateDrive(s, dt);
        const double throttle = std::max(s->throttle, governor);
        engine->setSpeedControl(throttle);
        const bool machines = s->induction.active();
        for (int i = 0; i < engine->getIntakeCount(); ++i) {
            engine->getIntake(i)->m_inletPressure = machines ? s->induction.inletPressure() : units::pressure(s->boost, units::atm);
        }
        updateStarting(s, dt);
        applyWear(s, dt);
        const bool limiterCut = updateLimiter(s, dt);
        engine->getIgnitionModule()->m_enabled = s->ignition && !limiterCut;

        // engine torque: the dyno's, averaged over this frame's physics steps, smoothed over half a cycle
        double sum = 0, drag = 0;
        int steps = 0;
        sim->startFrame(dt);
        const double wear = effectiveWear(s);
        const double stepDt = 1.0 / sim->getSimulationFrequency();
        while (sim->simulateStep()) {
            sum += sim->m_dyno.getTorque();
            ++steps;
            s->effects.step(engine, engine->getRpm(), s->redlineRpm, wear);
            if (machines) {
                // the charge pressure follows what the compressors deliver against what the engine drew
                drag += s->induction.step(engine, units::rpm(std::max(0.0, s->targetRpm)), throttle, stepDt);
                for (int i = 0; i < engine->getIntakeCount(); ++i) engine->getIntake(i)->m_inletPressure = s->induction.inletPressure();
                for (int i = 0; i < engine->getExhaustSystemCount(); ++i) engine->getExhaustSystem(i)->m_outletPressure = s->induction.exhaustPressure();
            }
        }
        // the supercharger is driven off the crank
        s->inductionDrag = steps > 0 ? drag / steps : 0;
        sum -= drag;
        s->rawTorque = (steps > 0 ? sum / steps : 0) + s->built.crankInertia * s->alpha;
        pumpSynthesizer(sim->synthesizer());
        sim->endFrame();
        const double frameTorque = (steps > 0 ? sum / steps : 0) + s->built.crankInertia * s->alpha;
        const double tau = std::min(0.08, std::max(0.015, 0.5 * 120.0 / std::max(60.0, s->targetRpm)));
        s->torque += (frameTorque - s->torque) * (1 - std::exp(-dt / tau));
        return (int32_t)sim->synthesizer().m_audioBuffer.size();
    }

    // a real engine idles on its idle air; at a fully closed throttle the dyno would just be
    // motoring it, and combustion (and so misfires) would hardly be heard
    engine->setSpeedControl(s->idleAir + (1.0 - s->idleAir) * s->throttle);
    for (int i = 0; i < engine->getIntakeCount(); ++i) {
        engine->getIntake(i)->m_inletPressure = units::pressure(s->boost, units::atm);
    }
    applyWear(s, dt);
    const bool limiterCut = updateLimiter(s, dt);
    engine->getIgnitionModule()->m_enabled = s->ignition && !limiterCut;

    // The dyno drives the crank in whichever direction it already turns
    // (Dynamometer::calculate: v_theta < 0 -> v_bias = +speed), and the
    // starter turns it negative (StarterMotor: speed -starterSpeed, bias
    // +starterSpeed). At rest (v_theta == 0) the dyno would push the wrong
    // way, so crank on the starter until the crank turns the starter's way.
    const double v = engine->getOutputCrankshaft()->m_body.v_theta;
    const bool wantSpin = s->targetRpm > 0;
    const bool turningRightWay = v < -units::rpm(50);
    sim->m_starterMotor.m_enabled = wantSpin && !turningRightWay;
    sim->m_dyno.m_enabled = wantSpin && turningRightWay;
    sim->m_dyno.m_hold = true;
    sim->m_dyno.m_rotationSpeed = units::rpm(std::max(0.0, s->targetRpm * (1.0 + s->hunt) * (1.0 - s->stumble) * (1.0 - s->limiterDrop)));

    sim->startFrame(dt);
    double sum = 0;
    int steps = 0;
    const double wear = effectiveWear(s);
    while (sim->simulateStep()) {
        sum += sim->m_dyno.getTorque();
        ++steps;
        s->effects.step(engine, engine->getRpm(), s->redlineRpm, wear);
    }
    if (steps > 0) s->rawTorque = sum / steps;
    pumpSynthesizer(sim->synthesizer());
    sim->endFrame();

    return (int32_t)sim->synthesizer().m_audioBuffer.size();
}

ES_API int32_t es_read(es_session *s, int16_t *dst, int32_t max) {
    if (s == nullptr || dst == nullptr || max <= 0) return 0;
    Synthesizer &synth = s->simulator->synthesizer();
    const int32_t n = std::min(max, (int32_t)synth.m_audioBuffer.size());
    if (n <= 0) return 0;
    const int32_t read = synth.readAudioOutput(n, dst);
    if (read > 0) s->effects.mixInto(dst, read, s->built.engine->getRpm());
    return read;
}

ES_API void es_set_drive_mode(es_session *s, int32_t on) {
    if (s == nullptr) return;
    s->drive = on != 0;
    s->governorI = s->torque = s->alpha = 0;
    s->hunt = s->stumble = s->limiterDrop = 0;
    s->jammed = false;
    s->jamTimer = 0;
    std::fill(s->startMiss.begin(), s->startMiss.end(), 0);
}
ES_API void es_set_starter(es_session *s, int32_t on) { if (s != nullptr) s->starter = on != 0; }
ES_API void es_set_idle_rpm(es_session *s, double rpm) { if (s != nullptr) s->idleRpm = rpm; }
ES_API double es_get_torque(es_session *s) { return (s != nullptr) ? s->torque : 0.0; }
ES_API void es_set_induction(es_session *s, const es_induction_desc *desc) {
    if (s == nullptr || desc == nullptr) return;
    s->induction.configure(*desc, 0);
}
ES_API void es_get_induction(es_session *s, double *boostPsi, double *turboSpeed, double *superchargerKw) {
    if (s == nullptr) return;
    if (boostPsi != nullptr) *boostPsi = s->induction.active() ? s->induction.boostPa() / 6894.757 : 0;
    if (turboSpeed != nullptr) for (int i = 0; i < ES_MAX_TURBOS; ++i) turboSpeed[i] = s->induction.turboSpeed(i);
    if (superchargerKw != nullptr) *superchargerKw = s->induction.superchargerPowerW() / 1000;
}
ES_API int32_t es_get_blowoffs(es_session *s, double *psi) {
    if (s == nullptr) return 0;
    double pa = 0;
    const int n = s->induction.takeBlowOffs(&pa);
    if (psi != nullptr) *psi = pa / 6894.757;
    return n;
}
ES_API void es_set_fuel(es_session *s, int32_t available) { if (s != nullptr) s->fuel = available != 0; }
ES_API void es_probe_exhaust(es_session *s, int32_t cylinder, double *fuel, double *temperature, double *moles) {
    if (s == nullptr || cylinder < 0 || cylinder >= s->built.engine->getCylinderCount()) return;
    GasSystem &runner = s->built.engine->getChamber(cylinder)->m_exhaustRunnerAndPrimary;
    *fuel = runner.mix().p_fuel;
    *temperature = runner.temperature();
    *moles = runner.n();
}

ES_API void es_set_lowpass(es_session *s, double crankHz, double idleHz, double openAt, double idleRpm) {
    if (s == nullptr) return;
    s->effects.filterOn = crankHz > 0;
    s->effects.filterCrankHz = std::min(20000.0, std::max(50.0, crankHz));
    s->effects.filterIdleHz = std::min(20000.0, std::max(50.0, idleHz));
    s->effects.filterOpenAt = std::max(1.01, openAt);
    if (idleRpm > 100) s->effects.idleRpm = idleRpm;
}
ES_API void es_set_effects(es_session *s, double backfireGain, double backfireChance, double valveGain) {
    if (s == nullptr) return;
    s->effects.backfireGain = std::max(0.0, backfireGain);
    s->effects.backfireChance = std::min(1.0, std::max(0.0, backfireChance));
    s->effects.valveGain = std::max(0.0, valveGain);
}
ES_API void es_get_exhaust(es_session *s, double *flow, double *fuel, double *temperature) {
    if (s == nullptr) return;
    s->effects.takeExhaust(flow, fuel, temperature);
}
ES_API int32_t es_get_backfires(es_session *s) { return (s != nullptr) ? s->effects.takeBackfires() : 0; }
ES_API double es_get_torque_raw(es_session *s) { return (s != nullptr) ? s->rawTorque : 0.0; }

ES_API double es_get_starter_load(es_session *s) { return (s != nullptr) ? s->starterLoad : 0.0; }
ES_API int32_t es_get_starter_jammed(es_session *s) { return (s != nullptr && s->jammed) ? 1 : 0; }
ES_API void es_set_starting(es_session *s, double hardStart, int32_t starterGivesUp) {
    if (s == nullptr) return;
    s->hardStart = std::min(1.0, std::max(0.0, hardStart));
    s->giveUp = starterGivesUp != 0;
}
ES_API void es_set_heat(es_session *s, double heat) { if (s != nullptr) s->heat = std::min(1.0, std::max(0.0, heat)); }
ES_API double es_get_crank_inertia(es_session *s) { return (s != nullptr) ? s->built.crankInertia : 0.0; }

ES_API double es_get_rpm(es_session *s) { return (s != nullptr) ? s->built.engine->getRpm() : 0.0; }

ES_API double es_get_crank_speed(es_session *s) {
    return (s != nullptr) ? s->built.engine->getOutputCrankshaft()->m_body.v_theta : 0.0;
}

ES_API double es_get_processing_time_us(es_session *s) {
    return (s != nullptr) ? s->simulator->getAverageProcessingTime() : 0.0;
}

ES_API const char *es_last_error(void) { return g_lastError.c_str(); }

} // extern "C"
