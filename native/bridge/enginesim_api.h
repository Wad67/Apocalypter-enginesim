#ifndef ENGINESIM_API_H
#define ENGINESIM_API_H

/*
 * C API around engine-sim for the Engine Sim BepInEx plugin.
 *
 * Threading: a session is NOT thread-safe. Every call on a session must come
 * from one thread (the plugin's sim thread). The synthesizer's own rendering
 * thread is never started; es_update() runs the simulation and renders the
 * audio on the calling thread. es_default_params() is pure and can be
 * called from anywhere.
 *
 * Audio: es_read() returns mono int16 at ES_SAMPLE_RATE (fixed by engine-sim).
 */

#include <stdint.h>

#ifdef _WIN32
#define ES_API __declspec(dllexport)
#else
#define ES_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define ES_SAMPLE_RATE 44100

enum es_layout {
    ES_LAYOUT_INLINE = 0,
    ES_LAYOUT_V = 1,
    ES_LAYOUT_BOXER = 2
};

#define ES_MAX_CYLINDERS 16
#define ES_MAX_BANKS 2
#define ES_PORT_FLOW_SAMPLES 15   /* port flow every 50 thou (1.27 mm) of valve lift, 0 - 700 thou */
#define ES_MAX_TIMING_SAMPLES 12

/* Everything engine-sim is built from, per engine. Plain C (4-byte members, fixed arrays) so it
   marshals as-is. Per-cylinder arrays are indexed by cylinder number - 1. es_default_params()
   fills it with generic values from the five basic ones. Units: mm, cc, L, g, kg, Nm, cm2, degrees, rpm; flows are
   cfm (at 28 inH2O for ports and blowby, through a carburettor for the rest). */
typedef struct es_engine_params {
    int32_t cylinders;
    int32_t layout;                     /* es_layout */
    float bankAngleDeg;                 /* angle between the banks of a V */
    float displacementL;                /* only used by es_default_params */
    int32_t diesel;                     /* 0 = gasoline */

    /* firing: crank angle each cylinder fires at within the 720 degree cycle, and its bank */
    float firingAngleDeg[ES_MAX_CYLINDERS];
    int32_t cylinderBank[ES_MAX_CYLINDERS];

    /* bottom end */
    float boreMm, strokeMm, rodLengthMm, compressionHeightMm, compressionRatio;
    float pistonMassG, rodMassG;
    float crankMassKg, flywheelMassKg, flywheelRadiusMm, frictionTorqueNm;
    float starterTorqueNm, starterSpeedRpm;
    float blowbyCfm;

    /* intake */
    float plenumVolumeL, plenumAreaCm2, intakeFlowCfm, runnerFlowCfm, runnerLengthMm;
    float idleThrottlePlate;            /* closed throttle plate position, 0..1 (higher = less idle air) */
    float throttleGamma, intakeVelocityDecay;

    /* heads and cams */
    float intakePortCfm[ES_PORT_FLOW_SAMPLES];
    float exhaustPortCfm[ES_PORT_FLOW_SAMPLES];
    float headFlowScale[ES_MAX_BANKS];
    float intakeRunnerVolumeCc, intakeRunnerAreaCm2, exhaustRunnerVolumeCc, exhaustRunnerAreaCm2;
    float intakeCamDurationDeg, intakeCamLiftMm, intakeCamGamma, intakeLobeCenterDeg;
    float exhaustCamDurationDeg, exhaustCamLiftMm, exhaustCamGamma, exhaustLobeCenterDeg;
    float camBaseRadiusMm;

    /* exhaust manifold (an exhaust part adds to it) */
    float collectorDiameterMm, outletFlowCfm, primaryLengthMm, primaryFlowCfm, exhaustVelocityDecay;
    float exhaustVolume[ES_MAX_BANKS];          /* sound level of each bank's exhaust */
    float headerLengthMm[ES_MAX_CYLINDERS];     /* per cylinder, sound path to the collector */
    float soundAttenuation[ES_MAX_CYLINDERS];   /* per cylinder */

    /* ignition */
    int32_t timingSamples;
    float timingRpm[ES_MAX_TIMING_SAMPLES], timingAdvanceDeg[ES_MAX_TIMING_SAMPLES];

    /* combustion and simulation */
    float burningEfficiency;
    float simulationFrequency;          /* physics steps per second */
    float minGasStepsPerSecond;         /* gas dynamics steps per second the cylinder size needs to stay stable */
    float airNoise, jitter;             /* synthesizer; 0 = chosen to suit the impulse response */
    float mixture;                      /* air-fuel ratio scale on engine-sim's power mixture (lambda 0.8); 0 = 1 */
} es_engine_params;

/* An engine to simulate: its parameters, the game's idle and rev limit, and the fitted exhaust. */
typedef struct es_engine_desc {
    es_engine_params engine;
    float redlineRpm;
    float idleRpm;

    /* Exhaust. Zero / empty = defaults chosen from the engine. */
    char exhaustIr[64];      /* impulse-response file name inside irDirectory */
    float exhaustFlow;       /* outlet flow scale (restriction): 1 = default, higher = freer */
    float exhaustLengthM;    /* pipe length after the collector, metres */
    float headerScale;       /* primary (header) tube length scale */
    float airNoise;          /* synthesizer air noise 0..1, overrides the engine's; 0 = the engine's */
    float jitter;            /* synthesizer input-sample jitter 0..1, overrides the engine's; 0 = the engine's */
    int32_t fluidSteps;      /* gas dynamics sub-steps per physics step: quality vs CPU (engine-sim: 8) */
} es_engine_desc;

typedef struct es_session es_session;

/* Fills every parameter of an engine with generic values from its cylinder count, layout, bank angle
   (V only), displacement and fuel. Returns 1 on success. Pure, callable from any thread. */
ES_API int32_t es_default_params(int32_t cylinders, int32_t layout, float bankAngleDeg, float displacementL,
    int32_t diesel, es_engine_params *out);

/* Builds the engine and the simulator. irDirectory holds the impulse-response .wav files.
   Returns NULL on failure; es_last_error() says why. */
ES_API es_session *es_create(const es_engine_desc *desc, const char *irDirectory);
ES_API void es_destroy(es_session *s);

/* Crank speed the dyno holds, rpm. <= 0 releases the dyno (the engine runs free). */
ES_API void es_set_target_rpm(es_session *s, double rpm);
/* 0..1 */
ES_API void es_set_throttle(es_session *s, double throttle);
ES_API void es_set_ignition(es_session *s, int32_t on);
/* Engine condition 0..1 (the game's percentage / 100). Worn engines leak past the rings,
   burn less evenly, stumble on misfires (from 70 % down, worst at idle and on one cylinder)
   and hunt at idle; 4+ cylinder engines lose whole (silent) cylinders under 50 / 30 / 10 % (the last
   only on 6+ cylinders). Default 1. */
ES_API void es_set_condition(es_session *s, double condition);
/* Rev limiter: at rpm (the game's limit) under throttle the sim cuts sparks for cutSeconds
   and lets the rpm bounce, as an ignition-cut limiter. rpm <= 0 disables it. */
ES_API void es_set_rev_limiter(es_session *s, double rpm, double cutSeconds);
/* Forced induction: intake air pressure as a ratio to atmospheric (1 = none). */
ES_API void es_set_boost(es_session *s, double pressureRatio);
/* Throttle floor the engine idles on (0..0.3); the game's throttle is scaled above it. */
ES_API void es_set_idle_air(es_session *s, double idleAir);
/* Scales how much condition matters: 0 = always as new, 1 = default, 2 = twice as harsh. */
ES_API void es_set_wear_intensity(es_session *s, double intensity);
/* Simulation lead over the synthesizer, seconds (engine-sim default 0.1). Lower = less lag, more risk of gaps. */
ES_API void es_set_target_latency(es_session *s, double seconds);
/* Output gain, 0..1+ (engine-sim's synthesizer volume). */
ES_API void es_set_volume(es_session *s, double volume);

/* Advances the simulation by dt seconds of wall time and renders the resulting audio.
   Returns the number of samples waiting to be read. */
ES_API int32_t es_update(es_session *s, double dt);
/* Copies up to max samples into dst; returns how many were copied (never pads with silence). */
ES_API int32_t es_read(es_session *s, int16_t *dst, int32_t max);

ES_API double es_get_rpm(es_session *s);
/* Signed crank speed, rad/s (negative = the starter's direction). */
ES_API double es_get_crank_speed(es_session *s);
/* Average physics time per frame, microseconds. */
ES_API double es_get_processing_time_us(es_session *s);

ES_API const char *es_last_error(void);

/* Drive mode: instead of following the game's engine, the engine powers it. The crank is held at
   the speed set by es_set_target_rpm (the game's engine speed) and es_get_torque() reports the
   torque the engine makes there, for the game's drivetrain to run on. The key (es_set_ignition), the
   starter (es_set_starter) and the fuel supply (es_set_fuel) are its inputs; an idle governor holds
   es_set_idle_rpm. There is no running state: it runs, stalls, catches or starves as simulated.
   Wear and the rev limiter act on torque. */
ES_API void es_set_drive_mode(es_session *s, int32_t on);
ES_API void es_set_starter(es_session *s, int32_t on);
ES_API void es_set_idle_rpm(es_session *s, double rpm);
/* Variable intake cam timing on (1) or off (0): the intake cams phased with rpm and load while running. */
ES_API void es_set_vvt(es_session *s, int32_t on);
/* Gas dynamics sub-steps per physics step while running (quality vs CPU), kept at or above the stable floor. */
ES_API void es_set_fluid_steps(es_session *s, int32_t steps);
/* Engine torque at the crank, N m, smoothed over about half an engine cycle (+ = driving). */
ES_API double es_get_torque(es_session *s);
/* Fuel reaching the engine (0 = the tank is empty: it starves). */
ES_API void es_set_fuel(es_session *s, int32_t available);
/* Low-rpm filter on the engine sound (not on the effects): a low-pass whose cutoff rises from
   crankHz below a fifth of idle to idleHz at idle, wide open at openAt x idle. crankHz <= 0 = off. */
ES_API void es_set_lowpass(es_session *s, double crankHz, double idleHz, double openAt, double idleRpm);
/* Effects: backfire detection (gain 0 = off; chance an unburnt slug in a hot runner backfires; the
   game plays them, see es_get_backfires) and valve tick (gain, 0 = off). */
ES_API void es_set_effects(es_session *s, double backfireGain, double backfireChance, double valveGain);
/* Exhaust since the last call, for the game's smoke: flow out of the cylinders (mol/s), its fuel
   fraction (unburnt fuel), and the exhaust runners' mean temperature (K). */
ES_API void es_get_exhaust(es_session *s, double *flow, double *fuel, double *temperature);
/* Backfires since the last call (for the game's exhaust flash). */
ES_API int32_t es_get_backfires(es_session *s);
/* Engine torque at the crank over the last update, N m, without the cycle smoothing (for the
   engine rocking on its mounts). */
ES_API double es_get_torque_raw(es_session *s);
/* Diagnostics: a cylinder's exhaust runner: fuel fraction of its gas, temperature (K), moles. */
ES_API void es_probe_exhaust(es_session *s, int32_t cylinder, double *fuel, double *temperature, double *moles);
/* How hard the starter is working (a DC motor): its current / its stall current, 1 at rest falling to
   0 at its free speed (0 when off). */
ES_API double es_get_starter_load(es_session *s);
/* The starter gave up: held against more than it can turn, its solenoid only clicks (until it is
   released). */
ES_API int32_t es_get_starter_jammed(es_session *s);
/* Starting: hardStart 0..1 (0 = engine-sim's every charge lights from the first turn; 1 = priming,
   and charges failing to light on a cold or worn engine's slow crank), and whether the starter gives
   up when it can't turn the engine. */
ES_API void es_set_starting(es_session *s, double hardStart, int32_t starterGivesUp);

/* Forced induction as machines (drive mode): a supercharger on the crank and turbos in the exhaust,
   their boost from what they deliver against what the engine draws (see induction.h). Sizes are
   relative, 1 = a medium unit; 0 superchargerSize / 0 turbos = none. With both, the turbos feed the
   supercharger. Replaces es_set_boost while any is fitted. */
#define ES_MAX_TURBOS 4
typedef struct es_induction_desc {
    float superchargerSize;
    float superchargerReliefPsi;        /* its relief valve, psi over ambient */
    int32_t turbos;
    float turboSize[ES_MAX_TURBOS];
    float wastegatePsi;                 /* psi over ambient */
} es_induction_desc;
ES_API void es_set_induction(es_session *s, const es_induction_desc *desc);
/* Boost after the last compressor, psi over ambient (negative: under); each turbo's shaft speed as a
   fraction of its top speed (ES_MAX_TURBOS values); the power driving the supercharger, kW. */
ES_API void es_get_induction(es_session *s, double *boostPsi, double *turboSpeed, double *superchargerKw);
/* Blow-off valve openings since the last call, and the boost it let go of the last time, psi. */
ES_API int32_t es_get_blowoffs(es_session *s, double *psi);
/* Engine temperature for starting, 0 cold .. 1 warm (the game's engine temperature). */
ES_API void es_set_heat(es_session *s, double heat);
/* The simulated crank and flywheel's moment of inertia, kg m2. */
ES_API double es_get_crank_inertia(es_session *s);

/* sizeof(es_engine_desc), so the plugin can check its copy of the struct matches. */
ES_API int32_t es_desc_size(void);

#ifdef __cplusplus
}
#endif

#endif /* ENGINESIM_API_H */
