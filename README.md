# Engine Sim

A BepInEx 5 plugin for Apocalypter that replaces the game's recorded engine sounds with a live physical
simulation from [engine-sim](https://github.com/ange-yaghi/engine-sim) by Ange Yaghi (AngeTheGreat).

Every engine fitted to a car near you is simulated, running or not: pistons, valves, combustion and
exhaust gas flow, turned into sound as it happens. With the key off an engine still turns when something
turns it (rolling in gear, a push, the starter) and is heard turning over, just without ignition. The simulated engine is built from the engine fitted to the car, so a 594cc twin, a
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
- **Forced induction.** The boost of the game's (NWH) forced induction raises the simulated intake pressure. With
  the experimental simulated drivetrain, turbos and superchargers are simulated machines instead (below).
- **Exhausts.** The fitted exhaust changes the sound. Every exhaust is set relative to a base sound with no
  exhaust, and both can be tuned without rebuilding.
- **Turbo and supercharger sound.** The game's own spool and whine clips, re-proportioned (optional), one sound
  per turbo: each part's turbos are listed by size (Triple Threat: two small and one large). A turbo spools with
  rpm and load, lags low in the revs and coasts down after a lift; small ones spool sooner and whistle higher. A
  supercharger's whine follows the engine speed. Stock, both top out at half the rev range. With the simulated
  drivetrain each turbo whistles with its simulated shaft speed and the blow-off flutters when the simulated valve
  lifts.
- **Only the engine sound is replaced.** Wind, reverse whine and beep, gear changes, muffler crackle, turbo
  whistle and the rest of the game's vehicle sounds are untouched.
- All settings can be changed in game through the Apocasetter Mods menu
  ([Apocasetter index](https://github.com/DeonUrist/Apocasetter-Index)).

### Effects

Added without changing the simulation (it is only read), each with its own setting under `[Effects]`:

- **Backfires**: unburnt fuel reaching a hot exhaust runner bangs at the exhaust, with flame and dark smoke from its
  emitters and a flash: on the rev limiter, on lifting off, and often from a worn engine's misfires (outside the
  engine's own sound).
- **Valve tick**: a tick as each valve closes, from the engine's cam timing; louder with rpm and wear.
- **Engine rock**: the engine rocks on its mounts with its torque, in place of the game's constant shake.
- **Exhaust smoke**: the fitted exhaust's own emitters follow the engine's exhaust: puffs and flow, black with
  unburnt fuel and heavy boost (rolling coal), blue-grey with blow-by as it wears, smoke while cranking.
- **Chassis torque**: the engine's torque reaction rolls the vehicle body.

A low-pass filter (`[Audio] LowRpmFilter`, on by default, with its cutoffs) takes out the gas model's squeal at
very low engine speeds.

### ⚠ Experimental: simulated drivetrain

Off by default (`[Experimental] ⚠ Simulated drivetrain ⚠`). The simulated engine powers the vehicle instead of the
game's engine model: the game's engine keeps driving the clutch, gearbox and wheels, but on the simulated engine's
torque and at its flywheel's inertia, while the simulation holds its crank at the game's engine speed. Only the
simulated engines (`MaxEngines`, nearest first) are driven this way.

- **No running state.** The simulation's inputs are the key, the starter, the throttle and the fuel. It fires
  whenever its crank turns with the key on and fuel there, however it is turned, so it catches, stalls, starves
  and push / bump starts on its own. The game's own start, stop, stall, auto-start and damage-stop logic is
  switched off for these engines.
- **Ignition switch.** Click to turn the ignition on or off, hold to crank for as long as it is held.
- **Starting.** Fuel has to wet the ports before anything lights, and on a slow crank cold or worn engines fail
  to light some charges: a healthy engine catches in about a second and a half, a worn one takes several seconds,
  a finished one may not catch. Warmth is the game's oil temperature. The starter cranks at a realistic speed,
  dims the lights, and held against more than it can turn (in gear, clutch out) it gives up and only clicks.
- **Clutch.** The clutch holds at least 2.5 times the engine's peak torque, whatever the game's condition-scaled
  figure says. A vehicle that sets a stronger clutch of its own keeps it. In gear the engine turns on its own
  inertia, loaded by the torque it passes through the clutch: held, its momentum goes into the drivetrain and a
  heavy enough load (first gear against a stopped car, reverse at speed) pulls it down to a stall; slipping, it
  is dragged by the clutch's friction torque, and its revs fall into the next gear on a shift. The game's model
  barely moved the engine whatever it was coupled to, which only the tyres' grip on the road covered up; a wheel
  off the ground spun up under an engine that never felt it.
- **Fuel.** An empty tank starves the engine after the fuel left in the lines is used up.
- **Forced induction as machines.** A supercharger is a blower on the crank: it moves a fixed volume per
  revolution and takes power to drive, so a big blower on a small engine makes boost and costs more than it gives.
  A turbo is a shaft spun by the simulated exhaust against its compressor, with a wastegate, a blow-off valve and
  back pressure; a big turbo on a small engine barely spools. Boost is what the machines deliver against what the
  engine draws, capped at each part's set pressure.
- **Twincharging.** A turbo and a supercharger can be fitted together: with one fitted, hold the other against
  the engine's mount and press Use. The turbo feeds the blower; each is removed as normal.
- **Boost gauge.** The game's boost gauge reads the simulated pressure.
- With `Match game power` (on), each engine's torque is scaled to the game's rated power (`drive_torque_scale`
  in `EngineSim.engines.cfg`).

## Parts for building vehicles

Components a vehicle can carry (added by a vehicle mod, e.g. from a SigmaMod `.json`). They work with or
without the simulated drivetrain.

- `EngineSim.WheelCoupling`: two wheels turned together outside the game's drivetrain, which only knows a
  tree from the gearbox down to the wheels. A belt from a flywheel to another axle, or a solid axle. Fields:
  `a`, `b` (WheelControllers; b turns `ratio` times as fast as a), `capacity` (most torque passed, N m at
  b; 0 = rigid) and `engagement` (0..1). It works through the wheels' torque, as the game's drivetrain does:
  a wheel on the ground drives the car and the road loads the coupling.
- `EngineSim.Belt`: draws a belt round two pulleys (`a`, `b`, `radiusA`, `radiusB`, `width`, `thickness`):
  straight along the outer tangents and wrapped round each. It moves with the driving pulley's rim when its
  `coupling` is engaged and hangs slack (`sag`) when not. Without a `material` it is black rubber with a
  moulded cross rib every `pitch` m (a cogged belt), so it can be seen running and slipping. `replaces`
  hides placeholder meshes.
- `EngineSim.Windage`: air drag on a spinning wheel or flywheel, a disc of the wheel's radius and width
  (both faces and the rim), added to its rolling resistance. A 1 m flywheel at 1000 rpm loses about 70 N m.
- `EngineSim.RimFriction`: the game's wheels collide through frictionless meshes and only grip through
  the tyre model, which only sees the ground below the wheel as the vehicle stands. On its side or roof a
  vehicle resting on a wheel or flywheel would skate forever. With this next to a WheelController, its
  meshes take `friction` (0.5, steel on dirt) whenever the tyre model isn't gripping.
- Wheels without suspension travel (`spring.maxLength` 0, a solid axle): when a bigger wheel goes on, the
  vehicle is lifted by as much as the wheel would sink into the ground, instead of burying it.

### Loads on the crankshaft (for other mods)

Something the engine turns that isn't the game's drivetrain (a propeller, a pump, a pre-rotator) implements
`EngineSim.IShaftLoad` and is attached with `EngineSim.Shaft.Attach(vehicleController, load)`:

- `Inertia`: kg m2 as seen at the crank (a load behind a reduction of ratio r counts r^2 times its own).
- `Torque(w, dt, out slope)`: the torque it takes at crank speed `w` (rad/s) over the next step, and its slope
  dTorque/dw. Negative torque drives the engine (a windmilling propeller).

With any load attached, every engine step turns the crank on the engine's inertia plus the loads', driven by the
engine's torque and held back by the loads and, in gear, by what goes to the clutch; in neutral the loads are all
that hold it. Each load is taken semi-implicitly through its slope, so a stiff one can't make the step overshoot.
It works with or without the simulated drivetrain. Apoc-Air's propellers use it.

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
| Performance | `MaxEngines` | `2` | Engines simulated at once: turning or with the key on first, then the nearest. The rest keep the stock sound. |
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
| Audio | `LowRpmFilter` | `true` | Low-pass at low rpm, with `LowRpmFilterCrank`, `LowRpmFilterIdle`, `LowRpmFilterOpenAt`. |
| Effects | `Backfires`, `BackfireSound`, `BackfireChance` | `1`, `gunshot_6`, `0.01` | |
| Effects | `ValveTick`, `EngineRock`, `ExhaustSmoke`, `ChassisTorque` | `1` | 0 = off / the game's own. |
| Experimental | `⚠ Simulated drivetrain ⚠` | `false` | The simulated engine powers the vehicle. |
| Experimental | `Match game power` | `true` | Scale its torque to the game's rated power. |
| Experimental | `Hard starting` | `1` | 0 = every charge lights from the first turn. |
| Experimental | `Starter gives up` | `true` | An overloaded starter only clicks. |
| Debug | `VerboseLog` | `false` | Engine and exhaust identification, live statistics and part details in `LogOutput.log`. |

The exhaust ranges are the limits of engine-sim's exhaust model: outside them it oscillates (a metallic squeal
at idle) or rasps.

### Tables from other mods

A mod that adds or retunes parts ships its own lines instead of editing the tables in `BepInEx/config`: any
plugin folder's `enginesim_assets` folder (any depth), in files named `*.engines.cfg`, `*.exhausts.cfg` and
`*.induction.cfg`, in the same formats as the tables below. They are read on top of the tables in
`BepInEx/config`, so a mod can add its own engines and retune the game's. Where two mods set the same part, the
later folder (by name) wins and the log says so. Edits to them apply to the next engine that starts, like edits
to the main tables. Exhaust profiles name impulse responses in `EngineSim/ir`.

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

`BepInEx\config\EngineSim.induction.cfg` lists each turbo and supercharger part's set pressure (psi; the wastegate
or relief valve with the simulated drivetrain) and its units by relative size (1 = a medium unit: a 50 mm turbo
compressor, or a blower moving 2 L per crank revolution):

```
# part id = set pressure : size [, size ...]
865075917467842793   = 6.2  : 0.6, 0.6, 1.4   # Turbo - Triple Threat - Power x1.7 (two small, one large)
8589483064466669470  = 4.45 : 0.85, 0.85      # Turbo - Double Trouble - Power x1.5 (twin)
1280457821324750492  = 1.8  : 0.8             # Supercharger - Med Mark - Power x1.2
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

`native/build/linux/cosim` tests the simulated drivetrain against a stand-in for the game's engine:
`drive` (crank, idle, throttle, stall, roll start), `sweep` (torque and power by rpm), `start` (cold starts:
`ES_TRIALS`, `ES_HEAT`, `ES_ROLL`, `ES_LOAD`) and `machines` (boost by rpm, spool and blow-off: `ES_SC`,
`ES_SC_PSI`, `ES_TURBOS`, `ES_WG_PSI`, `ES_LIFT`).

## Credits and license

- [engine-sim](https://github.com/ange-yaghi/engine-sim) and
  [simple-2d-constraint-solver](https://github.com/ange-yaghi/simple-2d-constraint-solver) by Ange Yaghi, MIT
  licensed. The exhaust impulse responses in `native/ir` are engine-sim's.
- Engine Sim is MIT licensed, see `LICENSE`.
