// Offline test: drives a session the way the plugin will (fixed-rate frames,
// a consumer that reads in real time) and writes the audio to a WAV file.
//   render_wav "<engine part name>" out.wav <ir dir> [exhaust ir.wav flow length_m header_scale]
// Scenario: crank and idle 3 s, full-throttle sweep to the rev limit 5 s, on the limiter 1 s, coast down 3 s.
// Environment: ES_CONDITION, ES_IDLE, ES_REDLINE, ES_IDLE_AIR, ES_LIMITER (cut s, 0 = off), ES_BOOST,
// ES_FLUID_STEPS, ES_NOISE, ES_JITTER, ES_IDLE_THROTTLE, ES_NO_IGNITION, ES_RPMSTATS.
#include "enginesim_api.h"

#include <algorithm>
#include <cctype>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

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

// Test convenience only (the plugin identifies engines by part id): reads the game's part names,
// "2.8L V6 115HP 183Nm Gasoline", "594cc I2 23HP 39Nm Gasoline", into the five basic values.
static bool engineFromName(const char *name, es_engine_desc *out) {
    int cylinders = 0, layout = ES_LAYOUT_INLINE, diesel = 0;
    float litres = 0;
    std::string s(name);
    size_t i = 0;
    while (i < s.size()) {
        while (i < s.size() && isspace((unsigned char)s[i])) ++i;
        size_t j = i;
        while (j < s.size() && !isspace((unsigned char)s[j])) ++j;
        std::string tok;
        for (char c : s.substr(i, j - i)) tok += (char)tolower((unsigned char)c);
        i = j;
        if (tok.empty()) continue;
        char *end = nullptr;
        const double num = strtod(tok.c_str(), &end);
        const std::string suffix = (end != nullptr) ? std::string(end) : "";
        if (end != tok.c_str() && suffix == "l") litres = (float)num;
        else if (end != tok.c_str() && suffix == "cc") litres = (float)(num / 1000.0);
        else if (tok.rfind("diesel", 0) == 0) diesel = 1;
        else if (tok.size() >= 2 && strchr("ivbh", tok[0]) != nullptr && isdigit((unsigned char)tok[1])) {
            cylinders = atoi(tok.c_str() + 1);
            layout = (tok[0] == 'i') ? ES_LAYOUT_INLINE : (tok[0] == 'v') ? ES_LAYOUT_V : ES_LAYOUT_BOXER;
        }
    }
    const float bank = (layout != ES_LAYOUT_V) ? 0.0f : (cylinders == 2 ? 45.0f : (cylinders == 6 || cylinders == 12) ? 60.0f : 90.0f);
    memset(out, 0, sizeof(*out));
    if (!es_default_params(cylinders, layout, bank, litres, diesel, &out->engine)) return false;
    out->redlineRpm = diesel ? 4000.0f : 6000.0f;
    out->idleRpm = diesel ? 750.0f : 850.0f;
    return true;
}

int main(int argc, char **argv) {
    if (argc < 4) { fprintf(stderr, "usage: render_wav \"<engine name>\" out.wav <ir dir> [exhaust ir.wav flow length_m header_scale]\n"); return 2; }
    es_engine_desc d;
    if (!engineFromName(argv[1], &d)) { fprintf(stderr, "cannot read '%s'\n", argv[1]); return 1; }
    printf("%s: %d cyl layout=%d V=%.0f %.3f L diesel=%d redline=%.0f idle=%.0f\n",
        argv[1], d.engine.cylinders, d.engine.layout, d.engine.bankAngleDeg, d.engine.displacementL, d.engine.diesel, d.redlineRpm, d.idleRpm);

    if (const char *v = getenv("ES_NOISE")) d.airNoise = (float)atof(v);
    if (const char *v = getenv("ES_JITTER")) d.jitter = (float)atof(v);
    if (const char *v = getenv("ES_FLUID_STEPS")) d.fluidSteps = atoi(v);
    if (argc >= 8) {
        snprintf(d.exhaustIr, sizeof(d.exhaustIr), "%s", argv[4]);
        d.exhaustFlow = (float)atof(argv[5]);
        d.exhaustLengthM = (float)atof(argv[6]);
        d.headerScale = (float)atof(argv[7]);
        printf("exhaust: %s flow %.2f length %.2f m headers x%.2f\n", d.exhaustIr, d.exhaustFlow, d.exhaustLengthM, d.headerScale);
    }

    if (const char *v = getenv("ES_IDLE")) d.idleRpm = (float)atof(v);
    if (const char *v = getenv("ES_REDLINE")) d.redlineRpm = (float)atof(v);
    const double idleThrottle = getenv("ES_IDLE_THROTTLE") ? atof(getenv("ES_IDLE_THROTTLE")) : 0.0;
    es_session *s = es_create(&d, argv[3]);
    if (!s) { fprintf(stderr, "es_create: %s\n", es_last_error()); return 1; }
    if (const char *c = getenv("ES_CONDITION")) { es_set_condition(s, atof(c)); printf("condition %.2f\n", atof(c)); }

    // scenario: crank + idle 3 s, full-throttle sweep idle -> redline 5 s, hold 1 s, lift and fall 3 s
    const double dt = 1 / 60.0, total = 12.0;
    std::vector<int16_t> pcm, chunk(8192);
    double owed = 0;
    long underrun = 0;
    auto t0 = std::chrono::steady_clock::now();
    for (int frame = 0; frame * dt < total; ++frame) {
        const double t = frame * dt;
        double rpm, thr;
        if (t < 3) { rpm = d.idleRpm; thr = idleThrottle; }
        else if (t < 8) { rpm = d.idleRpm + (d.redlineRpm - d.idleRpm) * (t - 3) / 5; thr = 1.0; }
        else if (t < 9) { rpm = d.redlineRpm; thr = 1.0; }
        else { rpm = d.redlineRpm + (d.idleRpm - d.redlineRpm) * std::min(1.0, (t - 9) / 3); thr = 0.0; }
        es_set_target_rpm(s, rpm);
        if (getenv("ES_NO_IGNITION")) es_set_ignition(s, 0);
        if (getenv("ES_IDLE_AIR")) es_set_idle_air(s, atof(getenv("ES_IDLE_AIR")));
        const double limiterCut = getenv("ES_LIMITER") ? atof(getenv("ES_LIMITER")) : 0.08;
        if (limiterCut > 0) es_set_rev_limiter(s, d.redlineRpm, limiterCut);
        if (getenv("ES_BOOST")) es_set_boost(s, t >= 3 && t < 9 ? atof(getenv("ES_BOOST")) : 1.0);
        es_set_throttle(s, thr);
        es_update(s, dt);

        // consumer: exactly real-time worth of audio per frame
        owed += dt * ES_SAMPLE_RATE;
        int want = (int)owed;
        owed -= want;
        while (want > 0) {
            const int got = es_read(s, chunk.data(), std::min(want, (int)chunk.size()));
            if (got <= 0) break;
            pcm.insert(pcm.end(), chunk.begin(), chunk.begin() + got);
            want -= got;
        }
        if (want > 0) { underrun += want; pcm.insert(pcm.end(), want, 0); }

        if (getenv("ES_RPMSTATS") && t > 1.0 && t < 3.0) {
            static double sum = 0, sum2 = 0, lo = 1e9, hi = 0; static int k = 0;
            const double r = es_get_rpm(s); sum += r; sum2 += r * r; lo = std::min(lo, r); hi = std::max(hi, r); ++k;
            if (t + dt >= 3.0) printf("idle rpm: mean %.0f  sd %.1f  min %.0f  max %.0f\n", sum / k, std::sqrt(sum2 / k - (sum / k) * (sum / k)), lo, hi);
        }
        if (frame % 60 == 0) {
            printf("t=%5.2f target=%5.0f rpm=%7.1f v_theta=%8.2f proc=%6.0fus\n",
                t, rpm, es_get_rpm(s), es_get_crank_speed(s), es_get_processing_time_us(s));
        }
    }
    const double wall = std::chrono::duration<double>(std::chrono::steady_clock::now() - t0).count();
    printf("simulated %.1f s in %.2f s wall (%.2fx real time), underrun %ld samples (%.1f%%)\n",
        total, wall, total / wall, underrun, 100.0 * underrun / std::max<size_t>(1, pcm.size()));

    writeWav(argv[2], pcm);
    es_destroy(s);
    return 0;
}
