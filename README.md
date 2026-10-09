# Engine Sim

A BepInEx 5 plugin for Apocalypter that replaces the game's recorded engine sounds with a live physical
simulation from [engine-sim](https://github.com/ange-yaghi/engine-sim) by Ange Yaghi (AngeTheGreat).

Every running engine near you is simulated: pistons, valves, combustion and exhaust gas flow, turned into
sound as it happens. The simulated engine is built from the engine fitted to the car, so a 594cc twin, a
2.8L V6 and a 7L V8 each sound like themselves.

## Features

- **Built from the fitted engine.** Each engine part has a line in an engine table (cylinders, layout, bank angle,
  displacement, fuel); idle and rev limit come from the game's engine. Inline, V and boxer layouts with their usual
  firing orders; the game's V-twin is set up after engine-sim's Harley Shovelhead.
- **Follows the game.** RPM, throttle and ignition come from the game's engine every frame. The simulated crank
  is held at the game's RPM, so the sound always matches what the car is doing.
- **Rev limiter.** At the game's rev limit the simulation cuts the ignition and lets the engine bounce off the
  limiter, instead of sitting pegged at the limit.
- **Engine condition.** A worn engine leaks past its rings, burns unevenly, stumbles on misfires (from 70 %
  condition down, worst at idle) and hunts at idle. Engines with four or more cylinders lose whole cylinders:
  one under 50 %, two under 30 %, and on six or more cylinders a third under 10 %.
- **Forced induction.** The boost of the game's (NWH) forced induction raises the simulated intake pressure.
- **Exhausts.** The fitted exhaust changes the sound. Every exhaust is set relative to a base sound with no
  exhaust, and both can be tuned without rebuilding.
- **Turbo and supercharger sound.** The game's own spool and whine clips, re-proportioned (optional), one sound
  per turbo: each part's turbos are listed by size (Triple Threat: two small and one large). A turbo spools with
  rpm and load, lags low in the revs and coasts down after a lift; small ones spool sooner and whistle higher. A
  supercharger's whine follows the engine speed. Stock, both top out at half the rev range. The wastegate
  flutter is left as it is.
- **Only the engine sound is replaced.** Wind, reverse whine and beep, gear changes, muffler crackle, turbo
  whistle and the rest of the game's vehicle sounds are untouched.
- All settings can be changed in game through the Apocasetter Mods menu
  ([Apocasetter index](https://github.com/DeonUrist/Apocasetter-Index)).

## Requirements

- Apocalypter with [BepInEx 5](https://github.com/BepInEx/BepInEx/releases) (x64).
- A reasonably modern CPU. Each simulated engine runs on its own thread and uses most of a core;
  `MaxEngines` (default 2) limits how many run at once.

## Installation

Unzip the release into `Apocalypter\BepInEx\plugins`:

```
BepInEx\plugins\EngineSim\
    EngineSim.dll            the plugin
    enginesim_native.dll     engine-sim and the engine builder (native, x64)
    icon.png
    ir\*.wav                 exhaust impulse responses
```

## Settings

`BepInEx\config\com.burner.apocalypter.enginesim.cfg`, or in game through Apocasetter.

| Section | Setting | Default | |
|---|---|---|---|
| General | `Enabled` | `true` | Off = the game's own engine sounds. |
| Performance | `MaxEngines` | `2` | Engines simulated at once, nearest first. The rest keep the stock sound. |
| Performance | `SimQuality` | `Balanced` | Gas-flow detail: `High` (engine-sim's own setting), `Balanced`, `Fast`. Lower it if the sound breaks up. |
| Audio | `Volume` | `0.8` | |
| Audio | `DistanceAttenuation` | `1` | How much engines and turbo / supercharger sounds fade with distance: 1 = the game's rolloff, 0 = none. |
| Audio | `DistanceScale` | `1` | Scales the game's rolloff distances; above 1 sounds carry further. |
| Audio | `Latency` | `0.05` | How far (s) the simulation runs ahead of what you hear. |
| Audio | `WearIntensity` | `1` | How much engine condition is heard. 0 = always new, 2 = twice as harsh. |
| Audio | `IdleAir` | `0.08` | Air the engine idles on. Without it the idle is the engine being turned over, not running. |
| Exhaust | `BaseImpulseResponse` | `mild_exhaust.wav` | The exhaust acoustics every engine is played through. |
| Exhaust | `BaseOutletFlow` | `1.0` | 0.8 - 1.2. |
| Exhaust | `BasePipeLength` | `0.15` | Pipe length with no exhaust fitted, 0.15 - 0.5 m. |
| Exhaust | `BaseHeaderLength` | `1.0` | 0.5 - 1.0. |
| Exhaust | `BaseLoudness` | `1.0` | |
| Exhaust | `BaseAirNoise`, `BaseJitter` | `0` | 0 = chosen to suit the impulse response. |
| Induction | `ReplaceSpoolSound` | `true` | Off = the game's own turbo / supercharger sound. |
| Induction | `TurboVolume`, `SuperchargerVolume` | `0.5` | Volume fully spooled under load (the game's own is ~0.05). |
| Induction | `TurboPitchLow`, `TurboPitchHigh` | `0.3`, `0.8` | Pitch of a size 1 turbo barely turning and fully spooled. |
| Induction | `TurboFullSpoolRpm` | `0.65` | Fraction of the rev limit at which a size 1 turbo spools fully at full throttle. |
| Induction | `TurboSpinDown` | `0.8` | Seconds for a size 1 turbo to coast down after a lift. Spool-up time is the game's. |
| Induction | `SuperchargerPitch` | `0.5` | Whine pitch of a size 1 blower at the rev limit, proportional to rpm below it. |
| Debug | `VerboseLog` | `false` | Engine and exhaust identification, live statistics and part details in `LogOutput.log`. |

The exhaust ranges are the limits of engine-sim's exhaust model: outside them it oscillates (a metallic squeal
at idle) or rasps.

### Engine and exhaust tables

Parts are identified by their prefab's Easy Save id (`ES3Prefab.prefabId`), a number the game stores with every
part; names are never read. A vehicle's fitted parts are the parts under it without a Rigidbody (the game removes
a part's Rigidbody when it is fitted). Both tables are written on first start and re-read when they change; edits
apply to the next engine that starts. The text after `#` is only a label.

`BepInEx\config\EngineSim.engines.cfg` holds everything engine-sim builds each engine from, one section per
engine part. The game keeps an engine's power, idle, rev limit and inertia as data (those are read from the
vehicle); its cylinders, displacement and everything below exist only here:

```
[7876660059110608148]   # 2.8L V6 115HP 183Nm Gasoline
cylinders = 6
layout = V
bank_angle = 60
displacement = 2.8
fuel = gasoline
firing_angles = 0, 120, 240, 360, 480, 600
bore = 84.0696
stroke = 84.0696
intake_cam_duration = 226
intake_lobe_center = 116
timing = 0:12, 1000:12, 2000:20, 3000:26, 4000:30, 5000:34, 6000:38, 7000:38, 8000:38
...
```

Firing order and banks, bore and stroke, rods, pistons, crank and flywheel, intake, port flow, cams (duration,
lift, shape, lobe centres), exhaust manifold, per-cylinder header lengths, ignition timing, combustion and
simulation rate. The file lists every key with its unit. A section needs only the five basic keys (`cylinders`,
`layout`, `bank_angle`, `displacement`, `fuel`); any other key left out gets a generic value worked out from them. An engine that
isn't listed keeps the stock sound; the log then lists the vehicle's unlisted fitted parts with their ids. Delete
the file to get the defaults back.

`BepInEx\config\EngineSim.exhausts.cfg` says how each exhaust part differs from the base sound:

```
# part id = impulse response, flow x, pipe + m, headers x, loudness x [, air noise, jitter]
none                 = -, 1.0,  0.00, 1.0, 1.0
default              = -, 0.95, 0.25, 1.0, 0.85
7511643549129607125  = -, 1.2,  0.35, 1.0, 0.9     # exhaust_quad_big
```

`none` is no exhaust fitted, `default` an exhaust part that isn't listed. `-` keeps the base impulse response.

`BepInEx\config\EngineSim.induction.cfg` lists each turbo and supercharger part's units by relative size (1 = a
medium single unit; more boost, a bigger unit):

```
# part id = size [, size ...]
865075917467842793   = 0.6, 0.6, 1.4     # Turbo - Triple Threat - Power x1.7 (two small, one large)
8589483064466669470  = 0.85, 0.85        # Turbo - Double Trouble - Power x1.5 (twin)
1280457821324750492  = 0.8               # Supercharger - Med Mark - Power x1.2
```

## How it works

The native library (`native/`) contains engine-sim's simulator and synthesizer, without its application, UI or
scripting language, plus a builder that assembles an engine in code from the part description
(`native/bridge/engine_builder.cpp`: bore and stroke from the displacement, crank and cam timing from the layout's
firing order, heads, intake, exhaust and ignition), and a small C API (`native/bridge/enginesim_api.h`).

Each simulated engine runs in audio-only mode with the transmission in neutral. engine-sim's dynamometer holds the
crank at the game's RPM; throttle, ignition, condition and boost are the game's. The simulation and the
synthesizer run together on one thread per engine and fill a lock-free ring buffer. Unity's audio thread only
reads that buffer (`AudioOutput.cs`) and resamples it from 44.1 kHz to the output rate. The sound is attenuated by
distance with the game's own rolloff settings, without panning.

The game's engine sound (SkrilStudio RealisticEngineSound) keeps playing on the vehicles that are not simulated;
on the simulated ones only its engine loops are muted.

engine-sim needed some changes to build with GCC and to run this way. They are listed in
`native/engine-sim/VENDORED.txt`.

## Building

The plugin builds against the game's own assemblies. The native library is cross-compiled for Windows (the game
is a Windows build, also when run through Proton).

Requirements: .NET SDK, CMake, MinGW-w64 (`x86_64-w64-mingw32-g++`), and on Linux a host C++ compiler for the
test tool.

```
native/build.sh                  # native/build/win/enginesim_native.dll, plus a Linux build for testing
dotnet build -c Release          # EngineSim.dll; copies the plugin into the game's BepInEx/plugins/EngineSim
```

The project expects to sit in the game folder (`Apocalypter/EngineSim`). Elsewhere, pass the game path:
`dotnet build -c Release -p:GameDir=/path/to/Apocalypter`.

### Offline test

`native/build/linux/render_wav` drives an engine through idle, a full-throttle sweep, the rev limiter and a
coast-down, the way the plugin does, and writes a WAV:

```
native/build/linux/render_wav "5.2L V8 230HP 340Nm Gasoline" out.wav native/ir \
    mild_exhaust.wav 1.0 0.15 1.0      # optional: impulse response, outlet flow, pipe length (m), header scale
```

Environment variables set the rest: `ES_CONDITION` (0-1), `ES_IDLE`, `ES_REDLINE`, `ES_IDLE_AIR`,
`ES_LIMITER` (cut length in s, default 0.08, 0 = off), `ES_BOOST` (pressure ratio), `ES_FLUID_STEPS`.

## Credits and license

- [engine-sim](https://github.com/ange-yaghi/engine-sim) and
  [simple-2d-constraint-solver](https://github.com/ange-yaghi/simple-2d-constraint-solver) by Ange Yaghi, MIT
  licensed. The exhaust impulse responses in `native/ir` are engine-sim's.
- Engine Sim is MIT licensed, see `LICENSE`.
