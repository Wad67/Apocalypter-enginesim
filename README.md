# Engine Sim

A BepInEx 5 plugin for Apocalypter that replaces the game's recorded engine sounds with a live physical
simulation from [engine-sim](https://github.com/ange-yaghi/engine-sim) by Ange Yaghi (AngeTheGreat).

Every running engine near you is simulated: pistons, valves, combustion and exhaust gas flow, turned into
sound as it happens. The simulated engine is built from the engine fitted to the car, so a 594cc twin, a
2.8L V6 and a 7L V8 each sound like themselves.

## Features

- **Built from the fitted engine.** The engine part (`2.8L V6 115HP 183Nm Gasoline`, `5.8L I6 120HP 356Nm Diesel`,
  ...) gives the displacement, cylinder count, layout and fuel. Idle and rev limit come from the game's engine.
  Inline, V and boxer layouts with their usual firing orders.
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
| Audio | `Latency` | `0.05` | How far (s) the simulation runs ahead of what you hear. |
| Audio | `WearIntensity` | `1` | How much engine condition is heard. 0 = always new, 2 = twice as harsh. |
| Audio | `IdleAir` | `0.08` | Air the engine idles on. Without it the idle is the engine being turned over, not running. |
| Exhaust | `BaseImpulseResponse` | `mild_exhaust.wav` | The exhaust acoustics every engine is played through. |
| Exhaust | `BaseOutletFlow` | `1.0` | 0.8 - 1.2. |
| Exhaust | `BasePipeLength` | `0.15` | Pipe length with no exhaust fitted, 0.15 - 0.5 m. |
| Exhaust | `BaseHeaderLength` | `1.0` | 0.5 - 1.0. |
| Exhaust | `BaseLoudness` | `1.0` | |
| Exhaust | `BaseAirNoise`, `BaseJitter` | `0` | 0 = chosen to suit the impulse response. |
| Debug | `VerboseLog` | `false` | Engine and exhaust identification, live statistics and part details in `LogOutput.log`. |

The exhaust ranges are the limits of engine-sim's exhaust model: outside them it oscillates (a metallic squeal
at idle) or rasps.

### Exhaust profiles

`BepInEx\config\EngineSim.exhausts.cfg` (written on first start) says how each exhaust part differs from the base
sound. The file is re-read when it changes; edits apply to the next engine that starts.

```
# name = impulse response, flow x, pipe + m, headers x, loudness x [, air noise, jitter]
none                 = -, 1.0,  0.00, 1.0, 1.0
exhaust_quad_big     = -, 1.2,  0.35, 1.0, 0.9
exhaust_single       = -, 0.85, 0.30, 1.0, 0.75
*                    = -, 0.95, 0.25, 1.0, 0.85
```

A line matches when its name appears in the exhaust part's name; `none` is an empty exhaust slot and `*` anything
else. `-` keeps the base impulse response.

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
