#include "induction.h"

#include "combustion_chamber.h"
#include "engine.h"
#include "intake.h"

#include <algorithm>
#include <cmath>

namespace {

const double kPsi = 6894.757;
const double kAirM = 0.02897;                 // kg/mol
const double kAirR = 287.05;                  // J/(kg K)
const double kChargeT = 298.15;               // K: no heating of the charge (as engine-sim's intake)
const double kCpAir = 1005, kCpExhaust = 1150;
const double kTwoPi = 6.283185307179586;

double clamp01(double x) { return std::min(1.0, std::max(0.0, x)); }

} // namespace

// Sizes are relative (1 = a medium unit), as the part table gives them:
//   supercharger: 2 L per crank revolution (a 1.5 L/rev blower geared up ~1.3x): on a 2.8 L engine
//                 about twice what it swallows, about 0.6 bar if nothing held it back
//   turbo       : compressor wheel 50 mm (radius 25 mm x size), tip speed up to 520 m/s (pressure
//                 ratio ~4.5 at full speed); choke flow 0.22 kg/s x size^2 (~300 hp of air); turbine
//                 swallowing 0.15 kg/s x size^2 at an expansion ratio of 2.6; rotor inertia
//                 3e-5 kg m2 x size^4, spinning down free in a few seconds
void Induction::configure(const es_induction_desc &d, double displacementM3) {
    (void)displacementM3;
    m_superchargerSize = std::max(0.0, (double)d.superchargerSize);
    m_scDisplacement = 2.0e-3 * m_superchargerSize;
    m_scLeak = 6.7e-5 * m_superchargerSize;
    m_scRelief = std::max(0.0, (double)d.superchargerReliefPsi) * kPsi;   // across the blower
    m_turbos = std::min(std::max(0, (int)d.turbos), ES_MAX_TURBOS);
    double total = 0;
    for (int i = 0; i < m_turbos; ++i) {
        Turbo &t = m_turbo[i];
        const double s = std::min(3.0, std::max(0.2, (double)d.turboSize[i]));
        const double omega = t.omega;   // a reconfigured turbo keeps spinning
        t = Turbo();
        t.size = s;
        t.radius = 0.025 * s;
        t.omegaMax = 520.0 / t.radius;
        t.inertia = 3.0e-5 * std::pow(s, 4);
        t.chokeFlow = 0.22 * s * s;
        t.turbineFlow = 0.15 * s * s;
        t.omega = std::min(omega, t.omegaMax);
        total += s;
    }
    m_wastegate = kAmbient + std::max(0.0, (double)d.wastegatePsi) * kPsi;
    // the blow-off valve passes what the turbos can blow (their choke flow) with 0.5 bar across it
    m_blowOffFlow = 0;
    for (int i = 0; i < m_turbos; ++i) m_blowOffFlow += m_turbo[i].chokeFlow;
    m_v1 = 0.0015 + 0.001 * total;                  // turbo outlets and their pipes
    m_v2 = 0.0015 + 0.0015 * m_superchargerSize;    // blower outlet, to the throttle
    if (!active()) m_p1 = m_p2 = kAmbient;
}

double Induction::compressorPr(const Turbo &t) const {
    const double u = t.omega * t.radius;
    return std::pow(1 + 0.6 * u * u / (kCpAir * kChargeT), 3.5);
}

double Induction::step(Engine *engine, double crankSpeed, double throttle, double dt) {
    if (!active() || dt <= 0) { m_scPower = 0; return 0; }

    // what the engine drew through its intakes this step, kg/s
    double drawn = 0;
    for (int i = 0; i < engine->getIntakeCount(); ++i) drawn += engine->getIntake(i)->m_flow;
    const double engineFlow = drawn * kAirM / dt;

    // what it pushed out of its cylinders, kg/s, and how hot (the runners' mean temperature)
    double exhaust = 0, temperature = 0;
    const int n = engine->getCylinderCount();
    for (int i = 0; i < n; ++i) {
        CombustionChamber *c = engine->getChamber(i);
        exhaust += std::max(0.0, c->getLastTimestepExhaustFlow());
        temperature += c->m_exhaustRunnerAndPrimary.temperature();
    }
    const double exhaustFlow = exhaust * 0.0285 / dt;
    temperature = std::max(300.0, temperature / std::max(1, n));

    const bool sc = m_superchargerSize > 0, turbo = m_turbos > 0;
    // The blow-off and bypass valves are worked by the pressure across the throttle: boost in the pipes
    // against vacuum in the manifold once the throttle shuts (the manifold empties as the engine pumps
    // it down, so they lift a moment after the throttle, not with it). They feel it through a vacuum
    // hose on a diaphragm (~80 ms), and the throttle plate itself shuts over a few tens of ms. Blow-off:
    // spring set to lift at 5 psi across, full open 1.5 psi on; the blower's bypass at 3 psi.
    (void)throttle;
    double manifold = 0;
    for (int i = 0; i < engine->getIntakeCount(); ++i) manifold += engine->getIntake(i)->m_system.pressure();
    manifold /= std::max(1, engine->getIntakeCount());
    m_across += (m_p2 - manifold - m_across) * std::min(1.0, dt / 0.08);
    const double across = m_across;
    const double closed = clamp01((across - 3.0 * kPsi) / (1.5 * kPsi));   // the blower's bypass
    const double bov = clamp01((across - 5.0 * kPsi) / (1.5 * kPsi));

    // the turbos' blow-off valve lifting (an event, for its sound); it reseats once it has shut again
    if (m_turbos > 0) {
        if (!m_venting && bov > 0.5) {
            m_venting = true;
            ++m_blowOffs;
            m_blowOffPa = std::max(0.0, m_p1 - kAmbient);
        }
        else if (m_venting && bov <= 0) m_venting = false;
    }

    // turbos: each its share of the exhaust (by turbine size), less what the wastegate lets past
    double compressed = 0;
    if (turbo) {
        // the wastegate holds the turbos' own outlet pressure
        const double gate = clamp01((m_p1 - m_wastegate) / 7000.0);
        double sizes = 0, back = 0;
        for (int i = 0; i < m_turbos; ++i) sizes += m_turbo[i].size * m_turbo[i].size;
        const double pr = std::max(1.0, m_p1 / kAmbient);
        for (int i = 0; i < m_turbos; ++i) {
            Turbo &t = m_turbo[i];
            const double share = t.size * t.size / sizes;
            // turbine: expansion across it rises with the flow forced through it
            const double through = exhaustFlow * share * (1 - gate);
            const double expansion = std::min(4.0, 1 + 1.6 * std::pow(through / t.turbineFlow, 2));
            const double turbinePower = 0.65 * through * kCpExhaust * temperature * (1 - std::pow(expansion, -0.2593));
            back += share * expansion;
            // compressor: the pressure its tip speed holds, the flow its size passes
            const double prMax = compressorPr(t);
            const double u = t.omega * t.radius, uMax = t.omegaMax * t.radius;
            const double headroom = prMax > 1.0001 ? std::min(1.2, std::sqrt(std::max(0.0, (prMax - pr) / (prMax - 1)))) : 0.0;
            const double flow = t.chokeFlow * (u / uMax) * headroom;
            const double compressorPower = flow * kCpAir * kChargeT * (std::pow(pr, 0.2857) - 1) / 0.7;
            const double bearing = t.inertia / 2.5 * t.omega * t.omega;
            t.omega += (turbinePower - compressorPower - bearing) / (t.inertia * std::max(t.omega, 500.0)) * dt;
            t.omega = std::min(t.omegaMax * 1.05, std::max(0.0, t.omega));
            compressed += flow;
        }
        // the back pressure the turbines hold the exhaust at (the open wastegate's passage relieves it),
        // eased over ~20 ms: the exhaust manifold's volume
        const double target = kAmbient * (1 + (back - 1) * (1 - 0.7 * gate));
        m_backPressure += (target - m_backPressure) * std::min(1.0, dt / 0.02);
    }
    else m_backPressure = kAmbient;

    // supercharger: its volume per crank revolution, less the leak back past its rotors, at the density
    // it draws from; its bypass opens at a closed throttle, its relief valve at its set pressure
    double blown = 0, bypass = 0;
    m_scPower = 0;
    double drag = 0;
    if (sc) {
        const double rho1 = m_p1 / (kAirR * kChargeT);
        const double rev = std::max(0.0, crankSpeed) / kTwoPi;
        // leak: back past the rotors under boost, through them when the engine pulls harder than it blows
        const double rise = m_p2 - m_p1;
        const double leak = m_scLeak * (rise >= 0 ? std::sqrt(rise) : -std::sqrt(-rise));
        blown = rho1 * (m_scDisplacement * rev - leak);
        // its bypass / relief valve back to its inlet: an orifice passing the blower's whole flow at its top speed
        // (6000 rpm) with 5 kPa across it, opening at a closed throttle and over its set pressure
        // relief: the rise across the blower (fed by turbos, it adds its rise to theirs)
        const double open = std::max(closed, clamp01((rise - m_scRelief) / 5000.0));
        const double rho2 = m_p2 / (kAirR * kChargeT);
        const double valve = rho2 * m_scDisplacement * 100.0 * std::sqrt(std::fabs(rise) / 5000.0);
        bypass = open * (rise >= 0 ? valve : -valve);
        // pushing its whole swept volume against the pressure across it (a Roots blower compresses
        // against the outlet, not inside), through its gears and belt
        m_scPower = m_scDisplacement * rev * std::max(0.0, rise) / 0.9;
        drag = crankSpeed > 1 ? m_scPower / crankSpeed : 0;
    }

    // the charge pipes
    const double toPa1 = kAirR * kChargeT / m_v1, toPa2 = kAirR * kChargeT / m_v2;
    if (turbo && sc) {
        // between them: the turbos' blow-off valve, and air drawn through the turbos as through an open
        // pipe when the blower pulls it under ambient
        const double blowOff = bov * m_blowOffFlow * std::sqrt(std::max(0.0, m_p1 - kAmbient) / 50000.0);
        const double open = 5.0e-6 * std::max(0.0, kAmbient - m_p1);
        m_p1 += (compressed + open - blown + bypass - blowOff) * dt * toPa1;
        m_p2 += (blown - bypass - engineFlow) * dt * toPa2;
    }
    else if (turbo) {
        // one volume, its blow-off valve venting at a closed throttle; under ambient it draws through
        // the compressor as through an open pipe
        const double blowOff = bov * m_blowOffFlow * std::sqrt(std::max(0.0, m_p1 - kAmbient) / 50000.0);
        const double open = 5.0e-6 * std::max(0.0, kAmbient - m_p1);
        m_p1 += (compressed + open - blowOff - engineFlow) * dt * kAirR * kChargeT / (m_v1 + m_v2);
        m_p2 = m_p1;
    }
    else {
        m_p1 = kAmbient;
        m_p2 += (blown - bypass - engineFlow) * dt * toPa2;
    }
    m_p1 = std::max(0.3 * kAmbient, m_p1);
    m_p2 = std::max(0.3 * kAmbient, m_p2);
    return drag;
}
