#ifndef ENGINESIM_INDUCTION_H
#define ENGINESIM_INDUCTION_H

#include "enginesim_api.h"

class Engine;

// Forced induction as machines on the simulated engine (drive mode), instead of a pressure handed in:
// what the compressors deliver against what the engine draws sets the boost.
//
//   supercharger: a positive-displacement (Roots) blower on the crank. It moves a fixed volume per
//                 crank revolution (its size), less what leaks back past its rotors with the pressure
//                 across it, and takes that volume times the pressure it pushes against off the crank.
//                 Its bypass valve opens at a closed throttle and its relief valve at its set pressure.
//                 A big blower on a small engine pushes hard, and the engine pays for every bit of it.
//   turbo       : a shaft with a turbine in the exhaust and a centrifugal compressor on the intake. The
//                 turbine takes power from the engine's own exhaust (mass flow and temperature, as
//                 simulated), the compressor's pressure is set by its tip speed and its flow by its
//                 size; the shaft speeds up or slows on the difference. Its wastegate bypasses the
//                 turbine at its set pressure. A big turbo on a small engine barely spools.
//
// The charge pipes are volumes whose pressure follows the mass in them. With both, the turbos feed the
// supercharger (twincharged, in series): the wastegate holds the turbos' outlet at its pressure and the
// blower's relief valve its rise above that, so together they make about the sum of the two. The
// engine's intake breathes the last volume's pressure.
class Induction {
public:
    void configure(const es_induction_desc &desc, double displacementM3);
    bool active() const { return m_superchargerSize > 0 || m_turbos > 0; }

    // before a simulation step: the pressure the engine's intake breathes, Pa
    double inletPressure() const { return m_p2; }
    // after it: the engine's crank speed (rad/s), its throttle, and the air it drew and exhaust it
    // pushed out this step (mol); returns the supercharger's drag on the crank, N m
    double step(Engine *engine, double crankSpeed, double throttle, double dt);

    double boostPa() const { return m_p2 - kAmbient; }
    double turboSpeed(int i) const { return i < m_turbos ? m_turbo[i].omega / m_turbo[i].omegaMax : 0; }
    double superchargerPowerW() const { return m_scPower; }
    // what the engine's exhaust flows out against: the turbines' back pressure (ambient without)
    double exhaustPressure() const { return m_backPressure; }
    // blow-off valve openings since the last call (the throttle shut on boost), and the boost (Pa) of
    // the last one
    int takeBlowOffs(double *pa) { int n = m_blowOffs; m_blowOffs = 0; if (pa) *pa = m_blowOffPa; return n; }

    static constexpr double kAmbient = 101325.0;

private:
    struct Turbo {
        double size = 1, omega = 0, omegaMax = 0, radius = 0, inertia = 0, chokeFlow = 0, turbineFlow = 0;
    };

    double compressorPr(const Turbo &t) const;

    double m_superchargerSize = 0, m_scDisplacement = 0, m_scRelief = 0, m_scLeak = 0;
    Turbo m_turbo[ES_MAX_TURBOS];
    int m_turbos = 0;
    double m_wastegate = 0;
    double m_v1 = 0.002, m_v2 = 0.002;           // charge pipe volumes, m3
    double m_p1 = kAmbient, m_p2 = kAmbient;     // after the turbos, after the supercharger, Pa
    double m_scPower = 0;
    double m_backPressure = kAmbient;            // before the turbines, Pa
    bool m_venting = false;
    double m_across = 0;
    double m_blowOffFlow = 0;                    // kg/s at 0.5 bar                         // the pressure across the throttle the valves feel, Pa
    int m_blowOffs = 0;
    double m_blowOffPa = 0;
};

#endif /* ENGINESIM_INDUCTION_H */
