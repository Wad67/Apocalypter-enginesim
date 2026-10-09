#ifndef ENGINESIM_EFFECTS_H
#define ENGINESIM_EFFECTS_H

#include "enginesim_api.h"

#include <cstdint>
#include <random>
#include <vector>

class Engine;

// Sound engine-sim doesn't make, added to its output without touching the simulation: it only
// reads the simulation's state (crank angle, exhaust gas), and its sounds are mixed into the
// synthesizer's output after the leveler, so they never change how loud the engine itself is.
//
//   backfires : detected only (the game plays them, at the exhaust): unburnt fuel reaching a hot
//               exhaust runner (spark cut at the limiter, misfires, lift-off), at most one per
//               exhaust every 0.35 s
//   valve tick: a click as each valve closes, from the cam timing against the crank angle;
//               louder with rpm and with wear (valve lash)
//
// Also the low-rpm filter: a low-pass on the synthesizer's output (not on the effects), against
// the gas model's 1-3 kHz ringing at very low engine speeds.
class Effects {
public:
    // after the engine is built
    void initialize(Engine *engine, const es_engine_params &params, double simulationFrequency);

    // after every simulation step; wear 0..1, the engine's speed and rev limit for the tick level
    void step(Engine *engine, double rpm, double redlineRpm, double wear);

    // the synthesizer's output, filtered, with the effects mixed in (dst: samples just read)
    void mixInto(int16_t *dst, int n, double rpm);

    int takeBackfires() { int n = m_backfires; m_backfires = 0; return n; }

    // exhaust since the last call: flow (mol/s out of the cylinders), its fuel fraction (flow
    // weighted) and the runners' mean temperature (K)
    void takeExhaust(double *flow, double *fuel, double *temperature);

    double backfireGain = 1.0, backfireChance = 0.35, valveGain = 1.0;
    bool filterOn = true;
    double filterCrankHz = 400, filterIdleHz = 2500, filterOpenAt = 1.5, idleRpm = 800;

private:
    struct Valve { double angle; int variant; float level; };
    void add(int64_t at, const std::vector<float> &kernel, float gain);
    void updateFilter(double rpm, int n);
    float filter(float x);

    std::vector<std::vector<float>> m_clicks;
    std::vector<Valve> m_valves;
    std::vector<int> m_exhaustOf;                          // each cylinder's exhaust system
    std::vector<bool> m_slug;
    std::vector<double> m_cooldown, m_exhaustCooldown;
    double m_lastCycle = -1, m_simFrequency = 10000;
    uint64_t m_steps = 0;
    int m_backfires = 0, m_exhaustCount = 1;
    double m_flowSum = 0, m_fuelSum = 0, m_tempSum = 0;
    uint64_t m_exhaustSteps = 0;
    std::mt19937 m_rng{ 1234 };

    std::vector<float> m_out;    // effects, from audio sample m_outBase on
    int64_t m_outBase = 0, m_readPos = 0;

    double m_cutoff = -1;
    float m_b0 = 1, m_b1 = 0, m_b2 = 0, m_a1 = 0, m_a2 = 0;
    float m_x1 = 0, m_x2 = 0, m_y1 = 0, m_y2 = 0, m_u1 = 0, m_u2 = 0, m_w1 = 0, m_w2 = 0;
};

#endif /* ENGINESIM_EFFECTS_H */
