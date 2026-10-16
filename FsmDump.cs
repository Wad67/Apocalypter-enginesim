using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using HutongGames.PlayMaker;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace EngineSim
{
    // VerboseLog only: writes every PlayMaker FSM the game has loaded (scene objects and prefabs)
    // to BepInEx/EngineSim-fsm-dump.txt, with every action's parameters resolved, plus the live
    // NWH engine, induction and sound state of every vehicle.
    // Runs 20 s after each scene load.
    internal static class FsmDump
    {
        private static float _due = -1f;

        public static void Init()
        {
            SceneManager.sceneLoaded += (s, m) => _due = Time.realtimeSinceStartup + 20f;
        }

        public static void Tick()
        {
            if (!Plugin.VerboseLogEntry.Value) return;
            if (_due < 0 || Time.realtimeSinceStartup < _due) return;
            _due = -1f;
            try { Write(); }
            catch (Exception e) { Plugin.Log.LogError("FSM dump failed: " + e); }
        }

        private static void Write()
        {
            var sb = new StringBuilder();
            sb.Append("EngineSim FSM dump, ").Append(DateTime.Now).Append(", scene ").Append(SceneManager.GetActiveScene().name).Append('\n');

            sb.Append("\n==== vehicles\n");
            foreach (var vc in UnityEngine.Object.FindObjectsOfType<NWH.VehiclePhysics2.VehicleController>())
            {
                var e = vc.powertrain.engine;
                var fi = e.forcedInduction;
                var sm = vc.soundManager;
                sb.Append(vc.name).Append(": engine running=").Append(e.IsRunning).Append(" rpm=").Append(e.OutputRPM)
                  .Append(" limiter=").Append(e.revLimiterRPM).Append(" cut=").Append(e.revLimiterCutoffDuration)
                  .Append("\n  forcedInduction use=").Append(fi.useForcedInduction).Append(" type=").Append(fi.forcedInductionType)
                  .Append(" boost=").Append(fi.boost).Append(" gain=").Append(fi.powerGainMultiplier).Append(" spool=").Append(fi.spoolUpTime)
                  .Append(" wastegate=").Append(fi.hasWastegate)
                  .Append(" wastegateListeners=").Append(fi.onWastegateRelease.GetPersistentEventCount()).Append('\n');
                if (sm != null)
                {
                    var w = sm.turboWhistleComponent;
                    var fl = sm.turboFlutterComponent;
                    sb.Append("  whistle base=").Append(w.baseVolume).Append(" pitchRange=").Append(w.pitchRange)
                      .Append(" clips=[").Append(string.Join(",", w.clips.Select(c => c != null ? c.name : "null").ToArray())).Append("]")
                      .Append(" source.clip=").Append(w.source != null && w.source.clip != null ? w.source.clip.name : "none")
                      .Append(" vol=").Append(w.source != null ? w.source.volume : -1).Append(" pitch=").Append(w.source != null ? w.source.pitch : -1)
                      .Append("\n  flutter base=").Append(fl.baseVolume)
                      .Append(" clips=[").Append(string.Join(",", fl.clips.Select(c => c != null ? c.name : "null").ToArray())).Append("]")
                      .Append(" masterVolume=").Append(sm.masterVolume).Append('\n');
                }
            }

            sb.Append("\n==== FSMs\n");
            int count = 0;
            foreach (var fsm in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
            {
                if (fsm == null || fsm.Fsm == null) continue;
                bool live = fsm.gameObject.scene.IsValid();
                count++;
                sb.Append("\n---- ").Append(Path(fsm.transform)).Append(live ? "" : " (prefab)")
                  .Append(" / fsm '").Append(fsm.FsmName).Append("' state '").Append(fsm.ActiveStateName).Append("'\n");
                var v = fsm.FsmVariables;
                foreach (var x in v.GetAllNamedVariables())
                    sb.Append("  var ").Append(x.VariableType).Append(' ').Append(x.Name).Append(" = ").Append(Val(x)).Append('\n');
                foreach (var st in fsm.FsmStates)
                {
                    sb.Append("  state '").Append(st.Name).Append("'");
                    foreach (var t in st.Transitions) sb.Append("  [").Append(t.EventName).Append(" -> ").Append(t.ToState).Append(']');
                    sb.Append('\n');
                    FsmStateAction[] actions;
                    // a state not tied to its FSM yet (a menu's, before it starts): loading its actions only logs errors
                    if (st.Fsm == null) { sb.Append("    (actions not loaded: the FSM hasn't started)\n"); continue; }
                    try { actions = st.Actions; }
                    catch (Exception e) { sb.Append("    (actions not loaded: ").Append(e.Message).Append(")\n"); continue; }
                    foreach (var a in actions)
                    {
                        sb.Append("    ").Append(a.GetType().Name);
                        if (!a.Enabled) sb.Append(" (disabled)");
                        sb.Append('\n');
                        foreach (var f in a.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                        {
                            object o;
                            try { o = f.GetValue(a); } catch { continue; }
                            if (o == null) continue;
                            var p = o as FsmProperty;
                            if (p != null) { sb.Append("      ").Append(f.Name).Append(": ").Append(Prop(p)).Append('\n'); continue; }
                            sb.Append("      ").Append(f.Name).Append(" = ").Append(Any(o)).Append('\n');
                        }
                    }
                }
            }

            var path = System.IO.Path.Combine(Paths.BepInExRootPath, "EngineSim-fsm-dump.txt");
            File.WriteAllText(path, sb.ToString());
            Plugin.Log.LogInfo("FSM dump: " + count + " FSMs written to " + path);
        }

        private static string Prop(FsmProperty p)
        {
            var sb = new StringBuilder();
            sb.Append(p.setProperty ? "SET " : "GET ").Append(p.TargetTypeName).Append(" . ").Append(p.PropertyName)
              .Append(" on ").Append(Val(p.TargetObject));
            Append(sb, "bool", p.BoolParameter); Append(sb, "float", p.FloatParameter); Append(sb, "int", p.IntParameter);
            Append(sb, "string", p.StringParameter); Append(sb, "object", p.ObjectParameter); Append(sb, "enum", p.EnumParameter);
            Append(sb, "gameObject", p.GameObjectParameter);
            return sb.ToString();
        }

        private static void Append(StringBuilder sb, string what, NamedVariable v)
        {
            if (v == null) return;
            if (!v.UseVariable && (v.RawValue == null || (v.RawValue is UnityEngine.Object && (UnityEngine.Object)v.RawValue == null))) return;
            sb.Append(" | ").Append(what).Append(' ').Append(Val(v));
        }

        private static string Val(NamedVariable v)
        {
            if (v == null) return "null";
            string val;
            try { val = Any(v.RawValue); } catch { val = "?"; }
            return v.UseVariable ? "{" + v.Name + "}=" + val : val;
        }

        private static string Any(object o)
        {
            if (o == null) return "null";
            var nv = o as NamedVariable;
            if (nv != null) return Val(nv);
            var uo = o as UnityEngine.Object;
            if (uo != null || o is UnityEngine.Object) return uo == null ? "null" : uo.GetType().Name + " '" + uo.name + "'";
            var ev = o as FsmEvent;
            if (ev != null) return "event " + ev.Name;
            var own = o as FsmOwnerDefault;
            if (own != null) return own.OwnerOption == OwnerDefaultOption.UseOwner ? "owner" : Val(own.GameObject);
            var fv = o as FsmVar;
            if (fv != null) return "var " + fv.Type + " " + fv.variableName + " = " + Any(fv.GetValue());
            if (o is string) return "\"" + o + "\"";
            var arr = o as IEnumerable;
            if (arr != null) return "[" + string.Join(", ", arr.Cast<object>().Select(Any).ToArray()) + "]";
            return o.ToString();
        }

        private static string Path(Transform t)
        {
            var s = t.name;
            for (t = t.parent; t != null; t = t.parent) s = t.name + "/" + s;
            return s;
        }
    }
}
