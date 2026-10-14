using System.Linq;
using ES3Internal;
using HutongGames.PlayMaker;
using UnityEngine;

namespace EngineSim
{
    // The Engine Tuning Toolkit: the game's toolbox under its own id (a SigmaMod variant, EngineSim/sigma_assets/
    // engine_tuning_kit.json), known by its ID FSM. Worked like the game's toolbox: look at it within 2 m with empty
    // hands and press Use to take the tools out, Use on it again (or X) to put them away. With them out, a left click
    // on an engine (fitted or loose, one Engine Sim simulates) with nothing in hand opens its tune (the game's grab
    // is off just then, or it would pick a loose one up): ignition timing, intake and exhaust cam timing, idle speed and rev limit, as
    // offsets from the engine as built (Tune). Apply keeps the tune for that engine in this save slot (Tunes) and
    // makes its simulation again with it; Stock takes it off. While the window is open the cursor is free and the
    // player's mouse look is paused.
    internal sealed class TuningKit : MonoBehaviour
    {
        private const string KitId = "EngineTuningKit";
        private const float KitReach = 2f, EngineReach = 2.5f;
        private const KeyCode PutAwayKey = KeyCode.X;

        private bool _out;
        private GameObject _engine;           // the engine whose tune is open
        private string _engineName;
        private Tune _editing;
        private PlayMakerFSM _grab;
        private bool _grabHeld;               // the kit turned the game's grab off
        private Rect _window = new Rect(0, 0, 470, 650);
        private float _throttle;              // the window's throttle, held on the engine while it is open
        private bool _key, _crank, _cranked;  // the window's key and starter (_crank: held this frame)
        private PlayMakerFSM _look;           // the player's mouse look, switched off while the window is open
        private NWH.VehiclePhysics2.VehicleController _vehicle;
        private bool _vehicleAutoInput;
        private GUIStyle _hint;
        private string _said;
        private float _saidUntil;
        private bool _turbo, _blower;         // the vehicle's machines, for the window's induction rows
        private Dyno _pull, _lastPull;        // the dyno pull shown, and the one before it (faded)
        private bool _graph;
        private Rect _graphWindow = new Rect(0, 0, 520, 390);
        private Texture2D _plot;
        private int _plotted = -1;
        private const int PlotW = 504, PlotH = 270;

        private static GameObject Global(string name)
        {
            var g = FsmVariables.GlobalVariables.FindFsmGameObject(name);
            return g != null ? g.Value : null;
        }

        private static Camera Eye()
        {
            var pc = Global("PlayerCamera");
            var cam = pc != null ? pc.GetComponent<Camera>() : null;
            return cam != null && cam.isActiveAndEnabled ? cam : Camera.main;
        }

        private static bool HandsBusy()
        {
            var use = Global("HandItemUse");
            var hand = Global("Hand");
            return (use != null && use.transform.childCount > 0) || (hand != null && hand.transform.childCount > 0);
        }

        private void Update()
        {
            if (!Plugin.TuningEntry.Value || Global("Player") == null) { if (_out) PutAway(); return; }
            if (_engine != null) { Holding(); return; }   // the window has the mouse
            if (Cursor.lockState != CursorLockMode.Locked) return;
            var cam = Eye();
            if (cam == null) return;
            var ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));

            if (Input.GetButtonDown("Use") && AimedAtKit(ray))
            {
                if (_out) PutAway();
                else if (!HandsBusy()) TakeOut();
                return;
            }
            if (!_out) return;
            if (Input.GetKeyDown(PutAwayKey)) { PutAway(); return; }
            // the game's grab stays on (parts are picked up and bolted on as ever) except over an engine with an
            // empty hand, where the left click is the kit's
            var hand = Global("Hand");
            string label = null;
            var engine = hand != null && hand.transform.childCount > 0 ? null : AimedEngine(ray, out label);
            if (engine == null) { ReleaseGrab(); return; }
            HoldGrab();
            if (Input.GetMouseButtonDown(0)) Open(engine, label);
        }

        // the kit under the aim (the game's toolbox pick: 2 m, layers 0 and 9), by its ID
        private static bool AimedAtKit(Ray ray)
        {
            if (!Physics.Raycast(ray, out var hit, KitReach, (1 << 0) | (1 << 9))) return false;
            foreach (var fsm in hit.collider.GetComponentsInParent<PlayMakerFSM>())
                if (fsm.FsmName == "ID" && fsm.FsmVariables.GetFsmString("ID")?.Value == KitId) return true;
            return false;
        }

        // the engine under the aim: the first thing up from what it hits that is an engine part in the engine table
        private static GameObject AimedEngine(Ray ray, out string label)
        {
            label = null;
            var player = Global("Player");
            foreach (var hit in Physics.RaycastAll(ray, EngineReach, ~0, QueryTriggerInteraction.Ignore).OrderBy(h => h.distance))
            {
                if (player != null && hit.collider.transform.IsChildOf(player.transform)) continue;
                for (var t = hit.collider.transform; t != null; t = t.parent)
                {
                    var pf = t.GetComponent<ES3Prefab>();
                    if (pf == null) continue;
                    var spec = PartTable.Engine(pf.prefabId);
                    if (spec != null) { label = spec.Label ?? spec.Id.ToString(); return t.gameObject; }
                }
                return null;   // the nearest thing isn't an engine
            }
            return null;
        }

        private void TakeOut() { _out = true; Clack(); }

        private void PutAway()
        {
            _out = false;
            Close();
            ReleaseGrab();
            Clack();
        }

        private PlayMakerFSM Grab()
        {
            if (_grab == null)
            {
                var pc = Global("PlayerCamera");
                _grab = pc != null ? pc.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "GrabItem") : null;
            }
            return _grab;
        }

        // the game's grab, off while the kit has the left click: only when it is waiting with empty hands
        private void HoldGrab()
        {
            var g = Grab();
            if (g == null || !g.enabled || (g.ActiveStateName != "idle" && g.ActiveStateName != "over")) return;
            if (g.ActiveStateName != "idle") g.SetState("idle");
            g.enabled = false;
            _grabHeld = true;
        }

        // and back on, if it was the kit that turned it off
        private void ReleaseGrab()
        {
            if (!_grabHeld) return;
            _grabHeld = false;
            var g = Grab();
            if (g != null && !g.enabled) g.enabled = true;
        }

        private void Open(GameObject engine, string label)
        {
            _engine = engine;
            _engineName = label;
            _editing = (Tunes.For(engine) ?? new Tune()).Clone();
            _throttle = 0f;
            _window.x = (Screen.width - _window.width) / 2f;
            _window.y = (Screen.height - _window.height) / 2f;
            // the player's mouse look off (its FSM keeps its state), the cursor free
            var player = Global("Player");
            _look = player != null ? player.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "Look") : null;
            if (_look != null) _look.enabled = false;
            // the engine's vehicle takes the window's throttle instead of the player's keys, so the game's engine
            // revs too (with the simulated drivetrain off, it sets the speed)
            _vehicle = engine.GetComponentInParent<NWH.VehiclePhysics2.VehicleController>();
            if (_vehicle != null && _vehicle.input != null) { _vehicleAutoInput = _vehicle.input.autoSetInput; _vehicle.input.autoSetInput = false; }
            var e = _vehicle != null ? _vehicle.powertrain.engine : null;
            _key = e != null && (DriveCoupling.KeyOf(e) ?? (e.IsRunning || e.ignition));
            _crank = _cranked = false;
            var induction = _vehicle != null ? InductionMachines.Describe(PartTable.Fitted(_vehicle), out _) : default(Native.InductionDesc);
            _turbo = induction.turbos > 0;
            _blower = induction.superchargerSize > 0f;
            _window.height = 650 + (_turbo || _blower ? 24 + 24 * ((_turbo ? 1 : 0) + (_blower ? 2 : 0)) : 0);
            _window.y = (Screen.height - _window.height) / 2f;
            _graph = false;
            _pull = _lastPull = null;
            Holding();
            Clack();
        }

        // every frame the window is open: the cursor stays free, and the throttle is held
        private void Holding()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            EngineManager.HoldThrottle(_engine, _throttle);
            if (_vehicle != null && _vehicle.input != null) _vehicle.input.Throttle = _throttle;
            var e = _vehicle != null ? _vehicle.powertrain.engine : null;
            if (e != null)
            {
                if (DriveCoupling.KeyOf(e).HasValue) DriveCoupling.Held[e] = (_key, _key && _crank);   // the simulation's own key and starter
                else
                {
                    // the game's engine: off with the key; its own start sequence on the starter, once a press
                    if (!_key && (e.IsRunning || e.starterActive)) e.StopEngine();
                    if (_key && _crank && !_cranked && !e.IsRunning && !e.starterActive) e.StartEngine();
                }
            }
            _cranked = _crank;
            _crank = false;   // held again by the next frame's window, or let go
        }

        private void Close()
        {
            if (_engine == null) return;
            EngineManager.HoldThrottle(_engine, null);
            if (_vehicle != null && _vehicle.input != null) { _vehicle.input.Throttle = 0f; _vehicle.input.autoSetInput = _vehicleAutoInput; }
            if (_vehicle != null) DriveCoupling.Held.Remove(_vehicle.powertrain.engine);   // the dashboard's switch again
            _vehicle = null;
            if (_pull != null) _pull.Cancel();
            _pull = _lastPull = null;
            _graph = false;
            _engine = null;
            _editing = null;
            if (_look != null) _look.enabled = true;
            _look = null;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private static AudioClip _toolbox;
        private static bool _looked;

        private static void Clack()
        {
            if (!_looked) { _looked = true; _toolbox = Resources.FindObjectsOfTypeAll<AudioClip>().FirstOrDefault(c => c.name == "toolbox"); }
            var cam = Eye();
            if (_toolbox != null && cam != null) AudioSource.PlayClipAtPoint(_toolbox, cam.transform.position, 0.3f);
        }

        private void Say(string text) { _said = text; _saidUntil = Time.time + 3f; }

        private void OnGUI()
        {
            if (_engine != null)
            {
                if (!_engine) { Close(); return; }
                var skin = GUI.skin;
                GUI.skin = Win95.Skin;
                _window = GUI.Window(0x7E5E, _window, Window, "");
                if (_graph && _engine != null) _graphWindow = GUI.Window(0x7E5F, _graphWindow, GraphWindow, "");
                GUI.skin = skin;
                return;
            }
            if (!_out || Cursor.lockState != CursorLockMode.Locked) return;
            if (_hint == null) _hint = new GUIStyle(GUI.skin.label) { fontSize = 16, normal = { textColor = new Color(0.75f, 1f, 0.75f) } };
            string text = "ENGINE TUNING TOOLKIT\nLeft click an engine: tune it\n" + PutAwayKey + " (or Use on the kit): put it away";
            if (_said != null && Time.time < _saidUntil) text += "\n\n" + _said;
            GUI.Label(new Rect(20f, 20f, 600f, 140f), text, _hint);
        }

        private void Window(int id)
        {
            if (Win95.TitleBarWithClose(_window, "Engine Tuning Toolkit - " + _engineName)) { Close(); return; }
            var t = _editing;
            bool diesel = PartTable.Engine(_engine.GetComponent<ES3Internal.ES3Prefab>()?.prefabId ?? 0)?.Params.diesel != 0;

            Section("Ignition");
            t.Ignition = Row("Timing", t.Ignition, -Tune.IgnitionRange, Tune.IgnitionRange, 0.5f, v => Signed(v, "0.#") + " deg");
            t.Springs = Row("Advance springs", t.Springs, Tune.SpringsMin, Tune.SpringsMax, 0.05f, v => Pct(v) + (v < 1 ? " (sooner)" : v > 1 ? " (later)" : ""));

            Section("Camshafts");
            t.IntakeCam = Row("Intake timing", t.IntakeCam, -Tune.CamRange, Tune.CamRange, 0.5f, v => Signed(v, "0.#") + " deg");
            t.IntakeDuration = Row("Intake duration", t.IntakeDuration, -Tune.DurationRange, Tune.DurationRange, 1f, v => Signed(v, "0") + " deg");
            t.IntakeLift = Row("Intake lift", t.IntakeLift, Tune.LiftMin, Tune.LiftMax, 0.01f, Pct);
            t.ExhaustCam = Row("Exhaust timing", t.ExhaustCam, -Tune.CamRange, Tune.CamRange, 0.5f, v => Signed(v, "0.#") + " deg");
            t.ExhaustDuration = Row("Exhaust duration", t.ExhaustDuration, -Tune.DurationRange, Tune.DurationRange, 1f, v => Signed(v, "0") + " deg");
            t.ExhaustLift = Row("Exhaust lift", t.ExhaustLift, Tune.LiftMin, Tune.LiftMax, 0.01f, Pct);
            GUILayout.BeginHorizontal();
            GUILayout.Label("  Variable timing", GUILayout.Width(130));
            if (GUILayout.Button(t.Vvt ? "ON" : "OFF", GUILayout.Width(60))) t.Vvt = !t.Vvt;
            GUILayout.Label(t.Vvt ? "intake cams phased with rpm and load" : "fixed");
            GUILayout.EndHorizontal();

            Section("Fuel and air");
            if (diesel) GUILayout.Label("  Mixture: set by the injection pump");
            else t.Mixture = Row("Mixture", t.Mixture, Tune.MixtureMin, Tune.MixtureMax, 0.01f, v => (v * Tune.PetrolAfr).ToString("0.0") + ":1" + (v < 1 ? " rich" : v > 1 ? " lean" : ""));
            t.Filter = Row("Air filter", t.Filter, Tune.FilterMin, Tune.FilterMax, 0.05f, v => Pct(v) + (v < 1 ? " (clogged)" : v > 1 ? " (open)" : ""));
            t.Linkage = Row("Throttle linkage", t.Linkage, Tune.LinkageMin, Tune.LinkageMax, 0.05f, v => "x" + v.ToString("0.00"));
            t.Idle = Row("Idle speed", t.Idle, Tune.IdleMin, Tune.IdleMax, 25f, v => Signed(v, "0") + " rpm");

            Section("Rev limiter");
            t.Limiter = Row("Limit", t.Limiter, Tune.LimiterMin, Tune.LimiterMax, 50f, v => Signed(v, "0") + " rpm");
            t.Cut = Row("Cut", t.Cut, 0f, Tune.CutMax, 0.01f, v => v <= 0f ? "as the game" : v.ToString("0.00") + " s");

            if (_turbo || _blower)
            {
                Section("Induction");
                if (_turbo) t.Wastegate = Row("Wastegate", t.Wastegate, Tune.WastegateMin, Tune.WastegateMax, 0.5f, v => Signed(v, "0.#") + " psi");
                if (_blower)
                {
                    t.Pulley = Row("Blower pulley", t.Pulley, Tune.PulleyMin, Tune.PulleyMax, 0.01f, v => Pct(v) + (v > 1 ? " (overdriven)" : v < 1 ? " (underdriven)" : ""));
                    t.Relief = Row("Relief valve", t.Relief, Tune.ReliefMin, Tune.ReliefMax, 0.5f, v => Signed(v, "0.#") + " psi");
                }
            }

            Section("Test");
            var s = EngineManager.SessionOf(_engine);
            var engine = _vehicle != null ? _vehicle.powertrain.engine : null;
            GUI.enabled = engine != null;
            GUILayout.BeginHorizontal();
            GUILayout.Label("  Ignition", GUILayout.Width(130));
            if (GUILayout.Button(_key ? "ON" : "OFF", GUILayout.Width(60))) _key = !_key;
            GUI.enabled = engine != null && _key;
            if (GUILayout.RepeatButton("Starter", GUILayout.Width(90))) _crank = true;
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            _throttle = Row("Throttle", _throttle, 0f, 1f, 0.01f, v => (v * 100f).ToString("0") + " %");
            GUILayout.Label(engine == null ? "  Not in a vehicle: the tune applies when it runs."
                : !engine.IsRunning ? (_key ? "  Key on: hold the starter to start it." : "  Off: turn the key on and hold the starter.")
                : $"  {engine.OutputRPM:0} rpm" + (s == null ? " (not simulated: too far, or too many engines)" : ""));

            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Apply")) { Tunes.Set(_engine, _editing); EngineManager.Retune(_engine); Clack(); }
            if (GUILayout.Button("Stock")) _editing = new Tune();
            GUI.enabled = _pull == null || _pull.Finished;
            if (GUILayout.Button("Simulate Pull")) StartPull();
            GUI.enabled = true;
            if (GUILayout.Button("Close")) Close();
            GUILayout.EndHorizontal();
            GUI.DragWindow(new Rect(0, 0, 10000, 22));
        }

        // a full throttle sweep of the window's tune (not yet applied) on the dyno
        private void StartPull()
        {
            if (_pull != null && _pull.Error == null && _pull.Done > 0) _lastPull = _pull;
            _pull = Dyno.Start(_engine, _editing.Clone());
            if (_pull == null) { Say("Engine Sim does not simulate this engine"); return; }
            if (!_graph)
            {
                _graphWindow.x = Mathf.Min(_window.xMax + 8f, Screen.width - _graphWindow.width);
                _graphWindow.y = _window.y;
            }
            _graph = true;
            _plotted = -1;
            _texts = null;
            Clack();
        }

        private void GraphWindow(int id)
        {
            if (Win95.TitleBarWithClose(_graphWindow, "Dyno - " + _engineName))
            {
                _graph = false;
                if (_pull != null && !_pull.Finished) _pull.Cancel();
                return;
            }
            var p = _pull;
            if (p == null) return;
            int key = p.Done * 2 + (p.Finished ? 1 : 0);
            if (_plot == null || _plotted != key) { DrawPlot(); _plotted = key; }
            var area = GUILayoutUtility.GetRect(PlotW, PlotH);
            GUI.Box(area, GUIContent.none);
            var inner = new Rect(area.x + 2, area.y + 2, area.width - 4, area.height - 4);
            GUI.DrawTexture(inner, _plot);
            var axis = new GUIStyle(Win95.Skin.label) { fontSize = 10, normal = { textColor = Color.white } };
            GUI.Label(new Rect(inner.x + 4, inner.y + 2, 260, 16), $"0 - {_scaleY:0}  N m / kW", axis);
            if (_scaleBoost > 0f)
                GUI.Label(new Rect(inner.xMax - 120, inner.y + 2, 116, 16), $"boost 0 - {_scaleBoost:0} psi", new GUIStyle(axis) { alignment = TextAnchor.UpperRight });
            GUI.Label(new Rect(inner.x + 4, inner.yMax - 16, 300, 16), $"rpm 0 - {_scaleX:0}", axis);

            var red = new GUIStyle(Win95.Skin.label) { normal = { textColor = new Color(0.7f, 0f, 0f) } };
            var blue = new GUIStyle(Win95.Skin.label) { normal = { textColor = new Color(0f, 0f, 0.7f) } };
            var green = new GUIStyle(Win95.Skin.label) { normal = { textColor = new Color(0f, 0.45f, 0f) } };
            // the pull's points finish on other threads: the text is taken once a frame (at layout), so the layout and
            // repaint passes draw the same controls
            if (Event.current.type == EventType.Layout || _texts == null)
            {
                string torqueText = Peaks(p, out string power, out string boost);
                _texts = new[]
                {
                    torqueText, power, boost ?? "",
                    p.Error != null ? "Failed: " + p.Error
                    : !p.Finished ? $"Pulling... {p.Done} of {p.Rpm.Length} points"
                    : _lastPull != null ? "Last pull: " + Peaks(_lastPull, out string lp, out string lb) + ", " + lp + (lb != null ? ", " + lb : "") + " (faded)"
                    : "Full throttle, steady at each rpm. Simulate again to compare.",
                };
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label(_texts[0], red);
            GUILayout.Label(_texts[1], blue);
            if (p.Induction) GUILayout.Label(_texts[2], green);
            GUILayout.EndHorizontal();
            GUILayout.Label(_texts[3]);
            GUI.DragWindow(new Rect(0, 0, 10000, 22));
        }

        private static string Peaks(Dyno p, out string power, out string boost)
        {
            int ti = -1, pi = -1, bi = -1;
            for (int i = 0; i < p.Rpm.Length; i++)
            {
                if (!p.Ready[i]) continue;
                if (ti < 0 || p.Torque[i] > p.Torque[ti]) ti = i;
                if (pi < 0 || Dyno.Kw(p.Rpm[i], p.Torque[i]) > Dyno.Kw(p.Rpm[pi], p.Torque[pi])) pi = i;
                if (bi < 0 || p.Boost[i] > p.Boost[bi]) bi = i;
            }
            if (ti < 0) { power = ""; boost = null; return ""; }
            float kw = Dyno.Kw(p.Rpm[pi], p.Torque[pi]);
            power = $"{kw:0} kW ({kw * 1.341f:0} hp) @ {p.Rpm[pi]:0}";
            boost = p.Induction ? $"{p.Boost[bi]:0.0} psi @ {p.Rpm[bi]:0}" : null;
            return $"{p.Torque[ti]:0} N m @ {p.Rpm[ti]:0}";
        }

        private float _scaleX, _scaleY, _scaleBoost;
        private string[] _texts;   // the dyno window's peaks and status, as laid out this frame

        // the curves on a dark grid: torque red, power blue, boost green; the last pull faded behind
        private void DrawPlot()
        {
            if (_plot == null) _plot = new Texture2D(PlotW, PlotH, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, hideFlags = HideFlags.HideAndDontSave };
            var px = new Color32[PlotW * PlotH];
            var bg = new Color32(16, 16, 24, 255);
            for (int i = 0; i < px.Length; i++) px[i] = bg;
            _scaleX = 1000f; _scaleY = 50f; _scaleBoost = 0f;
            foreach (var d in new[] { _pull, _lastPull })
            {
                if (d == null) continue;
                _scaleX = Mathf.Max(_scaleX, d.Rpm[d.Rpm.Length - 1]);
                for (int i = 0; i < d.Rpm.Length; i++)
                {
                    if (!d.Ready[i]) continue;
                    _scaleY = Mathf.Max(_scaleY, d.Torque[i], Dyno.Kw(d.Rpm[i], d.Torque[i]));
                    if (d.Induction) _scaleBoost = Mathf.Max(_scaleBoost, 5f, d.Boost[i]);
                }
            }
            _scaleX = Mathf.Ceil(_scaleX / 1000f) * 1000f;
            _scaleY = Mathf.Ceil(_scaleY * 1.1f / 50f) * 50f;
            if (_scaleBoost > 0f) _scaleBoost = Mathf.Ceil(_scaleBoost * 1.2f / 5f) * 5f;
            var grid = new Color32(48, 48, 64, 255);
            for (float r = 1000f; r < _scaleX; r += 1000f) Line(px, X(r), 0, X(r), PlotH - 1, grid, 1);
            for (float y = 50f; y < _scaleY; y += 50f) Line(px, 0, Y(y, _scaleY), PlotW - 1, Y(y, _scaleY), grid, 1);
            if (_lastPull != null) Curves(px, _lastPull, 0.35f);
            Curves(px, _pull, 1f);
            _plot.SetPixels32(px);
            _plot.Apply();
        }

        private int X(float rpm) => Mathf.Clamp(Mathf.RoundToInt(rpm / _scaleX * (PlotW - 1)), 0, PlotW - 1);
        private static int Y(float v, float scale) => Mathf.Clamp(Mathf.RoundToInt(v / scale * (PlotH - 1)), 0, PlotH - 1);

        private void Curves(Color32[] px, Dyno d, float alpha)
        {
            Color32 Fade(Color c) => Color.Lerp(new Color32(16, 16, 24, 255), c, alpha);
            Color32 torque = Fade(new Color(1f, 0.25f, 0.2f)), power = Fade(new Color(0.3f, 0.55f, 1f)), boost = Fade(new Color(0.3f, 0.9f, 0.3f));
            // the points in as they finish (they run several at once): each joined to the next finished one
            for (int j = -1, i = 0; i < d.Rpm.Length; i++)
            {
                if (!d.Ready[i]) continue;
                if (j >= 0)
                {
                    Line(px, X(d.Rpm[j]), Y(d.Torque[j], _scaleY), X(d.Rpm[i]), Y(d.Torque[i], _scaleY), torque, 2);
                    Line(px, X(d.Rpm[j]), Y(Dyno.Kw(d.Rpm[j], d.Torque[j]), _scaleY), X(d.Rpm[i]), Y(Dyno.Kw(d.Rpm[i], d.Torque[i]), _scaleY), power, 2);
                    if (d.Induction && _scaleBoost > 0f)
                        Line(px, X(d.Rpm[j]), Y(Mathf.Max(0f, d.Boost[j]), _scaleBoost), X(d.Rpm[i]), Y(Mathf.Max(0f, d.Boost[i]), _scaleBoost), boost, 2);
                }
                j = i;
            }
        }

        // texture rows run bottom up, as the plot does
        private static void Line(Color32[] px, int x0, int y0, int x1, int y1, Color32 c, int width)
        {
            int dx = Mathf.Abs(x1 - x0), dy = -Mathf.Abs(y1 - y0), sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1, err = dx + dy;
            while (true)
            {
                for (int ox = 0; ox < width; ox++)
                    for (int oy = 0; oy < width; oy++)
                    {
                        int x = x0 + ox, y = y0 + oy;
                        if (x >= 0 && x < PlotW && y >= 0 && y < PlotH) px[y * PlotW + x] = c;
                    }
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }

        private static string Signed(float v, string format) => (v > 0 ? "+" : "") + v.ToString(format);
        private static string Pct(float v) => (v >= 1 ? "+" : "") + ((v - 1f) * 100f).ToString("0") + " %";

        private static void Section(string name)
        {
            GUILayout.Space(4);
            GUILayout.Label(name, new GUIStyle(Win95.Skin.label) { fontStyle = FontStyle.Bold });
        }

        private static float Row(string name, float value, float min, float max, float step, System.Func<float, string> shown)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("  " + name, GUILayout.Width(130));
            float v = GUILayout.HorizontalSlider(value, min, max, GUILayout.ExpandWidth(true));
            GUILayout.Label(shown(Mathf.Round(v / step) * step), GUILayout.Width(110));
            GUILayout.EndHorizontal();
            return Mathf.Round(v / step) * step;
        }
    }
}
