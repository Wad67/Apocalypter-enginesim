#include "engine_builder.h"

#include "camshaft.h"
#include "combustion_chamber.h"
#include "connecting_rod.h"
#include "constants.h"
#include "crankshaft.h"
#include "cylinder_bank.h"
#include "cylinder_head.h"
#include "direct_throttle_linkage.h"
#include "exhaust_system.h"
#include "fuel.h"
#include "function.h"
#include "gas_system.h"
#include "ignition_module.h"
#include "impulse_response.h"
#include "intake.h"
#include "piston.h"
#include "standard_valvetrain.h"
#include "units.h"

#include <algorithm>
#include <cmath>
#include <cstring>

namespace {

// ---------------------------------------------------------------------
//  Layout: bank angles, and for each cylinder its bank and firing angle
//  (crank degrees within the 720 degree cycle). Everything else (crank
//  journal angles, cam lobe centrelines, crank TDC) follows from these:
//    journal  = fire + (bankAngle[bank] - bankAngle[0])     (mod 360)
//    TDC      = 90 deg + bankAngle[0]
//    lobes    = 360 -/+ lobe centre + fire                  (exhaust / intake)
// ---------------------------------------------------------------------
struct Layout {
    std::vector<double> bankAngle;
    std::vector<int> bank;
    std::vector<double> fire;
};

std::vector<double> bankAngles(int layout, double vAngle) {
    if (layout == ES_LAYOUT_INLINE) return { 0.0 };
    if (layout == ES_LAYOUT_BOXER) return { 90.0, -90.0 };
    const double v = (vAngle > 0) ? vAngle : 90.0;   // V: bank 0 on the left
    return { -v / 2, v / 2 };
}

Layout evenFiring(int n, std::vector<double> banks) {
    Layout l;
    l.bankAngle = banks;
    const int count = (int)banks.size();
    for (int i = 0; i < n; ++i) {
        l.bank.push_back(i % count);
        l.fire.push_back(i * 720.0 / n);
    }
    return l;
}

// The usual firing order of a layout (engine-sim's scripts where it has one).
Layout defaultLayout(int n, int layout, double vAngle) {
    if (layout == ES_LAYOUT_INLINE || n == 1) {
        Layout l;
        l.bankAngle = bankAngles(ES_LAYOUT_INLINE, 0);
        switch (n) {
        case 1: l.fire = { 0 }; break;
        case 2: l.fire = { 0, 360 }; break;                               // 360 deg parallel twin
        case 3: l.fire = { 0, 480, 240 }; break;                          // 1-3-2
        case 4: l.fire = { 0, 540, 180, 360 }; break;                     // 1-3-4-2
        case 5: l.fire = { 0, 144, 576, 288, 432 }; break;                // 1-2-4-5-3 (Audi i5 script)
        case 6: l.fire = { 0, 480, 240, 600, 120, 360 }; break;           // 1-5-3-6-2-4 (2JZ script)
        case 8: l.fire = { 0, 180, 450, 630, 270, 90, 540, 360 }; break;  // 1-6-2-5-8-3-7-4
        default: return evenFiring(n, l.bankAngle);
        }
        l.bank.assign(n, 0);
        return l;
    }

    const std::vector<double> banks = bankAngles(layout, vAngle);
    if (layout == ES_LAYOUT_BOXER) {
        if (n == 4) {
            // Subaru EJ25 script: bank 0 = cylinders 1, 3; bank 1 = 2, 4; order 1-3-2-4
            Layout l;
            l.bankAngle = banks;
            l.bank = { 0, 1, 0, 1 };
            l.fire = { 0, 360, 180, 540 };
            return l;
        }
        return evenFiring(n, banks);
    }

    const double v = (vAngle > 0) ? vAngle : 90.0;
    if (n == 2) {
        // shared crank pin (Harley / Ducati style): second cylinder fires 360 - V after the first
        Layout l;
        l.bankAngle = banks;
        l.bank = { 0, 1 };
        l.fire = { 0, 360 - v };
        return l;
    }
    if (n == 6) {
        // 60-degree V6 script: odd cylinders bank 0, even bank 1, 1-2-3-4-5-6
        Layout l;
        l.bankAngle = banks;
        l.bank = { 0, 1, 0, 1, 0, 1 };
        l.fire = { 0, 120, 240, 360, 480, 600 };
        return l;
    }
    if (n == 8) {
        // GM LS script (cross-plane): odd cylinders bank 0, even bank 1, 1-8-7-2-6-5-4-3
        Layout l;
        l.bankAngle = banks;
        l.bank = { 0, 1, 0, 1, 0, 1, 0, 1 };
        l.fire = { 0, 270, 630, 540, 450, 360, 180, 90 };
        return l;
    }
    return evenFiring(n, banks);
}

// global cylinder order is bank-major; each slot's cylinder number - 1
std::vector<int> bankMajorOrder(int n, int bankCount, const int32_t *bank) {
    std::vector<int> order;
    for (int b = 0; b < bankCount; ++b) {
        for (int c = 0; c < n; ++c) if (bank[c] == b) order.push_back(c);
    }
    return order;
}

double wrap360(double a) {
    a = std::fmod(a, 360.0);
    return (a < 0) ? a + 360.0 : a;
}

Function *newFunction(BuiltEngine *out, double filterRadius) {
    Function *f = new Function;
    out->functions.push_back(f);
    f->initialize(1, filterRadius);
    return f;
}

// actions.h GenerateHarmonicCamLobeNode
Function *harmonicCamLobe(BuiltEngine *out, double durationAt50Thou, double gamma, double lift, int steps) {
    Function *f = newFunction(out, 1.0);   // objects.mr function() default filter radius
    const double angle = durationAt50Thou / 4;
    const double s = std::pow(2 * units::distance(50, units::thou) / lift, 1 / gamma) - 1;
    const double k = std::acos(s) / angle;
    const double extents = constants::pi / k;
    const double step = extents / (steps - 5.0);
    for (int i = 0; i < steps; ++i) {
        if (i == 0) {
            f->addSample(0.0, lift);
        }
        else {
            const double x = i * step;
            const double y = (x >= extents) ? 0.0 : lift * std::pow(0.5 + 0.5 * std::cos(k * x), gamma);
            f->addSample(x, y);
            f->addSample(-x, y);
        }
    }
    return f;
}

// heads.mr add_flow_sample: lift in thou, flow in cfm at 28 inH2O
Function *portFlow(BuiltEngine *out, const float *cfm, int count, double scale) {
    Function *f = newFunction(out, units::distance(50, units::thou));
    for (int i = 0; i < count; ++i) {
        f->addSample(i * 50 * units::thou, GasSystem::k_28inH2O(cfm[i] * scale));
    }
    return f;
}

double diskMoment(double mass, double radius) { return 0.5 * mass * radius * radius; }
double circleArea(double radius) { return constants::pi * radius * radius; }

const double FtLb = 1.3558179483;   // Nm
const double Inch = 25.4;           // mm

} // namespace

// ---------------------------------------------------------------------
//  Defaults: generic parameters from an engine's five basic values, for the
//  keys an engines.cfg section leaves out.
// ---------------------------------------------------------------------
bool defaultParams(int cylinders, int layout, float bankAngleDeg, float displacementL, int diesel, es_engine_params *out) {
    if (out == nullptr || cylinders < 1 || cylinders > ES_MAX_CYLINDERS) return false;
    if (!(displacementL > 0.02f && displacementL < 30.0f)) return false;
    if (layout != ES_LAYOUT_INLINE && layout != ES_LAYOUT_V && layout != ES_LAYOUT_BOXER) return false;

    es_engine_params p;
    memset(&p, 0, sizeof(p));
    const int n = cylinders;
    const double L = displacementL;
    p.cylinders = n;
    p.layout = layout;
    p.bankAngleDeg = (layout == ES_LAYOUT_V) ? bankAngleDeg : 0.0f;
    p.displacementL = displacementL;
    p.diesel = diesel ? 1 : 0;

    const Layout lay = defaultLayout(n, layout, bankAngleDeg);
    const int bankCount = (int)lay.bankAngle.size();
    for (int c = 0; c < n; ++c) {
        p.firingAngleDeg[c] = (float)lay.fire[c];
        p.cylinderBank[c] = lay.bank[c];
    }

    // bottom end
    const double cylVolume = L * 1e-3 / n;   // m3
    const double strokeToBore = diesel ? 1.2 : (n <= 2 ? 0.9 : 1.0);
    const double bore = std::cbrt(4 * cylVolume / (constants::pi * strokeToBore));   // m
    const double stroke = strokeToBore * bore;
    const double scale3 = std::pow(bore / 0.0995, 3);   // vs. the EJ25 script
    p.boreMm = (float)(bore * 1000);
    p.strokeMm = (float)(stroke * 1000);
    p.rodLengthMm = (float)(1.65 * stroke * 1000);
    p.compressionHeightMm = (float)(0.3 * bore * 1000);
    p.compressionRatio = diesel ? 16.0f : 9.5f;
    p.pistonMassG = (float)(566 * scale3);
    p.rodMassG = (float)(535 * scale3);
    p.crankMassKg = (float)std::max(3.0, 4.5 * L);
    p.flywheelMassKg = (float)(5.0 + 2.5 * L);
    p.flywheelRadiusMm = (float)(6 * Inch);
    p.frictionTorqueNm = (float)((L / 2.5 + 0.5) * FtLb);
    p.starterTorqueNm = (float)((70 + 20 * L) * FtLb);
    p.starterSpeedRpm = 300;   // free speed at the crank: cranks a cold engine at ~250-350 rpm
    p.blowbyCfm = 0.001f;

    // intake
    p.plenumVolumeL = (float)std::max(0.3, 0.5 * L);
    p.plenumAreaCm2 = 20;
    p.intakeFlowCfm = (float)(160 * L);
    p.runnerFlowCfm = (float)(100 * (cylVolume / 0.625e-3));
    p.runnerLengthMm = (float)(8 * Inch);
    p.idleThrottlePlate = 0.9978f;
    p.throttleGamma = 2.0f;
    p.intakeVelocityDecay = 0.5f;

    // heads: engine-sim's generic head flow (a 4.25" bore big block), scaled by bore area
    static const float intakeCfm[ES_PORT_FLOW_SAMPLES] = { 0, 25, 75, 100, 130, 180, 190, 220, 240, 250, 260, 260, 260, 255, 250 };
    static const float exhaustCfm[ES_PORT_FLOW_SAMPLES] = { 0, 25, 50, 75, 100, 125, 160, 175, 180, 190, 200, 205, 210, 210, 210 };
    const double flowScale = std::pow(bore / (4.25 * 0.0254), 2);
    for (int i = 0; i < ES_PORT_FLOW_SAMPLES; ++i) {
        p.intakePortCfm[i] = (float)(intakeCfm[i] * flowScale);
        p.exhaustPortCfm[i] = (float)(exhaustCfm[i] * flowScale);
    }
    p.headFlowScale[0] = p.headFlowScale[1] = 1.0f;
    p.intakeRunnerVolumeCc = (float)(0.24 * cylVolume * 1e6);
    p.intakeRunnerAreaCm2 = (float)(std::pow(0.44 * bore, 2) * 1e4);
    p.exhaustRunnerVolumeCc = (float)(0.08 * cylVolume * 1e6);
    p.exhaustRunnerAreaCm2 = (float)(std::pow(0.32 * bore, 2) * 1e4);

    const double lift = 0.1 * bore * 1000;
    const double duration = diesel ? 200 : 226;
    p.intakeCamDurationDeg = (float)duration;
    p.intakeCamLiftMm = (float)lift;
    p.intakeCamGamma = 2.0f;
    p.intakeLobeCenterDeg = 116.0f;
    p.exhaustCamDurationDeg = (float)(duration + 4);
    p.exhaustCamLiftMm = (float)(lift * 0.98);
    p.exhaustCamGamma = 2.0f;
    p.exhaustLobeCenterDeg = 112.0f;
    p.camBaseRadiusMm = (float)(0.18 * bore * 1000);

    // exhaust manifold
    p.collectorDiameterMm = (float)(2 * std::max(1.0, 1.25 * std::sqrt(L / bankCount)) * Inch);
    p.outletFlowCfm = (float)(1000 * std::max(0.5, L / 2.5));
    p.primaryLengthMm = (float)(20 * Inch);
    p.primaryFlowCfm = (float)(400 * std::max(0.3, L / 2.5));
    p.exhaustVelocityDecay = 1.0f;
    p.exhaustVolume[0] = p.exhaustVolume[1] = 1.0f;
    // unequal header lengths and slight per-cylinder differences give the scripts their character
    const std::vector<int> order = bankMajorOrder(n, bankCount, p.cylinderBank);
    for (int g = 0; g < n; ++g) {
        p.headerLengthMm[order[g]] = (float)((2.0 + 1.5 * (g % 3)) * Inch);
        p.soundAttenuation[order[g]] = (float)(0.9 + 0.2 * (g % 3) / 2.0);
    }

    // ignition advance by rpm
    static const float dieselTiming[] = { 8, 8, 10, 12, 14, 14 };
    static const float gasTiming[] = { 12, 12, 20, 26, 30, 34, 38, 38, 38 };
    const float *timing = diesel ? dieselTiming : gasTiming;
    p.timingSamples = diesel ? 6 : 9;
    for (int i = 0; i < p.timingSamples; ++i) {
        p.timingRpm[i] = 1000.0f * i;
        p.timingAdvanceDeg[i] = timing[i];
    }

    p.burningEfficiency = 0.9f;

    // Physics rate. By cylinder count (more cylinders cost more per step; 10 kHz is what
    // engine-sim's own V6 / V8 scripts use), and by cylinder size: small cylinders need a
    // finer step or the exhaust gas model oscillates at 1-2 kHz at idle (a 467 cc V6
    // cylinder squealed at 10 kHz, was clean at 18 kHz; 600 cc+ is clean at 10 kHz).
    const double cc = cylVolume * 1e6;
    const double byCount = (n <= 2) ? 30000 : (n <= 4 ? 18000 : 10000);
    const double bySize = std::min(30000.0, 10000.0 * std::pow(600.0 / cc, 2.5));
    p.simulationFrequency = (float)std::round(std::max(byCount, bySize));
    // The exhaust gas model's own step (physics rate x fluid sub-steps) must be fine enough for the
    // cylinder size, whatever the quality setting asks for. Mapped at idle: 600 cc+ cylinders are
    // clean from ~60k gas steps/s, a 467 cc V6 cylinder needs ~110k.
    p.minGasStepsPerSecond = (float)std::min(180000.0, 60000.0 * std::pow(600.0 / std::max(50.0, cc), 2.5));

    // 0 = chosen by the impulse response
    p.airNoise = 0.0f;
    p.jitter = 0.0f;

    *out = p;
    return true;
}

int stableFluidSteps(const es_engine_desc &d, double simulationFrequency, int requested) {
    const int floorSteps = (int)std::ceil(d.engine.minGasStepsPerSecond / simulationFrequency);
    return std::min(16, std::max(requested, floorSteps));
}

void BuiltEngine::destroy() {
    if (engine != nullptr) {
        engine->destroy();
        delete engine;
        engine = nullptr;
    }
    delete vehicle; vehicle = nullptr;
    delete transmission; transmission = nullptr;

    for (Camshaft *c : camshafts) { c->destroy(); delete c; }
    for (Valvetrain *v : valvetrains) delete v;
    for (Function *f : functions) { f->destroy(); delete f; }
    for (ImpulseResponse *ir : impulseResponses) delete ir;
    camshafts.clear();
    valvetrains.clear();
    functions.clear();
    impulseResponses.clear();
}

bool buildEngine(const es_engine_desc &d, const std::string &irDirectory, BuiltEngine *out, std::string *error) {
    const es_engine_params &p = d.engine;
    const int n = p.cylinders;
    if (n < 1 || n > ES_MAX_CYLINDERS) { *error = "cylinder count out of range"; return false; }
    if (!(p.boreMm > 5 && p.boreMm < 500 && p.strokeMm > 5 && p.strokeMm < 500)) { *error = "bore / stroke out of range"; return false; }
    if (!(p.simulationFrequency >= 1000 && p.simulationFrequency <= 100000)) { *error = "simulation frequency out of range"; return false; }
    if (p.timingSamples < 1 || p.timingSamples > ES_MAX_TIMING_SAMPLES) { *error = "timing curve needs 1 - 12 points"; return false; }

    Layout layout;
    layout.bankAngle = bankAngles(p.layout, p.bankAngleDeg);
    for (int c = 0; c < n; ++c) {
        if (p.cylinderBank[c] < 0 || p.cylinderBank[c] >= (int)layout.bankAngle.size()) { *error = "cylinder bank out of range"; return false; }
        layout.bank.push_back(p.cylinderBank[c]);
        layout.fire.push_back(p.firingAngleDeg[c]);
    }
    const int bankCount = (int)layout.bankAngle.size();
    const bool diesel = p.diesel != 0;
    const double redline = units::rpm(d.redlineRpm > 500 ? d.redlineRpm : (diesel ? 4000 : 6000));

    // ---- geometry -----------------------------------------------------
    const double bore = units::distance(p.boreMm, units::mm);
    const double stroke = units::distance(p.strokeMm, units::mm);
    const double rodLength = units::distance(p.rodLengthMm, units::mm);
    const double compressionHeight = units::distance(p.compressionHeightMm, units::mm);
    const double cylVolume = constants::pi / 4 * bore * bore * stroke;
    const double chamberVolume = cylVolume / std::max(0.5, (double)p.compressionRatio - 1);
    const double pistonMass = units::mass(p.pistonMassG, units::g);
    const double rodMass = units::mass(p.rodMassG, units::g);

    // ---- exhaust sound ------------------------------------------------
    // the fitted exhaust if given; otherwise smaller and rougher pipes for bikes and diesels
    const std::string irFile = (d.exhaustIr[0] != '\0')
        ? std::string(d.exhaustIr, strnlen(d.exhaustIr, sizeof(d.exhaustIr)))
        : (diesel ? "minimal_muffling_02.wav" : (n <= 2 ? "minimal_muffling_01.wav" : "mild_exhaust.wav"));
    if (irFile.find('/') != std::string::npos || irFile.find('\\') != std::string::npos || irFile.find("..") != std::string::npos) {
        *error = "impulse response must be a plain file name: " + irFile;
        return false;
    }
    // The synthesizer multiplies the exhaust pulse by broadband air noise; a muffled
    // impulse response filters it out, the barely-muffled ones pass it straight
    // through and it drowns the engine. engine-sim's own scripts use ~0.1-0.2 with them.
    const bool brightIr = irFile.rfind("minimal_muffling_01", 0) == 0 || irFile.rfind("minimal_muffling_03", 0) == 0;
    const bool mediumIr = irFile.rfind("minimal_muffling_02", 0) == 0;
    const double irNoise = brightIr ? 0.15 : (mediumIr ? 0.4 : (diesel ? 0.6 : 1.0));
    const double irJitter = brightIr ? 0.15 : (mediumIr ? 0.3 : (diesel ? 0.3 : 0.5));
    // the exhaust's own, else the engine's, else what suits the impulse response
    const double noise = (d.airNoise > 0) ? d.airNoise : (p.airNoise > 0 ? p.airNoise : irNoise);
    const double jitter = (d.jitter > 0) ? d.jitter : (p.jitter > 0 ? p.jitter : irJitter);

    // ---- engine -----------------------------------------------------
    DirectThrottleLinkage *throttle = new DirectThrottleLinkage;
    DirectThrottleLinkage::Parameters throttleParams;
    throttleParams.gamma = p.throttleGamma;
    throttle->initialize(throttleParams);

    const int exhaustCount = bankCount;   // one exhaust per bank
    Engine::Parameters ep;
    ep.name = "Engine Sim";
    ep.cylinderBanks = bankCount;
    ep.cylinderCount = n;
    ep.crankshaftCount = 1;
    ep.exhaustSystemCount = exhaustCount;
    ep.intakeCount = 1;
    ep.starterTorque = units::torque(p.starterTorqueNm, units::Nm);
    ep.starterSpeed = units::rpm(p.starterSpeedRpm);
    ep.redline = redline;
    ep.dynoMinSpeed = units::rpm(500);
    ep.dynoMaxSpeed = redline;
    ep.dynoHoldStep = units::rpm(100);
    ep.throttle = throttle;
    ep.initialSimulationFrequency = p.simulationFrequency;
    ep.initialHighFrequencyGain = 0.01;
    ep.initialNoise = noise;
    ep.initialJitter = jitter;

    out->engine = new Engine;
    Engine *engine = out->engine;
    engine->initialize(ep);

    // ---- crankshaft ---------------------------------------------------
    const double crankMass = units::mass(p.crankMassKg, units::kg);
    const double flywheelMass = units::mass(p.flywheelMassKg, units::kg);
    const double flywheelRadius = units::distance(p.flywheelRadiusMm, units::mm);
    Crankshaft::Parameters cp;
    cp.mass = crankMass;
    cp.flywheelMass = flywheelMass;
    cp.momentOfInertia =
        diskMoment(crankMass, stroke / 2)
        + 2 * diskMoment(flywheelMass, flywheelRadius)
        + diskMoment(units::mass(10, units::kg), units::distance(6, units::cm));   // cams, pulleys
    cp.crankThrow = stroke / 2;
    cp.pos_x = 0;
    cp.pos_y = 0;
    cp.tdc = (90.0 + layout.bankAngle[0]) * units::deg;
    cp.frictionTorque = units::torque(p.frictionTorqueNm, units::Nm);
    cp.rodJournals = n;
    Crankshaft *crank = engine->getCrankshaft(0);
    crank->initialize(cp);
    out->crankInertia = cp.momentOfInertia;

    // global cylinder order is bank-major; remember which cylinder each slot is
    const std::vector<int> order = bankMajorOrder(n, bankCount, p.cylinderBank);
    for (int g = 0; g < n; ++g) {
        const int c = order[g];
        const double journal = wrap360(layout.fire[c] + layout.bankAngle[layout.bank[c]] - layout.bankAngle[0]);
        crank->setRodJournalAngle(g, journal * units::deg);
    }

    // ---- intake / exhausts --------------------------------------------
    Intake::Parameters ip;
    ip.volume = units::volume(p.plenumVolumeL, units::L);
    ip.CrossSectionArea = units::area(p.plenumAreaCm2, units::cm2);
    ip.InputFlowK = GasSystem::k_carb(p.intakeFlowCfm);
    ip.IdleFlowK = 0.0;
    ip.RunnerFlowRate = GasSystem::k_carb(p.runnerFlowCfm);
    // engine-sim's mixture: 12.5 O2 per C8H18 at lambda 0.8 (its intake supplies 0.8 of this as air), scaled by
    // the mixture screw (Engine Tuning Toolkit): leaner above 1, richer below
    ip.MolecularAfr = 25.0 / 2.0 * (p.mixture > 0 ? p.mixture : 1.0);
    ip.IdleThrottlePlatePosition = p.idleThrottlePlate;
    ip.RunnerLength = units::distance(p.runnerLengthMm, units::mm);
    ip.VelocityDecay = p.intakeVelocityDecay;
    Intake *intake = engine->getIntake(0);
    intake->initialize(ip);

    const double outletFlowScale = (d.exhaustFlow > 0) ? d.exhaustFlow : 1.0;
    const double exhaustLength = units::distance((d.exhaustLengthM > 0) ? d.exhaustLengthM : 0.5, units::m);
    const double headerScale = (d.headerScale > 0) ? d.headerScale : 1.0;
    for (int e = 0; e < exhaustCount; ++e) {
        ImpulseResponse *ir = new ImpulseResponse;
        ir->initialize(irDirectory + "/" + irFile, 0.01);
        out->impulseResponses.push_back(ir);

        ExhaustSystem::Parameters xp;
        // Physical collector: fixed 0.5 m (its volume is cross-section x length). The fitted
        // exhaust's pipe length only changes what the synthesizer hears (setSoundLength below):
        // a short physical collector is a tiny volume that oscillates at 1-2 kHz at idle.
        xp.length = units::distance(0.5, units::m);
        xp.collectorCrossSectionArea = circleArea(units::distance(p.collectorDiameterMm / 2, units::mm));
        xp.outletFlowRate = GasSystem::k_carb(p.outletFlowCfm * outletFlowScale);
        xp.primaryTubeLength = units::distance(p.primaryLengthMm, units::mm) * headerScale;
        xp.primaryFlowRate = GasSystem::k_carb(p.primaryFlowCfm);
        xp.velocityDecay = p.exhaustVelocityDecay;
        xp.audioVolume = p.exhaustVolume[std::min(e, ES_MAX_BANKS - 1)];
        xp.impulseResponse = ir;
        engine->getExhaustSystem(e)->initialize(xp);
        engine->getExhaustSystem(e)->setSoundLength(exhaustLength);
    }

    // ---- cams ---------------------------------------------------------
    Function *intakeLobe = harmonicCamLobe(out, p.intakeCamDurationDeg * units::deg, p.intakeCamGamma,
        units::distance(p.intakeCamLiftMm, units::mm), 100);
    Function *exhaustLobe = harmonicCamLobe(out, p.exhaustCamDurationDeg * units::deg, p.exhaustCamGamma,
        units::distance(p.exhaustCamLiftMm, units::mm), 100);
    const double intakeCenter = p.intakeLobeCenterDeg * units::deg;
    const double exhaustCenter = p.exhaustLobeCenterDeg * units::deg;

    // ---- banks, pistons, rods, heads -----------------------------------
    int g = 0;
    for (int b = 0; b < bankCount; ++b) {
        int bankCylinders = 0;
        for (int c = 0; c < n; ++c) if (layout.bank[c] == b) ++bankCylinders;

        CylinderBank *bank = engine->getCylinderBank(b);
        CylinderBank::Parameters bp;
        bp.crankshaft = crank;
        bp.positionX = 0;
        bp.positionY = 0;
        bp.angle = layout.bankAngle[b] * units::deg;
        bp.bore = bore;
        bp.deckHeight = stroke / 2 + rodLength + compressionHeight;
        bp.displayDepth = 0.5;
        bp.cylinderCount = bankCylinders;
        bp.index = b;
        bank->initialize(bp);

        Camshaft *intakeCam = new Camshaft, *exhaustCam = new Camshaft;
        out->camshafts.push_back(intakeCam);
        out->camshafts.push_back(exhaustCam);
        Camshaft::Parameters camp;
        camp.lobes = bankCylinders;
        camp.advance = 0;
        camp.crankshaft = crank;
        camp.baseRadius = units::distance(p.camBaseRadiusMm, units::mm);
        camp.lobeProfile = intakeLobe;
        intakeCam->initialize(camp);
        camp.lobeProfile = exhaustLobe;
        exhaustCam->initialize(camp);

        StandardValvetrain *valvetrain = new StandardValvetrain;
        out->valvetrains.push_back(valvetrain);
        StandardValvetrain::Parameters vp;
        vp.intakeCamshaft = intakeCam;
        vp.exhaustCamshaft = exhaustCam;
        valvetrain->initialize(vp);

        const int bankBase = g;
        for (int i = 0; i < bankCylinders; ++i, ++g) {
            const int c = order[g];
            const double fire = layout.fire[c] * units::deg;
            intakeCam->setLobeCenterline(i, 360 * units::deg + intakeCenter + fire);
            exhaustCam->setLobeCenterline(i, 360 * units::deg - exhaustCenter + fire);

            Piston *piston = engine->getPiston(g);
            ConnectingRod *rod = engine->getConnectingRod(g);

            Piston::Parameters pp;
            pp.Rod = rod;
            pp.Bank = bank;
            pp.CylinderIndex = i;
            pp.BlowbyFlowCoefficient = GasSystem::k_28inH2O(p.blowbyCfm);
            pp.CompressionHeight = compressionHeight;
            pp.WristPinPosition = 0;
            pp.Displacement = 0;
            pp.mass = pistonMass;
            piston->initialize(pp);

            ConnectingRod::Parameters rp;
            rp.mass = rodMass;
            rp.momentOfInertia = (1 / 12.0) * rodMass * rodLength * rodLength;
            rp.centerOfMass = 0;
            rp.length = rodLength;
            rp.rodJournals = 0;
            rp.slaveThrow = 0;
            rp.piston = piston;
            rp.crankshaft = crank;
            rp.master = nullptr;
            rp.journal = g;
            rod->initialize(rp);
        }

        const double headFlow = p.headFlowScale[std::min(b, ES_MAX_BANKS - 1)];
        CylinderHead *head = engine->getHead(b);
        CylinderHead::Parameters hp;
        hp.Bank = bank;
        hp.ExhaustPortFlow = portFlow(out, p.exhaustPortCfm, ES_PORT_FLOW_SAMPLES, headFlow);
        hp.IntakePortFlow = portFlow(out, p.intakePortCfm, ES_PORT_FLOW_SAMPLES, headFlow);
        hp.Valvetrain = valvetrain;
        hp.CombustionChamberVolume = chamberVolume;
        hp.IntakeRunnerVolume = units::volume(p.intakeRunnerVolumeCc, units::cc);
        hp.IntakeRunnerCrossSectionArea = units::area(p.intakeRunnerAreaCm2, units::cm2);
        hp.ExhaustRunnerVolume = units::volume(p.exhaustRunnerVolumeCc, units::cc);
        hp.ExhaustRunnerCrossSectionArea = units::area(p.exhaustRunnerAreaCm2, units::cm2);
        hp.FlipDisplay = (b % 2) == 0;
        head->initialize(hp);

        for (int i = 0; i < bankCylinders; ++i) {
            const int c = order[bankBase + i];
            head->setIntake(i, intake);
            head->setExhaustSystem(i, engine->getExhaustSystem(b));
            head->setSoundAttenuation(i, p.soundAttenuation[c]);
            head->setHeaderPrimaryLength(i, units::distance(p.headerLengthMm[c], units::mm) * headerScale);
        }
    }

    // ---- ignition -----------------------------------------------------
    Function *timing = newFunction(out, units::rpm(1000));
    for (int i = 0; i < p.timingSamples; ++i) timing->addSample(units::rpm(p.timingRpm[i]), p.timingAdvanceDeg[i] * units::deg);

    IgnitionModule::Parameters ignp;
    ignp.cylinderCount = n;
    ignp.crankshaft = crank;
    ignp.timingCurve = timing;
    ignp.revLimit = redline + units::rpm(200);
    ignp.limiterDuration = 0.1 * units::sec;
    engine->getIgnitionModule()->initialize(ignp);
    for (int i = 0; i < n; ++i) {
        engine->getIgnitionModule()->setFiringOrder(i, layout.fire[order[i]] * units::deg);
    }

    // ---- fuel and chambers --------------------------------------------
    Function *turbulence = newFunction(out, 5.0);
    turbulence->addSample(0.0, 3.0);
    for (int i = 1; i < 10; ++i) turbulence->addSample(5.0 * i, 1.5 * 5.0 * i);

    Fuel::Parameters fp;
    fp.maxBurningEfficiency = p.burningEfficiency;
    fp.maxDilutionEffect = 10.0;   // objects.mr default (the struct says 50)
    fp.turbulenceToFlameSpeedRatio = turbulence;
    if (diesel) {
        fp.name = "Diesel";
        fp.energyDensity = units::energy(45.5, units::kJ) / units::mass(1.0, units::g);
        fp.density = units::mass(0.832, units::kg) / units::volume(1.0, units::L);
    }
    engine->getFuel()->initialize(fp);

    Function *meanPistonSpeedToTurbulence = newFunction(out, 1);
    for (int i = 0; i < 30; ++i) meanPistonSpeedToTurbulence->addSample((double)i, i * 0.5);

    CombustionChamber::Parameters ccp;
    ccp.CrankcasePressure = units::pressure(1.0, units::atm);
    ccp.Fuel = engine->getFuel();
    ccp.StartingPressure = units::pressure(1.0, units::atm);
    ccp.StartingTemperature = units::celcius(25.0);
    ccp.MeanPistonSpeedToTurbulence = meanPistonSpeedToTurbulence;
    for (int i = 0; i < n; ++i) {
        ccp.Piston = engine->getPiston(i);
        ccp.Head = engine->getHead(ccp.Piston->getCylinderBank()->getIndex());
        engine->getChamber(i)->initialize(ccp);
    }

    engine->calculateDisplacement();

    // ---- drivetrain: required by the simulator, left in neutral --------
    Vehicle::Parameters vehp;
    vehp.mass = units::mass(1597, units::kg);
    vehp.diffRatio = 3.42;
    vehp.tireRadius = units::distance(10, units::inch);
    vehp.dragCoefficient = 0.25;
    vehp.crossSectionArea = units::distance(6.0, units::foot) * units::distance(6.0, units::foot);
    vehp.rollingResistance = 2000.0;
    out->vehicle = new Vehicle;
    out->vehicle->initialize(vehp);

    static const double gearRatios[] = { 2.97, 2.07, 1.43, 1.00, 0.84, 0.56 };
    Transmission::Parameters tp;
    tp.GearCount = 6;
    tp.GearRatios = gearRatios;
    tp.MaxClutchTorque = units::torque(1000.0, units::ft_lb);
    out->transmission = new Transmission;
    out->transmission->initialize(tp);   // gear -1 = neutral

    return true;
}
