#include "effects.h"

#include "combustion_chamber.h"
#include "constants.h"
#include "crankshaft.h"
#include "cylinder_bank.h"
#include "cylinder_head.h"
#include "engine.h"
#include "exhaust_system.h"
#include "piston.h"

#include <algorithm>
#include <cmath>

namespace {

const double kRate = ES_SAMPLE_RATE;
const double kFourPi = 4 * constants::pi;

// a valve seating: a short metallic ring, a few kHz, decaying in about a millisecond
std::vector<float> click(std::mt19937 &rng, double frequency) {
    std::uniform_real_distribution<float> noise(-1, 1);
    std::vector<float> k((size_t)(0.006 * kRate));
    for (size_t i = 0; i < k.size(); ++i) {
        const double t = i / kRate;
        k[i] = (float)(std::exp(-t / 0.0007) * (std::sin(2 * constants::pi * frequency * t) + 0.3 * noise(rng)));
    }
    return k;
}

} // namespace

void Effects::initialize(Engine *engine, const es_engine_params &p, double simulationFrequency) {
    m_simFrequency = simulationFrequency;
    const int n = engine->getCylinderCount();
    m_exhaustCount = std::max(1, engine->getExhaustSystemCount());

    for (int v = 0; v < 4; ++v) m_clicks.push_back(click(m_rng, 3500 + 1200 * v));

    // valve closing angles in the 720 degree cycle (cycle angle 0 = cylinder 1 firing): the closing
    // flank of each lobe, its centre plus half its duration, and a little more to the seat
    std::uniform_real_distribution<float> level(0.7f, 1.3f);
    for (int c = 0; c < n; ++c) {
        const double fire = p.firingAngleDeg[c];
        const double ivc = fire + 360 + p.intakeLobeCenterDeg + p.intakeCamDurationDeg / 2 + 10;
        const double evc = fire + 360 - p.exhaustLobeCenterDeg + p.exhaustCamDurationDeg / 2 + 10;
        for (double a : { ivc, evc }) {
            Valve v;
            v.angle = std::fmod(std::fmod(a, 720.0) + 720.0, 720.0) * constants::pi / 180;
            v.variant = (int)(m_rng() % m_clicks.size());
            v.level = level(m_rng);
            m_valves.push_back(v);
        }
    }

    for (int i = 0; i < n; ++i) {
        Piston *piston = engine->getPiston(i);
        CylinderHead *head = engine->getHead(piston->getCylinderBank()->getIndex());
        m_exhaustOf.push_back(head->getExhaustSystem(piston->getCylinderIndex())->getIndex());
    }
    m_slug.assign(n, false);
    m_cooldown.assign(n, 0.0);
    m_exhaustCooldown.assign(m_exhaustCount, 0.0);
}

void Effects::add(int64_t at, const std::vector<float> &kernel, float gain) {
    if (at < m_readPos) at = m_readPos;
    const int64_t end = at + (int64_t)kernel.size() - m_outBase;
    if (end > (int64_t)m_out.size()) m_out.resize((size_t)end, 0.0f);
    float *out = m_out.data() + (at - m_outBase);
    for (size_t i = 0; i < kernel.size(); ++i) out[i] += kernel[i] * gain;
}

void Effects::step(Engine *engine, double rpm, double redlineRpm, double wear) {
    ++m_steps;
    const double dt = 1.0 / m_simFrequency;
    const int64_t at = (int64_t)(m_steps * kRate / m_simFrequency);   // this step in the audio stream
    std::uniform_real_distribution<double> roll(0.0, 1.0);

    // valve ticks: valves whose closing angle the crank passed this step
    const double cycle = engine->getOutputCrankshaft()->getCycleAngle();
    if (valveGain > 0 && m_lastCycle >= 0 && rpm > 60) {
        double from = m_lastCycle, to = cycle;
        if (to < from) to += kFourPi;
        if (to - from < 0.5) {   // not a jump (session start, reversal)
            const float level = (float)(valveGain * 1100 * (0.25 + 0.75 * std::min(1.0, rpm / std::max(1000.0, redlineRpm)))
                * (1 + 2 * wear));
            for (const Valve &v : m_valves) {
                double a = v.angle;
                if (a <= from) a += kFourPi;
                if (a <= to) add(at, m_clicks[v.variant], level * v.level);
            }
        }
    }
    m_lastCycle = cycle;

    // exhaust, for the game's smoke
    {
        const int n = engine->getCylinderCount();
        double flow = 0, fuel = 0, temperature = 0;
        for (int i = 0; i < n; ++i) {
            CombustionChamber *chamber = engine->getChamber(i);
            const double f = std::max(0.0, chamber->getLastTimestepExhaustFlow());
            flow += f;
            fuel += f * chamber->m_exhaustRunnerAndPrimary.mix().p_fuel;
            temperature += chamber->m_exhaustRunnerAndPrimary.temperature();
        }
        m_flowSum += flow;
        m_fuelSum += fuel;
        m_tempSum += temperature / std::max(1, n);
        ++m_exhaustSteps;
    }

    // backfires: an unburnt slug arriving in a hot runner (an idling exhaust runs 650 - 750 K, under
    // load ~2500 K). A slug is fuel well above what a burn leaves there: in a cool, idling runner
    // (under 1100 K) a fuel fraction over 0.0065 (a misfire, its small charge diluted in the runner;
    // a healthy idle stays under 0.006), under load over 0.014 (a spark cut or misfire; a burn leaves
    // ~0.007). At most one backfire per exhaust per 0.35 s: distinct bangs, not a rattle.
    if (backfireGain > 0) {
        for (double &c : m_exhaustCooldown) c = std::max(0.0, c - dt);
        const int n = engine->getCylinderCount();
        for (int i = 0; i < n; ++i) {
            m_cooldown[i] = std::max(0.0, m_cooldown[i] - dt);
            GasSystem &runner = engine->getChamber(i)->m_exhaustRunnerAndPrimary;
            const double fuel = runner.mix().p_fuel;
            const double temperature = runner.temperature();
            const double threshold = (temperature < 1100) ? 0.0065 : 0.014;
            const bool slug = fuel > threshold && temperature > 600;
            const int e = std::min(m_exhaustCount - 1, m_exhaustOf[i]);
            if (slug && !m_slug[i] && m_cooldown[i] <= 0 && m_exhaustCooldown[e] <= 0 && roll(m_rng) < backfireChance) {
                m_cooldown[i] = 0.35;
                m_exhaustCooldown[e] = 0.35;
                ++m_backfires;
            }
            m_slug[i] = slug;
        }
    }
}

void Effects::takeExhaust(double *flow, double *fuel, double *temperature) {
    const double seconds = m_exhaustSteps / m_simFrequency;
    *flow = seconds > 0 ? m_flowSum / seconds : 0;
    *fuel = m_flowSum > 1e-12 ? m_fuelSum / m_flowSum : 0;
    *temperature = m_exhaustSteps > 0 ? m_tempSum / m_exhaustSteps : 0;
    m_flowSum = m_fuelSum = m_tempSum = 0;
    m_exhaustSteps = 0;
}

void Effects::updateFilter(double rpm, int n) {
    const double x = rpm / std::max(100.0, idleRpm);
    const double open = std::max(1.01, filterOpenAt);
    double target = x < 1
        ? filterCrankHz * std::pow(filterIdleHz / filterCrankHz, std::min(1.0, std::max(0.0, (x - 0.2) / 0.8)))
        : filterIdleHz * std::pow(20000.0 / filterIdleHz, std::min(1.0, (x - 1) / (open - 1)));
    target = std::min(target, 0.45 * kRate);
    // glide over ~50 ms
    m_cutoff = (m_cutoff < 0) ? target : m_cutoff + (target - m_cutoff) * std::min(1.0, n / (0.05 * kRate));
    const double w0 = 2 * constants::pi * m_cutoff / kRate;
    const double cs = std::cos(w0), alpha = std::sin(w0) / (2 * 0.70710678), a0 = 1 + alpha;
    m_b0 = (float)((1 - cs) / 2 / a0); m_b1 = (float)((1 - cs) / a0); m_b2 = m_b0;
    m_a1 = (float)(-2 * cs / a0); m_a2 = (float)((1 - alpha) / a0);
}

float Effects::filter(float x) {
    const float y = m_b0 * x + m_b1 * m_x1 + m_b2 * m_x2 - m_a1 * m_y1 - m_a2 * m_y2;
    m_x2 = m_x1; m_x1 = x; m_y2 = m_y1; m_y1 = y;
    const float z = m_b0 * y + m_b1 * m_u1 + m_b2 * m_u2 - m_a1 * m_w1 - m_a2 * m_w2;
    m_u2 = m_u1; m_u1 = y; m_w2 = m_w1; m_w1 = z;
    return z;
}

void Effects::mixInto(int16_t *dst, int n, double rpm) {
    if (filterOn) updateFilter(rpm, n);
    for (int i = 0; i < n; ++i) {
        float v = filterOn ? filter((float)dst[i]) : (float)dst[i];
        const int64_t k = m_readPos + i - m_outBase;
        if (k >= 0 && k < (int64_t)m_out.size()) v += m_out[(size_t)k];
        dst[i] = (int16_t)std::max(-32768.0f, std::min(32767.0f, v));
    }
    m_readPos += n;
    // drop what has been played
    const int64_t played = m_readPos - m_outBase;
    if (played > 8192) {
        const size_t drop = (size_t)std::min<int64_t>(played, (int64_t)m_out.size());
        m_out.erase(m_out.begin(), m_out.begin() + drop);
        m_outBase += (int64_t)drop;
    }
}
