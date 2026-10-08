#include "../include/leveling_filter.h"

#include <cmath>

LevelingFilter::LevelingFilter() {
    m_peak = 30000.0;
    m_attenuation = 1.0;
    p_target = 30000.0;
    p_minLevel = 0.0;
    p_maxLevel = 1.0;
}

LevelingFilter::~LevelingFilter() {
    /* void */
}

float LevelingFilter::f(float sample) {
    // Engine Sim: a slow level control instead of a per-pulse compressor. Upstream let the
    // peak decay by 0.999 per sample (~23 ms), about one firing interval of a V8 at idle, so
    // the gain pumped up between pulses and each pulse hit it: clipped, distorted idle.
    // Peaks are still caught at once (no clipping); the release takes ~0.5 s.
    const float a = std::abs(sample);
    m_peak = (a > m_peak) ? a : 0.99995f * m_peak;

    if (m_peak == 0) return 0;

    const float raw_attenuation = p_target / m_peak;

    float attenuation = raw_attenuation;
    if (attenuation < p_minLevel) attenuation = p_minLevel;
    else if (attenuation > p_maxLevel) attenuation = p_maxLevel;

    // gain down at once (the peak is tracked at once, so the output can't overshoot the
    // target and clip), up slowly
    if (attenuation < m_attenuation) m_attenuation = attenuation;
    else m_attenuation += 0.0005f * (attenuation - m_attenuation);

    return sample * m_attenuation;
}
