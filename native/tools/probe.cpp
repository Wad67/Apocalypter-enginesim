// Offline: what is in a cylinder's exhaust runner through idle, full throttle, the limiter and a lift-off.
#include "enginesim_api.h"
#include "test_engines.h"
#include <cstdio>
#include <cstdlib>
#include <vector>
int main(int argc, char **argv) {
    es_engine_desc d;
    if (argc < 3 || !engineFromName(argv[1], &d)) return 1;
    d.idleRpm = 900; d.redlineRpm = 5000;
    es_session *s = es_create(&d, argv[2]);
    es_set_volume(s, 1); es_set_idle_air(s, 0.08); es_set_rev_limiter(s, 5000, 0.08);
    if (getenv("ES_CONDITION")) es_set_condition(s, atof(getenv("ES_CONDITION")));
    std::vector<int16_t> buf(16384);
    const double dt = 1 / 120.0;
    double maxFuel = 0, maxT = 0, sumFuel = 0; int n = 0, a10 = 0, a14 = 0, a18 = 0; const char *phase = "";
    for (int i = 0; i * dt < 9; ++i) {
        double t = i * dt, rpm, thr; const char *ph;
        if (t < 2) { rpm = 900; thr = 0; ph = "idle"; } else if (t < 5) { rpm = 900 + 4100 * (t - 2) / 3; thr = 1; ph = "WOT"; }
        else if (t < 7) { rpm = 5000; thr = 1; ph = "limiter"; } else { rpm = 5000 - 3000 * (t - 7) / 2; thr = 0; ph = "lift-off"; }
        if (ph != phase) { if (n) printf("%-9s fuel max %.4f mean %.4f, T max %.0f K, >0.010 %.1f%% >0.014 %.1f%% >0.018 %.1f%%\n", phase, maxFuel, sumFuel / n, maxT, 100.0*a10/n, 100.0*a14/n, 100.0*a18/n); phase = ph; maxFuel = maxT = sumFuel = 0; n = a10 = a14 = a18 = 0; }
        es_set_target_rpm(s, rpm); es_set_throttle(s, thr); es_update(s, dt);
        while (es_read(s, buf.data(), (int)buf.size()) > 0) {}
        { double fl = 0, fu = 0, te = 0; es_get_exhaust(s, &fl, &fu, &te); if (i % 60 == 0) printf("  t %.1f rpm %.0f flow %.2f mol/s fuel %.4f T %.0f\n", t, rpm, fl, fu, te); }
        for (int c = 0; c < d.engine.cylinders; ++c) { double f = 0, T = 0, m = 0; es_probe_exhaust(s, c, &f, &T, &m); maxFuel = f > maxFuel ? f : maxFuel; maxT = T > maxT ? T : maxT; sumFuel += f; ++n; a10 += f > 0.010; a14 += f > 0.014; a18 += f > 0.018; }
    }
    printf("%-9s fuel max %.4f mean %.4f, T max %.0f K, >0.010 %.1f%% >0.014 %.1f%% >0.018 %.1f%%\n", phase, maxFuel, sumFuel / n, maxT, 100.0*a10/n, 100.0*a14/n, 100.0*a18/n);
    es_destroy(s);
}
