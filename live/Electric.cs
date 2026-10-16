using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ES3Internal;
using HutongGames.PlayMaker;
using NWH.VehiclePhysics2;
using UnityEngine;

namespace EngineSim
{
    // DC electrics: a generator set's dynamo and the lamps it lights. Nothing here runs unless a generator is in the
    // world (a chassis whose .json adds EngineSim.DcGenerator); the game's lamps stay as they are everywhere else.
    //
    // The dynamo's pulley is a wheel (a WheelController, as the stand's flywheels), belted to the engine's crank pulley
    // (CrankBelt); the dynamo brakes that wheel, and the belt hands the load on to the crank. Its voltage is its speed
    // times its field;
    // its regulator weakens the field to keep the voltage at or below its setting and the current within its rating,
    // as a vibrating-contact regulator does. Below that it gives what its speed gives, so the lamps follow the engine. Its armature and brushes have
    // resistance (worn brushes more: its condition, kept in an FSM variable the game's repair raises, and worn down
    // by the current it delivers), so the voltage sags under load. The torque it takes is its EMF times its current
    // over its speed, plus bearing and brush friction.
    //
    // Lamps are 12 V tungsten globes on copper wiring from the generator (EngineSim.lamps.cfg says which of the
    // game's lamps are electric). A hot filament's current goes as the voltage to the 0.55 (its resistance rises as
    // it heats) and its light as the voltage to the 3.4; a cold one draws more. Each lamp's wire drops some voltage,
    // so far lamps are dimmer. A lamp belongs to the nearest generator whose wiring reaches it; with the generator
    // stopped, it is dark. A lamp shot out (the game destroys it) is off the circuit.
    public sealed class DcGenerator : MonoBehaviour
    {
        // set from the chassis .json
        public NWH.WheelController3D.WheelController wheel;   // its pulley, belted to the crank
        public float volts = 14.2f;         // regulator setting, V: the most it lets out
        public float amps = 40f;            // regulator current limit, A
        public float emf = 12.9f;           // open-circuit volts per 1000 generator rpm at full field (the machine's own)
        public float resistance = 0.08f;    // armature and brushes of a new generator, ohm
        public float friction = 0.35f;      // bearings and brushes, N m
        public float reach = 60f;           // m of wiring: the lamps it can light
        public float wire = 0.0138f;        // wiring resistance, ohm per metre of run (out and back: 2.5 mm2 copper)
        public float wear = 0.01f;          // condition lost per amp-hour delivered, % (brushes and commutator)
        public float capacitance = 1f;      // F across the output (0: none, the voltage follows the speed at once)
        public string conditionFsm = "Condition";   // FSM on this object holding its condition, 0 - 100 (none: always new)
        public string conditionVariable = "Condition";

        // the state now
        public float Volts { get; private set; }
        public float Amps { get; private set; }
        public float Rpm { get; private set; }
        public float Watts => Volts * Amps;
        public string LoadText => $"{Amps:0.0} A  {Watts:0} W";   // for the hover readout
        public string Title => $"Generator  {_condition:0} %";
        public string VoltsText => $"{Volts:0.0} V";
        public float Condition => _condition;

        public readonly List<Lamp> Lamps = new List<Lamp>();
        public static readonly List<DcGenerator> All = new List<DcGenerator>();

        private FsmFloat _conditionVar;
        private float _condition = 100f, _wornAh;
        private float _bus;         // the voltage across the output (the capacitor's)

        private void OnEnable()
        {
            if (wheel == null) Plugin.Log.LogWarning($"generator {name}: no wheel (its pulley) set; it can't turn");
            if (!All.Contains(this)) All.Add(this);
            var fsm = GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == conditionFsm);
            _conditionVar = fsm != null ? fsm.FsmVariables.FindFsmFloat(conditionVariable) : null;
            Plugin.Verbose($"generator {name}: {volts:0.#} V, {amps:0} A, reach {reach:0} m" + (_conditionVar == null ? ", no condition FSM (always new)" : ""));
        }

        private void OnDisable()
        {
            All.Remove(this);
            WheelTorque.Release(this, wheel);
            foreach (var l in Lamps) l.Owner = null;
            Lamps.Clear();
            Volts = Amps = Rpm = 0f;
        }

        // main thread, every frame: wear, the lamps
        private void Update()
        {
            if (_conditionVar != null)
            {
                _wornAh += Amps * Time.deltaTime / 3600f;
                if (_wornAh * wear >= 0.01f)   // in steps, so the FSM isn't written every frame
                {
                    _conditionVar.Value = Mathf.Clamp(_conditionVar.Value - _wornAh * wear, 0f, 100f);
                    _wornAh = 0f;
                }
                _condition = Mathf.Clamp(_conditionVar.Value, 0f, 100f);
            }
            for (int i = Lamps.Count - 1; i >= 0; i--)
            {
                if (!Lamps[i].Alive) { Lamps[i].Owner = null; Lamps.RemoveAt(i); continue; }
                Lamps[i].Show();
            }
            if (Plugin.VerboseLogEntry.Value && Time.unscaledTime >= _nextLog)
            {
                _nextLog = Time.unscaledTime + 5f;
                var sb = new System.Text.StringBuilder();
                sb.Append($"generator {transform.root.name}: {Rpm:0} rpm, {Volts:0.0} V, {Amps:0.0} A, {Lamps.Count} lamps, condition {_condition:0}");
                foreach (var l in Lamps) sb.Append($"\n    {l.Light.transform.parent?.parent?.name}/{l.Light.name}: {l.Volts:0.0} V, {l.Current(Volts):0.0} A, wire {l.Wire:0.000} ohm, rated {l.RatedVolts:0.#} V {l.RatedWatts:0} W");
                Plugin.Verbose(sb.ToString());
            }
        }
        private float _nextLog;

        // every physics step: the armature's load against its pulley's turning
        private void FixedUpdate()
        {
            if (wheel == null || wheel.wheel == null || !wheel.isActiveAndEnabled) { Load(0f, true); return; }   // stopped: the capacitor drains
            float wg = wheel.wheel.angularVelocity;
            float t = Load(Mathf.Abs(wg), true) * Mathf.Sign(wg);
            WheelTorque.Set(this, wheel, Mathf.Abs(wg) > 1e-4f ? -t : 0f, Time.fixedDeltaTime);
        }

        // the torque the armature takes at its speed wg, N m at the generator; keep: the state is this speed's
        private float Load(float wg, bool keep)
        {
            wg = Mathf.Max(0f, wg);
            float c = _condition / 100f;
            float tf = friction * Mathf.Clamp(wg / 2f, -1f, 1f);
            float v = 0f, i = 0f, te = 0f;
            if (capacitance > 0f && keep)
            {
                // a capacitor across the output: its voltage moves only by what the generator puts in less what the
                // lamps draw, C dV/dt = Ig - Il, solved for the end of the step (implicitly: stable however big C is)
                float dt = Mathf.Max(1e-4f, Time.fixedDeltaTime);
                float k = emf / (1000f * Mathf.PI / 30f);
                float emax = c > 0.001f ? k * wg : 0f;
                float r = resistance / Mathf.Max(c, 0.001f);
                float v0 = _bus;
                // the generator's current at bus voltage x: through its resistance, never back into it (its cut-out),
                // within its rating, the regulator letting the field down over the last tenths of a volt to its setting
                Func<float, float> gen = x =>
                {
                    if (emax <= x) return 0f;
                    float field = Mathf.Clamp01((volts - x) / 0.2f);
                    return Mathf.Min(amps, (emax - x) / r * field);
                };
                float hi = Mathf.Max(v0, Mathf.Max(emax, volts)) + 1f;
                v = Bisect(0f, hi, x => capacitance * (x - v0) / dt - gen(x) + Current(x));
                i = gen(v);
                if (wg > 0f) te = (v + r * i) * i / wg;
                _bus = v;
            }
            else if (c > 0.001f && wg > 0f)
            {
                float k = emf / (1000f * Mathf.PI / 30f);                     // V s per rad at full field
                float r = resistance / c;                                    // worn brushes: more contact resistance
                float emax = k * wg;
                // the regulator's ceiling: its voltage, or less where the lamps would draw more than its current limit
                float ceiling = volts;
                if (Current(volts) > amps) ceiling = Bisect(0f, volts, x => Current(x) - amps);
                // at full field: the bus voltage where the EMF covers it and the drop inside
                float full = Bisect(0f, emax, x => x + r * Current(x) - emax);
                v = Mathf.Min(full, ceiling);
                i = Current(v);
                float e = v + r * i;
                te = e * i / wg;
                _bus = v;
            }
            else _bus = 0f;
            if (keep)
            {
                Volts = v; Amps = i; Rpm = wg * 30f / Mathf.PI;
                foreach (var l in Lamps) l.Supply(v);
            }
            return te + tf;
        }

        // the lamps' current at bus voltage v
        private float Current(float v)
        {
            float sum = 0f;
            foreach (var l in Lamps) sum += l.Current(v);
            return sum;
        }

        // f rising in x: its zero in [lo, hi]
        public static float Bisect(float lo, float hi, Func<float, float> f)
        {
            if (f(hi) <= 0f) return hi;
            if (f(lo) >= 0f) return lo;
            for (int n = 0; n < 24; n++)
            {
                float mid = 0.5f * (lo + hi);
                if (f(mid) > 0f) hi = mid; else lo = mid;
            }
            return 0.5f * (lo + hi);
        }
    }

    // A belt from an engine's crank pulley to a wheel (a generator's pulley): it grips up to its capacity, times its
    // tension (a belt clutch lever), and slips past it. Each engine step it works out the torque that would bring the
    // two to one speed (the crank at its inertia, the wheel at its), capped by its grip, and puts it on both: on the
    // wheel as WheelCoupling does, on the crank as a load (Shaft).
    //   b                    the wheel
    //   radiusA, radiusB     the crank pulley's and b's, m: b turns radiusA / radiusB times as fast
    //   capacity             the most torque it passes, N m at b; engagement 0 - 1 scales it
    //   enginePulley         turns with the crank (about enginePulleyAxis, its own)
    public sealed class CrankBelt : MonoBehaviour, IShaftLoad, IBeltDrive
    {
        public NWH.WheelController3D.WheelController b;
        public float radiusA = 0.1f, radiusB = 0.05f;
        public float capacity = 30f;
        [Range(0f, 1f)] public float engagement = 1f;
        public Transform enginePulley;
        public Vector3 enginePulleyAxis = Vector3.up;

        public float BeltTorque { get; private set; }   // at b, last step
        public float CrankSpeed { get; private set; }   // rad/s

        private VehicleController _vc;

        float IShaftLoad.Inertia => 0f;   // the wheel turns on its own; the belt couples them
        float IBeltDrive.Engagement => engagement;
        float IBeltDrive.DriveSpeed => CrankSpeed;

        private void OnEnable()
        {
            _vc = GetComponentInParent<VehicleController>();
            if (_vc == null) { Plugin.Log.LogWarning($"crank belt {name}: no vehicle above it, so no engine"); return; }
            Shaft.Attach(_vc, this);
        }

        private void OnDisable()
        {
            if (_vc != null) Shaft.Detach(_vc, this);
            WheelTorque.Release(this, b);
            BeltTorque = CrankSpeed = 0f;
        }

        private void Update()
        {
            if (enginePulley != null && CrankSpeed > 1e-3f)
                enginePulley.localRotation *= Quaternion.AngleAxis(CrankSpeed * Mathf.Rad2Deg * Time.deltaTime, enginePulleyAxis);
        }

        public float Torque(float w, float dt, out float slope)
        {
            slope = 0f;
            CrankSpeed = Mathf.Max(0f, w);
            if (b == null || b.wheel == null || !b.isActiveAndEnabled || dt <= 0f || _vc == null || _vc.powertrain == null) { BeltTorque = 0f; return 0f; }
            float r = radiusA / Mathf.Max(1e-4f, radiusB);
            float ic = Mathf.Max(1e-4f, _vc.powertrain.engine.inertia), ib = WheelTorque.InertiaOf(b);
            // the torque at b that leaves both at one speed after the step (already the locked solution: no slope)
            float t = (r * CrankSpeed - b.wheel.angularVelocity) / (dt * (r * r / ic + 1f / ib));
            float max = Mathf.Max(0f, capacity) * Mathf.Clamp01(engagement);
            t = Mathf.Clamp(t, -max, max);
            BeltTorque = t;
            WheelTorque.Set(this, b, t, dt);
            return r * t;
        }
    }

    // One of the game's electric lamps on a generator's circuit: its Light dimmed with its voltage, and its glowing
    // glass with it where its material glows
    public sealed class Lamp
    {
        public readonly Light Light;
        public readonly Renderer Glass;
        public readonly float RatedVolts, RatedWatts, BaseIntensity, BaseRange;
        private readonly Color _baseEmission;
        private readonly bool _emits;
        private MaterialPropertyBlock _block;
        public DcGenerator Owner;
        public float Wire;        // ohm, there and back
        private float _volts;     // at the lamp
        public float Volts => _volts;

        public Lamp(Light light, Renderer glass, float ratedVolts, float ratedWatts, float baseIntensity, float baseRange)
        {
            Light = light; Glass = glass; RatedVolts = ratedVolts; RatedWatts = ratedWatts;
            BaseIntensity = baseIntensity;
            BaseRange = baseRange;
            var m = glass != null ? glass.sharedMaterial : null;
            _emits = m != null && m.IsKeywordEnabled("_EMISSION") && m.HasProperty("_EmissionColor");
            if (_emits) _baseEmission = m.GetColor("_EmissionColor");
        }

        public bool Alive => Light != null && Light.gameObject.activeInHierarchy;

        // the filament: hot, its current goes as the voltage to the 0.55; cold, its resistance is a twelfth
        private float Filament(float v)
        {
            if (v <= 0f) return 0f;
            float hot = RatedWatts / RatedVolts * Mathf.Pow(v / RatedVolts, 0.55f);
            float cold = v / (RatedVolts * RatedVolts / RatedWatts / 12f);
            return Mathf.Min(hot, cold);
        }

        // the current it draws at bus voltage v, through its wire: v = its own voltage + wire drop
        public float Current(float v)
        {
            if (v <= 0f) return 0f;
            if (Wire <= 1e-5f) return Filament(v);
            float lv = DcGenerator.Bisect(0f, v, x => x + Wire * Filament(x) - v);
            return Filament(lv);
        }

        public void Supply(float bus)
        {
            if (bus <= 0f) { _volts = 0f; return; }
            _volts = Wire <= 1e-5f ? bus : DcGenerator.Bisect(0f, bus, x => x + Wire * Filament(x) - bus);
        }

        // light goes as the voltage to the 3.4
        public void Show() { Set((_volts > 0f ? Mathf.Pow(_volts / RatedVolts, 3.4f) : 0f)); }
        public void Restore() { Set(1f); }

        private void Set(float f)
        {
            if (Light == null) return;
            Light.intensity = BaseIntensity * f;
            // how far it lights to the same brightness goes as the square root of its output (inverse square)
            if (Light.type != LightType.Directional) Light.range = BaseRange * Mathf.Sqrt(f);
            Light.enabled = f > 0.002f;
            if (!_emits || Glass == null) return;
            if (_block == null) _block = new MaterialPropertyBlock();
            Glass.GetPropertyBlock(_block);
            _block.SetColor("_EmissionColor", _baseEmission * f);
            Glass.SetPropertyBlock(_block);
        }
    }

    // A flyweight governor on the engine's throttle, switched on and off by clicking `toggle`. On, it holds its rated
    // speed (rpm), the throttle lever (an FSM float: lever / leverFsm / leverVariable, 0 - 1) raising that up to maxRpm
    // fully open, and it works the throttle: open fully at droop below that speed, shut at it, so it
    // holds the speed against the load as far as the engine's power goes. The linkage follows the weights through a
    // dashpot (lag), which keeps it from hunting. Off, the lever is the throttle again.
    public sealed class Governor : MonoBehaviour
    {
        public bool on;
        public GameObject toggle;           // clicking it switches the governor on and off
        public GameObject lever;
        public string leverFsm = "Throttle", leverVariable = "position";
        public float rpm = 2200f;           // the speed it holds with the lever shut (a generator set's rated speed)
        public float maxRpm = 3000f;        // the speed asked for with the lever fully open
        public float droop = 0.08f;         // of the set speed: from shut to fully open
        public float lag = 0.3f;            // s: the linkage (and its dashpot) follows the weights this slowly

        public float Target { get; private set; }
        public string Text => on ? $"Governor on, {Target:0} rpm  (left click: off)" : "Governor off  (left click: on)";

        internal static readonly Dictionary<VehicleController, Governor> ByVehicle = new Dictionary<VehicleController, Governor>();
        private VehicleController _vc;
        private FsmFloat _lever;
        private float _throttle;    // where the linkage has the throttle now

        private void OnEnable()
        {
            _vc = GetComponentInParent<VehicleController>();
            if (_vc != null) ByVehicle[_vc] = this;
            var fsm = lever != null ? lever.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == leverFsm) : null;
            _lever = fsm != null ? fsm.FsmVariables.FindFsmFloat(leverVariable) : null;
        }

        private void OnDisable()
        {
            if (_vc != null && ByVehicle.TryGetValue(_vc, out var g) && g == this) ByVehicle.Remove(_vc);
        }

        // Unity's sign that clicking this does something (other mods' hands leave the click alone)
        private void OnMouseDown() { }

        private void Update()
        {
            if (toggle == null || !Input.GetMouseButtonDown(0) || Cursor.lockState != CursorLockMode.Locked || Camera.main == null) return;
            var ray = Camera.main.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            if (Physics.Raycast(ray, out var hit, 2f, ~0, QueryTriggerInteraction.Collide) && hit.collider.transform.IsChildOf(toggle.transform))
                on = !on;
        }

        // the throttle it opens now
        public float Throttle()
        {
            var engine = _vc.powertrain.engine;
            float set = Mathf.Lerp(rpm, Mathf.Max(rpm, maxRpm), _lever != null ? Mathf.Clamp01(_lever.Value) : 0f);
            Target = set;
            float wanted = Mathf.Clamp01((set - engine.OutputRPM) / Mathf.Max(1f, droop * set));
            // the linkage follows the weights through its dashpot: a first-order lag, no faster than lag allows
            float dt = Time.fixedDeltaTime;
            _throttle += (wanted - _throttle) * Mathf.Clamp01(dt / Mathf.Max(dt, lag));
            return _throttle;
        }
    }

    // the governor's throttle goes in just before the engine reads it (after the lever's FSM has written its own)
    [HarmonyLib.HarmonyPatch(typeof(NWH.VehiclePhysics2.Powertrain.EngineComponent), nameof(NWH.VehiclePhysics2.Powertrain.EngineComponent.IntegrateDownwards))]
    internal static class GovernorThrottle
    {
        // the engine reads the input's throttle as worked out once a frame from what was written to it (by then the
        // lever's FSM has written its own): the governor's goes straight into that
        private static readonly HarmonyLib.AccessTools.FieldRef<NWH.VehiclePhysics2.Input.VehicleInputHandler, float> Swapped =
            HarmonyLib.AccessTools.FieldRefAccess<NWH.VehiclePhysics2.Input.VehicleInputHandler, float>("_inputSwappedThrottle");

        private static void Prefix(NWH.VehiclePhysics2.Powertrain.EngineComponent __instance)
        {
            var vc = __instance.vehicleController;
            if (vc == null || vc.input == null || !Governor.ByVehicle.TryGetValue(vc, out var g) || g == null || !g.on || !g.isActiveAndEnabled) return;
            float t = g.Throttle();
            vc.input.Throttle = t;
            Swapped(vc.input) = t;
        }
    }

    // Puts the game's electric lamps on the circuit of the nearest running-or-not generator whose wiring reaches them,
    // and gives back the ones no generator reaches. Every two seconds; a generator lights its lamps every frame.
    public sealed class Electrics : MonoBehaviour
    {
        public static Electrics Instance;
        private readonly Dictionary<Light, Lamp> _lamps = new Dictionary<Light, Lamp>();
        private readonly Dictionary<Mesh, long> _meshIds = new Dictionary<Mesh, long>();
        private float _next;

        private void OnEnable() { _next = 0f; }

        // forget every lamp and find them again (a new generation: lamps it made itself, with what it knows now)
        public void Rescan() { OnDisable(); _normal.Clear(); _next = 0f; }

        // off: its lamps go back to the game (the next scanner, if any, takes them at once)
        private void OnDisable()
        {
            foreach (var l in _lamps.Values)
            {
                if (l.Owner != null) l.Owner.Lamps.Remove(l);
                l.Owner = null;
                l.Restore();
            }
            _lamps.Clear();
        }

        private void Update()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 2f;
            if (DcGenerator.All.Count == 0)
            {
                if (_lamps.Count > 0) { foreach (var l in _lamps.Values) l.Restore(); _lamps.Clear(); }
                return;
            }
            var table = LampTable.Get();
            var seen = new HashSet<Light>();
            foreach (var light in FindObjectsOfType<Light>())
            {
                if (!_lamps.TryGetValue(light, out var lamp))
                {
                    var glass = Glass(light, table, out var spec, out long mesh);
                    if (glass == null) continue;
                    var normal = Normal(mesh, light);
                    lamp = _lamps[light] = new Lamp(light, glass, spec.Volts, spec.Watts, normal.intensity, normal.range);
                }
                seen.Add(light);
                DcGenerator best = null;
                float bestD = float.MaxValue;
                foreach (var g in DcGenerator.All)
                {
                    float d = Vector3.Distance(g.transform.position, light.transform.position);
                    if (d <= g.reach && d < bestD) { best = g; bestD = d; }
                }
                if (lamp.Owner != best)
                {
                    if (lamp.Owner != null) lamp.Owner.Lamps.Remove(lamp);
                    lamp.Owner = best;
                    if (best != null) best.Lamps.Add(lamp); else lamp.Restore();
                }
                if (best != null) lamp.Wire = best.wire * bestD;
            }
            foreach (var gone in _lamps.Keys.Where(l => !seen.Contains(l)).ToList())
            {
                var lamp = _lamps[gone];
                if (lamp.Owner != null) lamp.Owner.Lamps.Remove(lamp);
                lamp.Restore();
                _lamps.Remove(gone);
            }
        }

        // the lamp's glass: the object above the Light (it or the next one up) with a listed mesh
        // a lamp's light at its rated voltage: the game's own lamp (a prefab's, which nothing dims), not the one in the
        // world, which a generator may have dimmed already
        private readonly Dictionary<long, (float intensity, float range)> _normal = new Dictionary<long, (float, float)>();
        private (float intensity, float range) Normal(long mesh, Light seen)
        {
            if (_normal.TryGetValue(mesh, out var n)) return n;
            foreach (var l in Resources.FindObjectsOfTypeAll<Light>())
            {
                if (l == null || l.gameObject.scene.IsValid() || l.intensity <= 0f) continue;   // assets only
                if (Glass(l, LampTable.Get(), out _, out long m) != null && m == mesh) { n = (l.intensity, l.range); break; }
            }
            if (n.intensity <= 0f) n = (Mathf.Max(seen.intensity, 1f), Mathf.Max(seen.range, 5f));
            Plugin.Verbose($"lamps: mesh {mesh}: normal light {n.intensity:0.##}, range {n.range:0.#} m");
            return _normal[mesh] = n;
        }

        private Renderer Glass(Light light, Dictionary<long, LampSpec> table, out LampSpec spec, out long meshId)
        {
            spec = default;
            meshId = -1;
            var t = light.transform.parent;
            for (int up = 0; up < 2 && t != null; up++, t = t.parent)
            {
                var mf = t.GetComponent<MeshFilter>();
                var mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null) continue;
                if (!_meshIds.TryGetValue(mesh, out long id))
                {
                    var mgr = ES3ReferenceMgrBase.Current;
                    id = _meshIds[mesh] = mgr != null ? mgr.Get(mesh) : -1;
                }
                if (id != -1 && table.TryGetValue(id, out spec)) { meshId = id; return t.GetComponent<Renderer>(); }
            }
            return null;
        }
    }

    public struct LampSpec { public float Volts, Watts; }

    // EngineSim.lamps.cfg: the game's electric lamps, by their glass's mesh
    public static class LampTable
    {
        private const string Header = "# Engine Sim lamps (format 1).";
        private const string Defaults = Header + @"
# The game's electric lamps, which a generator lights (a generator set's chassis with EngineSim.DcGenerator).
# A lamp is a Light whose object, or the one above it, has a mesh listed here. Lamps not listed (fires, candles,
# tin cans) are left alone. A listed lamp no generator's wiring reaches is left as the game has it.
#
# mesh id = rated volts : rated watts      # label (not read)
#   mesh id: the mesh's Easy Save id (es3global in SigmaMod's place export)
#   rated volts, watts: the globe's. Light goes as the voltage to the 3.4: at its rated voltage it is as bright as
#     the game has it.

6468376695829448105 = 24 : 500       # lamp_1 (Camps 7, 13, 14, 15, Buildings 3 and 7, Cave 6)
";
        private static string _path;
        private static DateTime _stamp;
        private static Dictionary<long, LampSpec> _table = new Dictionary<long, LampSpec>();

        public static void Init(string configDir)
        {
            _path = Path.Combine(configDir, "EngineSim.lamps.cfg");
            TableFiles.Prepare(_path, Header, Defaults);
        }

        public static Dictionary<long, LampSpec> Get()
        {
            if (_path == null || !File.Exists(_path)) return _table;
            var stamp = File.GetLastWriteTimeUtc(_path);
            if (stamp == _stamp) return _table;
            _stamp = stamp;
            var t = new Dictionary<long, LampSpec>();
            int n = 0;
            foreach (var raw in File.ReadAllLines(_path))
            {
                n++;
                var line = raw;
                int hash = line.IndexOf('#');
                if (hash >= 0) line = line.Substring(0, hash);
                if (line.Trim().Length == 0) continue;
                var kv = line.Split('=');
                var vw = kv.Length == 2 ? kv[1].Split(':') : null;
                if (vw == null || vw.Length != 2 || !long.TryParse(kv[0].Trim(), out long id)
                    || !float.TryParse(vw[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
                    || !float.TryParse(vw[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float w) || v <= 0f || w <= 0f)
                {
                    Plugin.Log.LogWarning($"EngineSim.lamps.cfg line {n} ignored: not 'mesh id = volts : watts'");
                    continue;
                }
                t[id] = new LampSpec { Volts = v, Watts = w };
            }
            _table = t;
            Plugin.Log.LogInfo($"lamps: {t.Count} electric lamp kind(s) in EngineSim.lamps.cfg");
            return _table;
        }
    }

    // loaded (and reloaded) by SigmaMod's live code: the lamp table, and the object the lamps' scan runs on
    public static class ElectricsLive
    {
        [SigmaMod.Live.OnLoad]
        private static void Loaded()
        {
            // off (the default): no lamp is touched, and any scanner left from a reload goes
            if (!Plugin.Electrics)
            {
                foreach (var e in Resources.FindObjectsOfTypeAll<Electrics>()) if (e != null) { e.enabled = false; UnityEngine.Object.Destroy(e.gameObject); }
                Electrics.Instance = null;
                return;
            }
            LampTable.Init(BepInEx.Paths.ConfigPath);
            // one scanner: the one carried over, else a new one; any others go (hidden objects: FindObjectOfType misses them)
            if (Electrics.Instance == null)
            {
                var go = new GameObject("EngineSim Electrics");
                go.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(go);
                Electrics.Instance = go.AddComponent<Electrics>();
            }
            foreach (var e in Resources.FindObjectsOfTypeAll<Electrics>())
                if (e != null && e != Electrics.Instance) { e.enabled = false; UnityEngine.Object.Destroy(e.gameObject); }
            Electrics.Instance.Rescan();
            Plugin.Log.LogInfo("electrics (live, experimental) on");
        }
    }
}
