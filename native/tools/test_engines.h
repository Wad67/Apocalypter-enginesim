// Offline tools only: an engine from the game's part names.
#ifndef ENGINESIM_TEST_ENGINES_H
#define ENGINESIM_TEST_ENGINES_H

#include "enginesim_api.h"

#include <cctype>
#include <cstdlib>
#include <cstring>
#include <string>

// Test convenience only (the plugin identifies engines by part id): reads the game's part names,
// "2.8L V6 115HP 183Nm Gasoline", "594cc I2 23HP 39Nm Gasoline", into the five basic values.
inline bool engineFromName(const char *name, es_engine_desc *out) {
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

#endif
