// Offline test of drive mode: stands in for the game's engine (NWH), whose speed is integrated
// from the simulated engine's torque at the game's engine inertia, the way the plugin couples them.
//   cosim "<engine part name>" <ir dir> drive [out.wav]   crank, idle, full throttle in neutral to
//                                                        the limiter, lift, a load that stalls it, then
//                                                        a roll start (key on, wheels turn it at 700 rpm)
//   cosim "<engine part name>" <ir dir> sweep            steady full-throttle torque and power by rpm,
//                                                        at 1 atm and at ES_BOOST (default 1.5)
// Environment: ES_IDLE, ES_REDLINE, ES_INERTIA (default: the simulated crank's), ES_LOAD (N m),
// ES_IDLE_PLATE.
#include "enginesim_api.h"
#include "test_engines.h"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <vector>

static double env(const char *name, double fallback) {
    const char *v = getenv(name);
    return v ? atof(v) : fallback;
}

static void writeWav(const char *path, const std::vector<int16_t> &pcm) {
    FILE *f = fopen(path, "wb");
    if (!f) return;
    const uint32_t dataBytes = (uint32_t)(pcm.size() * 2), rate = ES_SAMPLE_RATE, byteRate = rate * 2;
    const uint32_t riffSize = 36 + dataBytes, fmtSize = 16;
    const uint16_t pcmFmt = 1, channels = 1, align = 2, bits = 16;
    fwrite("RIFF", 1, 4, f); fwrite(&riffSize, 4, 1, f); fwrite("WAVEfmt ", 1, 8, f);
    fwrite(&fmtSize, 4, 1, f); fwrite(&pcmFmt, 2, 1, f); fwrite(&channels, 2, 1, f);
    fwrite(&rate, 4, 1, f); fwrite(&byteRate, 4, 1, f); fwrite(&align, 2, 1, f); fwrite(&bits, 2, 1, f);
    fwrite("data", 1, 4, f); fwrite(&dataBytes, 4, 1, f); fwrite(pcm.data(), 2, pcm.size(), f);
    fclose(f);
}

// the simulation has no running state; for the test, as a person would judge it by ear: past 70 %
// of idle it has caught (the starter is let go), under a third of idle it isn't running
static bool caughtAt(double rpm, double idle) { return rpm > 0.7 * idle; }
static const char *looksLike(double rpm, double idle) { return rpm > idle / 3 ? "running" : "STOPPED"; }

int main(int argc, char **argv) {
    if (argc < 4) { fprintf(stderr, "usage: cosim \"<engine name>\" <ir dir> drive|sweep|start|machines [out.wav]\n"); return 2; }
    es_engine_desc d;
    if (!engineFromName(argv[1], &d)) { fprintf(stderr, "cannot read '%s'\n", argv[1]); return 1; }
    d.idleRpm = (float)env("ES_IDLE", d.idleRpm);
    d.engine.idleThrottlePlate = (float)env("ES_IDLE_PLATE", d.engine.idleThrottlePlate);
    d.redlineRpm = (float)env("ES_REDLINE", d.redlineRpm);
    d.engine.starterSpeedRpm = (float)env("ES_STARTER_SPEED", d.engine.starterSpeedRpm);
    // ES_BUILD_PSI: built for boost as the plugin does (InductionMachines.BuildFor)
    if (getenv("ES_BUILD_PSI")) {
        const double x = std::min(1.0, std::max(0.0, env("ES_BUILD_PSI", 0) / 15.0));
        const double boosted = d.engine.diesel ? 15.0 : 8.0;
        if (d.engine.compressionRatio > boosted) d.engine.compressionRatio -= (float)((d.engine.compressionRatio - boosted) * x);
        d.engine.intakeLobeCenterDeg += (float)(4 * x);
        d.engine.exhaustLobeCenterDeg += (float)(4 * x);
        d.engine.compressionRatio = (float)env("ES_CR", d.engine.compressionRatio);
        d.engine.intakeLobeCenterDeg += (float)env("ES_ICL_ADD", 0);
        d.engine.exhaustLobeCenterDeg += (float)env("ES_ECL_ADD", 0);
        d.engine.intakeCamDurationDeg += (float)env("ES_IDUR_ADD", 0);
        d.engine.exhaustCamDurationDeg += (float)env("ES_EDUR_ADD", 0);
        printf("built: compression %.1f, lobe centres %.1f / %.1f\n", d.engine.compressionRatio, d.engine.intakeLobeCenterDeg, d.engine.exhaustLobeCenterDeg);
    }
    snprintf(d.exhaustIr, sizeof(d.exhaustIr), "mild_exhaust.wav");
    d.exhaustFlow = 1; d.exhaustLengthM = 0.15f; d.headerScale = 1;

    const bool sweep = std::string(argv[3]) == "sweep";

    es_session *s = es_create(&d, argv[2]);
    if (!s) { fprintf(stderr, "es_create: %s\n", es_last_error()); return 1; }
    es_set_volume(s, 1.0);
    // the game's engine integrates at the simulated crank's inertia, as the plugin does in drive mode
    const double inertia = env("ES_INERTIA", es_get_crank_inertia(s));
    es_set_drive_mode(s, 1);
    es_set_idle_rpm(s, d.idleRpm);
    es_set_rev_limiter(s, d.redlineRpm, 0.08);
    if (getenv("ES_CONDITION")) es_set_condition(s, atof(getenv("ES_CONDITION")));
    printf("%s: idle %.0f, limit %.0f rpm, game inertia %.2f, sim crank inertia %.3f kg m2\n",
        argv[1], d.idleRpm, d.redlineRpm, inertia, es_get_crank_inertia(s));

    const double dt = 0.01;   // the game's physics step
    std::vector<int16_t> pcm, chunk(8192);
    auto drain = [&]() { int n; while ((n = es_read(s, chunk.data(), (int)chunk.size())) > 0) pcm.insert(pcm.end(), chunk.begin(), chunk.begin() + n); };
    const double rads = 2 * M_PI / 60;   // rpm -> rad/s

    if (std::string(argv[3]) == "machines") {
        // forced induction as machines: ES_SC (supercharger size), ES_SC_PSI (relief), ES_TURBOS
        // ("0.6,0.6,1.4"), ES_WG_PSI. Steady full throttle by rpm (held by the "game", 4 s to settle),
        // then a throttle step at ES_STEP_RPM (spool)
        es_induction_desc ind = {};
        ind.superchargerSize = (float)env("ES_SC", 0);
        ind.superchargerReliefPsi = (float)env("ES_SC_PSI", 8);
        ind.wastegatePsi = (float)env("ES_WG_PSI", 8);
        if (getenv("ES_TURBOS")) {
            std::string list = getenv("ES_TURBOS");
            size_t at = 0;
            while (at < list.size() && ind.turbos < ES_MAX_TURBOS) {
                ind.turboSize[ind.turbos++] = (float)atof(list.c_str() + at);
                at = list.find(',', at);
                if (at == std::string::npos) break;
                ++at;
            }
        }
        es_set_induction(s, &ind);
        es_set_rev_limiter(s, 0, 0);
        es_set_ignition(s, 1);
        auto report = [&](double rpm, double nm) {
            double psi, kw, spin[ES_MAX_TURBOS];
            es_get_induction(s, &psi, spin, &kw);
            printf("  %5.0f rpm  %6.1f N m  %6.1f kW   boost %5.1f psi  blower %5.1f kW  turbos", rpm, nm, nm * rpm * rads / 1000, psi, kw);
            for (int i = 0; i < ind.turbos; ++i) printf(" %3.0f%%", spin[i] * 100);
            printf("\n");
        };
        printf("full throttle, steady:\n");
        for (double rpm = 1000; rpm <= d.redlineRpm + 1; rpm += 500) {
            double sum = 0; int n = 0;
            for (double t = 0; t < 4.0; t += dt) {
                es_set_target_rpm(s, rpm);
                es_set_throttle(s, 1.0);
                es_update(s, dt);
                drain();
                if (t > 3.5) { sum += es_get_torque(s); ++n; }
            }
            report(rpm, sum / n);
        }
        // lift-off at ES_STEP_RPM after full boost: when the blow-off valve lifts
        if (getenv("ES_LIFT")) {
            const double at = env("ES_STEP_RPM", 3000);
            for (double t = 0; t < 4.0; t += dt) { es_set_target_rpm(s, at); es_set_throttle(s, 1); es_update(s, dt); drain(); }
            double dummy; es_get_blowoffs(s, &dummy);
            printf("lift at %.0f rpm:\n", at);
            for (double t = 0; t < 0.6; t += 0.005) {
                es_set_target_rpm(s, at); es_set_throttle(s, 0); es_update(s, 0.005); drain();
                double psi; const int n = es_get_blowoffs(s, &psi);
                double boost, kw, spin[ES_MAX_TURBOS]; es_get_induction(s, &boost, spin, &kw);
                if (n > 0 || std::lround(t / 0.005) % 4 == 0) printf("  t %.3f  boost %5.1f psi%s\n", t, boost, n > 0 ? "  BLOW-OFF" : "");
            }
            es_destroy(s);
            return 0;
        }
        const double stepRpm = env("ES_STEP_RPM", 3000);
        printf("throttle step at %.0f rpm:\n", stepRpm);
        for (double t = 0; t < 7.0; t += dt) {
            es_set_target_rpm(s, stepRpm);
            es_set_throttle(s, t < 2.0 ? 0.0 : 1.0);
            es_update(s, dt);
            drain();
            const int i = (int)std::lround(t / dt);
            if (i % 25 == 0) { printf("  t %4.2f", t); report(stepRpm, es_get_torque(s)); }
        }
        es_destroy(s);
        return 0;
    }

    if (sweep) {
        // the speed is held by the "game" here (a dyno run): steady points from 1000 rpm to the limit
        const double boosts[] = { 1.0, env("ES_BOOST", 1.5) };
        es_set_rev_limiter(s, 0, 0);
        for (double boost : boosts) {
            es_set_boost(s, boost);
            printf("boost x%.2f:\n", boost);
            double peakKw = 0, peakNm = 0;
            for (double rpm = 1000; rpm <= d.redlineRpm + 1; rpm += 500) {
                double sum = 0; int n = 0;
                for (double t = 0; t < 1.5; t += dt) {
                    es_set_target_rpm(s, rpm);
                    es_set_throttle(s, 1.0);
                    es_update(s, dt);
                    drain();
                    if (t > 1.0) { sum += es_get_torque(s); ++n; }
                }
                const double nm = sum / n, kw = nm * rpm * rads / 1000;
                peakKw = std::max(peakKw, kw); peakNm = std::max(peakNm, nm);
                printf("  %5.0f rpm  %6.1f N m  %6.1f kW\n", rpm, nm, kw);
            }
            printf("  peak %.0f N m, %.0f kW\n", peakNm, peakKw);
        }
        es_destroy(s);
        return 0;
    }

    if (std::string(argv[3]) == "start") {
        // starting: ES_TRIALS cold starts on the starter (up to 6 s each), how long until it catches
        // and whether it keeps running; ES_LOAD (N m) holds against the starter (cranking in gear)
        const int trials = (int)env("ES_TRIALS", 5);
        const double loadNm = env("ES_LOAD", 0);
        for (int k = 0; k < trials; ++k) {
            es_destroy(s);
            s = es_create(&d, argv[2]);
            es_set_drive_mode(s, 1);
            es_set_idle_rpm(s, d.idleRpm);
            if (getenv("ES_CONDITION")) es_set_condition(s, atof(getenv("ES_CONDITION")));
            es_set_heat(s, env("ES_HEAT", 0));
            const double crankFor = env("ES_CRANK", 6), roll = env("ES_ROLL", 0);
            double omega = 0, caught = -1, crankSum = 0, loadSum = 0, peakLoad = 0;
            int crankN = 0, jammedAt = -1;
            bool starter = true;
            for (int i = 0; i * dt < crankFor + 3; ++i) {
                const double t = i * dt;
                const bool caughtNow = caughtAt(omega / rads, d.idleRpm);
                if (starter && (caughtNow || t > crankFor)) {
                    starter = false;
                    if (caughtNow) caught = t;
                }
                if (starter && es_get_starter_jammed(s) && jammedAt < 0) jammedAt = i;
                // ES_ROLL: a bump / roll start instead: the wheels turn the engine at this speed
                // (clutch let out while rolling) until it catches, then the clutch goes in
                if (roll > 0 && starter) omega = std::min(roll * rads, omega + roll * rads * dt / 0.6);   // clutch bites over ~0.6 s
                es_set_starter(s, starter && roll <= 0);
                es_set_ignition(s, 1);
                es_set_target_rpm(s, omega / rads);
                es_update(s, dt);
                drain();
                if (starter && t > 0.3) { crankSum += omega / rads; loadSum += es_get_starter_load(s); ++crankN; }
                peakLoad = std::max(peakLoad, es_get_starter_load(s));
                const double load = starter ? loadNm : 0;
                omega = std::max(0.0, omega + (es_get_torque(s) - load) / inertia * dt);
            }
            printf("trial %d: %s%s after %.2f s, cranking %.0f rpm, starter load %.2f; at the end %s, %.0f rpm\n", k,
                caught >= 0 ? "caught" : "no start", jammedAt >= 0 ? " (starter gave up)" : "",
                caught >= 0 ? caught : (jammedAt >= 0 ? jammedAt * dt : crankFor),
                crankN ? crankSum / crankN : 0, crankN ? loadSum / crankN : 0, looksLike(omega / rads, d.idleRpm), omega / rads);
        }
        es_destroy(s);
        return 0;
    }

    // drive: crank (up to the game's 2 s start), idle, full throttle in neutral, lift, then a load
    double omega = 0;
    bool starter = true;
    const double load = env("ES_LOAD", 0);
    printf("   t     rpm   state     torque  throttle\n");
    for (int i = 0; i * dt < 20.0; ++i) {
        const double t = i * dt;
        if (starter && (caughtAt(omega / rads, d.idleRpm) || t > 2.0)) starter = false;
        double throttle = 0, loadNm = 0;
        if (t >= 6 && t < 9) throttle = 1;
        if (t >= 13 && t < 15) loadNm = load > 0 ? load : 0;   // clutch dropped in gear without throttle
        const bool rolled = t >= 16 && t < 17;                  // roll start: the wheels turn it, key on
        if (rolled) omega = 700 * rads;
        es_set_starter(s, starter);
        es_set_ignition(s, 1);   // the key stays on through the stall
        es_set_throttle(s, throttle);
        es_set_target_rpm(s, omega / rads);
        es_update(s, dt);
        drain();

        // the game's engine: integrate speed from the engine's torque (friction is already in it)
        const double torque = es_get_torque(s);
        omega = std::max(0.0, omega + (torque - loadNm) / inertia * dt);
        omega = std::min(omega, d.redlineRpm * 1.05 * rads);
        if (i % 25 == 0)
            printf("%5.2f %7.0f   %-9s %7.1f  %.2f%s\n", t, omega / rads, looksLike(omega / rads, d.idleRpm), torque, throttle,
                starter ? "  starter" : (loadNm > 0 ? "  load" : (rolled ? "  rolled" : "")));
    }
    if (argc >= 5) writeWav(argv[4], pcm);
    es_destroy(s);
    return 0;
}
