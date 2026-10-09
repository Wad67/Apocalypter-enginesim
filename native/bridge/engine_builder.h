#ifndef ENGINESIM_ENGINE_BUILDER_H
#define ENGINESIM_ENGINE_BUILDER_H

#include "enginesim_api.h"

#include "engine.h"
#include "vehicle.h"
#include "transmission.h"

#include <string>
#include <vector>

class Function;
class Camshaft;
class Valvetrain;
class ImpulseResponse;

// Everything the builder allocates. engine-sim's parts reference these by
// pointer and never free them (the script compiler leaks them), so we do.
struct BuiltEngine {
    Engine *engine = nullptr;
    Vehicle *vehicle = nullptr;
    Transmission *transmission = nullptr;

    std::vector<Function *> functions;
    std::vector<Camshaft *> camshafts;
    std::vector<Valvetrain *> valvetrains;
    std::vector<ImpulseResponse *> impulseResponses;

    void destroy();
};

// Generic parameters from an engine's five basic values, for the keys an engines.cfg section leaves out.
bool defaultParams(int cylinders, int layout, float bankAngleDeg, float displacementL, int diesel, es_engine_params *out);

// Builds an engine from its parameters, in place of engine-sim's .mr scripts
// (mirrors es_script::EngineNode::buildEngine). irDirectory is only used to
// form impulse-response file names; nothing is read here.
bool buildEngine(const es_engine_desc &desc, const std::string &irDirectory, BuiltEngine *out, std::string *error);

// Gas-dynamics sub-steps per physics step: the requested quality, but never below the engine's
// minimum gas steps per second at this physics rate.
int stableFluidSteps(const es_engine_desc &desc, double simulationFrequency, int requested);

#endif /* ENGINESIM_ENGINE_BUILDER_H */
