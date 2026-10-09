#include "enginesim_api.h"
#include "engine_builder.h"

#include "exhaust_system.h"
#include "fuel.h"
#include "ignition_module.h"
#include "piston.h"
#include "cylinder_bank.h"
#include "cylinder_head.h"
#include "impulse_response.h"
#include "simulator.h"
#include "piston_engine_simulator.h"
#include "synthesizer.h"
#include "units.h"

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
    std::mt19937 rng;
};

// ---------------------------------------------------------------------
//  Wear. w = 1 - condition, scaled by the wear intensity setting.
//    blow-by   : x(1 + 30 w^2), per-cylinder weakness, one bad cylinder x2
//    combustion: burn-to-burn variation 0.5 -> 1.0, efficiency -0.2 at w = 1
//    misfires  : from 70 % condition down, in streaks of 1-4 firing events
//                (a stumble you hear, rather than scattered roughness); share of
//                events misfiring ~25 % at 50 %, ~40 % at 40 %, up to 60 % (more and
//                it stops sounding like a stumble), more at idle / light throttle,
//                bad cylinder x2
//    stumble   : each misfire streak drags the held rpm down (recovering over ~150 ms):
//                the dyno holds the game's rpm, so without this a misfire is only a
//                missing pulse, never the crank losing speed
//    hunting   : the held rpm wanders up to +-12 % at idle when badly worn
//    dead      : 4+ cylinders lose whole cylinders, the weakest first: one under 50 %
//                condition, two under 30 %, and on 6+ cylinders a third under 10 %.
//                A dead cylinder has no spark and contributes nothing to the sound
//                (its weight in the synthesizer's exhaust sum is 0); it still turns
//                with the crank
// ---------------------------------------------------------------------
static void initWear(es_session *s) {
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

static void applyWear(es_session *s, double dt) {
    Engine *engine = s->built.engine;
    const double w = effectiveWear(s);
    const int n = engine->getCylinderCount();

    if (w != s->appliedCondition) {
        const double c = 1.0 - w;
        s->deadCount = (n < 4) ? 0 : (c < 0.10 && n >= 6) ? 3 : (c < 0.30) ? 2 : (c < 0.50) ? 1 : 0;
        for (int i = 0; i < n; ++i) {
            engine->getPiston(i)->setBlowbyK(s->baseBlowby[i] * (1 + 30 * w * w * s->weakness[i]));
        }
        for (int i = 0; i < n; ++i) {
            const bool dead = std::find(s->failOrder.begin(), s->failOrder.begin() + s->deadCount, i) != s->failOrder.begin() + s->deadCount;
            Piston *piston = engine->getPiston(i);
            engine->getHead(piston->getCylinderBank()->getIndex())->setSoundAttenuation(
                piston->getCylinderIndex(), dead ? 0.0 : s->baseSound[i]);
        }
        engine->getFuel()->setBurningEfficiencyRandomness(std::min(1.0, s->baseRandomness + 0.5 * w));
        engine->getFuel()->setMaxBurningEfficiency(s->baseEfficiency - 0.2 * w);
        s->appliedCondition = w;
    }

    const double x = wearSeverity(s);
    const double rpm = std::max(300.0, engine->getRpm());
    const double eventPeriod = 120.0 / rpm;                 // one firing per cylinder per two turns
    const double events = dt / eventPeriod;                 // firing events per cylinder this frame
    const double lightLoad = 1.0 + 0.5 * (1.0 - s->throttle);   // worse at idle / light throttle
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
            const double share = std::min(0.6, 1.0 * std::pow(x, 1.2) * lightLoad * std::min(2.0, s->weakness[i]));
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
        engine->getIgnitionModule()->setPlugEnabled(i, !dead);
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

    s->simulator->m_dyno.m_hold = true;
    engine->getIgnitionModule()->m_enabled = true;
    initWear(s);
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

ES_API int32_t es_update(es_session *s, double dt) {
    if (s == nullptr) return 0;
    Simulator *sim = s->simulator;
    Engine *engine = s->built.engine;
    dt = std::min(std::max(dt, 1 / 1000.0), 1 / 20.0);

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
    while (sim->simulateStep()) { /* void */ }
    pumpSynthesizer(sim->synthesizer());
    sim->endFrame();

    return (int32_t)sim->synthesizer().m_audioBuffer.size();
}

ES_API int32_t es_read(es_session *s, int16_t *dst, int32_t max) {
    if (s == nullptr || dst == nullptr || max <= 0) return 0;
    Synthesizer &synth = s->simulator->synthesizer();
    const int32_t n = std::min(max, (int32_t)synth.m_audioBuffer.size());
    if (n <= 0) return 0;
    return synth.readAudioOutput(n, dst);
}

ES_API double es_get_rpm(es_session *s) { return (s != nullptr) ? s->built.engine->getRpm() : 0.0; }

ES_API double es_get_crank_speed(es_session *s) {
    return (s != nullptr) ? s->built.engine->getOutputCrankshaft()->m_body.v_theta : 0.0;
}

ES_API double es_get_processing_time_us(es_session *s) {
    return (s != nullptr) ? s->simulator->getAverageProcessingTime() : 0.0;
}

ES_API const char *es_last_error(void) { return g_lastError.c_str(); }

} // extern "C"
