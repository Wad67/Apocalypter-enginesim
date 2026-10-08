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

#include <cmath>
#include <cstring>

namespace {

// ---------------------------------------------------------------------
//  Layout: bank angles, and for each cylinder its bank and firing angle
//  (crank degrees within the 720 degree cycle). Everything else (crank
//  journal angles, cam lobe centrelines, crank TDC) follows from these,
//  using the relations engine-sim's own engine scripts obey:
//    journal  = fire + (bankAngle[bank] - bankAngle[0])     (mod 360)
//    TDC      = 90 deg + bankAngle[0]
//    lobes    = 360 -/+ lobe centre + fire                  (exhaust / intake)
// ---------------------------------------------------------------------
struct Layout {
    std::vector<double> bankAngle;
    std::vector<int> bank;
    std::vector<double> fire;
};

Layout evenFiring(int n, std::vector<double> bankAngles) {
    Layout l;
    l.bankAngle = bankAngles;
    const int banks = (int)bankAngles.size();
    for (int i = 0; i < n; ++i) {
        l.bank.push_back(i % banks);
        l.fire.push_back(i * 720.0 / n);
    }
    return l;
}

Layout makeLayout(const es_engine_desc &d) {
    const int n = d.cylinders;
    if (d.layout == ES_LAYOUT_INLINE || n == 1) {
        Layout l;
        l.bankAngle = { 0.0 };
        // firing angle per cylinder number (1..n), common firing orders
        switch (n) {
        case 1: l.fire = { 0 }; break;
        case 2: l.fire = { 0, 360 }; break;                               // 360 deg parallel twin
        case 3: l.fire = { 0, 480, 240 }; break;                          // 1-3-2
        case 4: l.fire = { 0, 540, 180, 360 }; break;                     // 1-3-4-2
        case 5: l.fire = { 0, 144, 576, 288, 432 }; break;                // 1-2-4-5-3 (Audi i5 script)
        case 6: l.fire = { 0, 480, 240, 600, 120, 360 }; break;           // 1-5-3-6-2-4 (2JZ script)
        case 8: l.fire = { 0, 180, 450, 630, 270, 90, 540, 360 }; break;  // 1-6-2-5-8-3-7-4
        default: return evenFiring(n, { 0.0 });
        }
        l.bank.assign(n, 0);
        return l;
    }

    if (d.layout == ES_LAYOUT_BOXER) {
        if (n == 4) {
            // Subaru EJ25 script: bank 0 = cylinders 1, 3; bank 1 = 2, 4; order 1-3-2-4
            Layout l;
            l.bankAngle = { 90.0, -90.0 };
            l.bank = { 0, 1, 0, 1 };
            l.fire = { 0, 360, 180, 540 };
            return l;
        }
        return evenFiring(n, { 90.0, -90.0 });
    }

    // V: bank 0 on the left
    const double v = (d.vAngleDeg > 0) ? d.vAngleDeg : 90.0;
    const std::vector<double> banks = { -v / 2, v / 2 };
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
Function *portFlow(BuiltEngine *out, const double *cfm, int count, double scale) {
    Function *f = newFunction(out, units::distance(50, units::thou));
    for (int i = 0; i < count; ++i) {
        f->addSample(i * 50 * units::thou, GasSystem::k_28inH2O(cfm[i] * scale));
    }
    return f;
}

double diskMoment(double mass, double radius) { return 0.5 * mass * radius * radius; }
double circleArea(double radius) { return constants::pi * radius * radius; }

} // namespace

int stableFluidSteps(const es_engine_desc &d, double simulationFrequency, int requested) {
    // The exhaust gas model's own step (physics rate x fluid sub-steps) must be fine enough
    // for the cylinder size, whatever the quality setting asks for. Mapped at idle: 600 cc+
    // cylinders are clean from ~60k gas steps/s, a 467 cc V6 cylinder needs ~110k.
    const double cc = d.displacementL * 1000.0 / std::max(1, (int)d.cylinders);
    const double required = std::min(180000.0, 60000.0 * std::pow(600.0 / std::max(50.0, cc), 2.5));
    const int floorSteps = (int)std::ceil(required / simulationFrequency);
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
    if (d.cylinders < 1 || d.cylinders > 16) { *error = "cylinder count out of range"; return false; }
    if (!(d.displacementL > 0.02 && d.displacementL < 30)) { *error = "displacement out of range"; return false; }

    const Layout layout = makeLayout(d);
    const int n = d.cylinders;
    const int bankCount = (int)layout.bankAngle.size();
    const bool diesel = d.diesel != 0;
    const double redline = units::rpm(d.redlineRpm > 500 ? d.redlineRpm : (diesel ? 4000 : 6000));

    // ---- geometry from displacement ---------------------------------
    const double cylVolume = d.displacementL * units::L / n;
    const double strokeToBore = diesel ? 1.2 : (n <= 2 ? 0.9 : 1.0);
    const double bore = std::cbrt(4 * cylVolume / (constants::pi * strokeToBore));
    const double stroke = strokeToBore * bore;
    const double rodLength = 1.65 * stroke;
    const double compressionHeight = 0.3 * bore;
    const double scale3 = std::pow(bore / units::distance(99.5, units::mm), 3);   // vs. the EJ25 script
    const double pistonMass = units::mass(566, units::g) * scale3;
    const double rodMass = units::mass(535, units::g) * scale3;
    const double compressionRatio = diesel ? 16.0 : 9.5;
    const double chamberVolume = cylVolume / (compressionRatio - 1);
    const double litres = d.displacementL;

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
    const double defaultNoise = brightIr ? 0.15 : (mediumIr ? 0.4 : (diesel ? 0.6 : 1.0));
    const double defaultJitter = brightIr ? 0.15 : (mediumIr ? 0.3 : (diesel ? 0.3 : 0.5));

    // ---- engine -----------------------------------------------------
    DirectThrottleLinkage *throttle = new DirectThrottleLinkage;
    DirectThrottleLinkage::Parameters throttleParams;
    throttleParams.gamma = 2.0;
    throttle->initialize(throttleParams);

    const int exhaustCount = bankCount;   // one exhaust per bank
    Engine::Parameters ep;
    ep.name = "Engine Sim";
    ep.cylinderBanks = bankCount;
    ep.cylinderCount = n;
    ep.crankshaftCount = 1;
    ep.exhaustSystemCount = exhaustCount;
    ep.intakeCount = 1;
    ep.starterTorque = units::torque(70 + 20 * litres, units::ft_lb);
    ep.starterSpeed = units::rpm(500);
    ep.redline = redline;
    ep.dynoMinSpeed = units::rpm(500);
    ep.dynoMaxSpeed = redline;
    ep.dynoHoldStep = units::rpm(100);
    ep.throttle = throttle;
    // small engines need a finer timestep to sound right (single/twin scripts use 35-40 kHz)
    // Physics rate. By cylinder count (more cylinders cost more per step; 10 kHz is what
    // engine-sim's own V6 / V8 scripts use), and by cylinder size: small cylinders need a
    // finer step or the exhaust gas model oscillates at 1-2 kHz at idle (a 467 cc V6
    // cylinder squealed at 10 kHz, was clean at 18 kHz; 600 cc+ is clean at 10 kHz).
    // See also stableFluidSteps().
    const double byCount = (n <= 2) ? 30000 : (n <= 4 ? 18000 : 10000);
    const double cc = cylVolume / units::cc;
    const double bySize = std::min(30000.0, 10000.0 * std::pow(600.0 / cc, 2.5));
    ep.initialSimulationFrequency = std::round(std::max(byCount, bySize));
    ep.initialHighFrequencyGain = 0.01;
    ep.initialNoise = (d.airNoise > 0) ? d.airNoise : defaultNoise;
    ep.initialJitter = (d.jitter > 0) ? d.jitter : defaultJitter;

    out->engine = new Engine;
    Engine *engine = out->engine;
    engine->initialize(ep);

    // ---- crankshaft ---------------------------------------------------
    const double crankMass = units::mass(std::max(3.0, 4.5 * litres), units::kg);
    const double flywheelMass = units::mass(5.0 + 2.5 * litres, units::kg);
    const double flywheelRadius = units::distance(6, units::inch);
    Crankshaft::Parameters cp;
    cp.mass = crankMass;
    cp.flywheelMass = flywheelMass;
    cp.momentOfInertia =
        diskMoment(crankMass, stroke / 2)
        + 2 * diskMoment(flywheelMass, flywheelRadius)
        + diskMoment(units::mass(10, units::kg), units::distance(6, units::cm));
    cp.crankThrow = stroke / 2;
    cp.pos_x = 0;
    cp.pos_y = 0;
    cp.tdc = (90.0 + layout.bankAngle[0]) * units::deg;
    cp.frictionTorque = units::torque(1.0 * litres / 2.5 + 0.5, units::ft_lb);
    cp.rodJournals = n;
    Crankshaft *crank = engine->getCrankshaft(0);
    crank->initialize(cp);

    // global cylinder order is bank-major; remember which input cylinder each slot is
    std::vector<int> order;
    for (int b = 0; b < bankCount; ++b) {
        for (int c = 0; c < n; ++c) if (layout.bank[c] == b) order.push_back(c);
    }
    for (int g = 0; g < n; ++g) {
        const int c = order[g];
        const double journal = wrap360(layout.fire[c] + layout.bankAngle[layout.bank[c]] - layout.bankAngle[0]);
        crank->setRodJournalAngle(g, journal * units::deg);
    }

    // ---- intake / exhausts --------------------------------------------
    Intake::Parameters ip;
    ip.volume = units::volume(std::max(0.3, 0.5 * litres), units::L);
    ip.CrossSectionArea = units::area(20.0, units::cm2);
    ip.InputFlowK = GasSystem::k_carb(160.0 * litres);
    ip.IdleFlowK = 0.0;
    ip.RunnerFlowRate = GasSystem::k_carb(100.0 * (cylVolume / (0.625 * units::L)));
    ip.MolecularAfr = 25.0 / 2.0;
    ip.IdleThrottlePlatePosition = 0.9978;
    ip.RunnerLength = units::distance(8.0, units::inch);
    ip.VelocityDecay = 0.5;
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
        xp.collectorCrossSectionArea = circleArea(units::distance(std::max(1.0, 1.25 * std::sqrt(litres / bankCount)), units::inch));
        xp.outletFlowRate = GasSystem::k_carb(1000.0 * std::max(0.5, litres / 2.5) * outletFlowScale);
        xp.primaryTubeLength = units::distance(20.0, units::inch) * headerScale;
        xp.primaryFlowRate = GasSystem::k_carb(400.0 * std::max(0.3, litres / 2.5));
        xp.velocityDecay = 1.0;
        xp.audioVolume = 1.0;
        xp.impulseResponse = ir;
        engine->getExhaustSystem(e)->initialize(xp);
        engine->getExhaustSystem(e)->setSoundLength(exhaustLength);
    }

    // ---- head flow and cams (shared by all banks) ------------------------
    static const double intakeCfm[] = { 0, 25, 75, 100, 130, 180, 190, 220, 240, 250, 260, 260, 260, 255, 250 };
    static const double exhaustCfm[] = { 0, 25, 50, 75, 100, 125, 160, 175, 180, 190, 200, 205, 210, 210, 210 };
    const double flowScale = std::pow(bore / units::distance(4.25, units::inch), 2);   // generic head is a 4.25" bore BBC
    Function *intakeFlow = portFlow(out, intakeCfm, 15, flowScale);
    Function *exhaustFlow = portFlow(out, exhaustCfm, 15, flowScale);

    const double lift = 0.1 * bore;
    const double duration = (diesel ? 200 : 226) * units::deg;
    Function *intakeLobe = harmonicCamLobe(out, duration, 2.0, lift, 100);
    Function *exhaustLobe = harmonicCamLobe(out, duration + 4 * units::deg, 2.0, lift * 0.98, 100);
    const double intakeCenter = 116 * units::deg;
    const double exhaustCenter = 112 * units::deg;

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
        camp.baseRadius = 0.18 * bore;
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
            pp.BlowbyFlowCoefficient = GasSystem::k_28inH2O(0.001);
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

        CylinderHead *head = engine->getHead(b);
        CylinderHead::Parameters hp;
        hp.Bank = bank;
        hp.ExhaustPortFlow = exhaustFlow;
        hp.IntakePortFlow = intakeFlow;
        hp.Valvetrain = valvetrain;
        hp.CombustionChamberVolume = chamberVolume;
        hp.IntakeRunnerVolume = 0.24 * cylVolume;
        hp.IntakeRunnerCrossSectionArea = std::pow(0.44 * bore, 2);
        hp.ExhaustRunnerVolume = 0.08 * cylVolume;
        hp.ExhaustRunnerCrossSectionArea = std::pow(0.32 * bore, 2);
        hp.FlipDisplay = (b % 2) == 0;
        head->initialize(hp);

        for (int i = 0; i < bankCylinders; ++i) {
            head->setIntake(i, intake);
            head->setExhaustSystem(i, engine->getExhaustSystem(b));
            // unequal header lengths and slight per-cylinder differences give the scripts their character
            head->setSoundAttenuation(i, 0.9 + 0.2 * ((bankBase + i) % 3) / 2.0);
            head->setHeaderPrimaryLength(i, units::distance(2.0 + 1.5 * ((bankBase + i) % 3), units::inch) * headerScale);
        }
    }

    // ---- ignition -----------------------------------------------------
    Function *timing = newFunction(out, units::rpm(1000));
    if (diesel) {
        const double t[] = { 8, 8, 10, 12, 14, 14 };
        for (int i = 0; i < 6; ++i) timing->addSample(units::rpm(1000.0 * i), t[i] * units::deg);
    }
    else {
        const double t[] = { 12, 12, 20, 26, 30, 34, 38, 38, 38 };
        for (int i = 0; i < 9; ++i) timing->addSample(units::rpm(1000.0 * i), t[i] * units::deg);
    }

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
    fp.maxBurningEfficiency = 0.9;
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
