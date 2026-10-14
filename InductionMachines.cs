using System.Collections.Generic;
using System.Linq;
using System.Text;
using HutongGames.PlayMaker;
using NWH.VehiclePhysics2.Powertrain;
using UnityEngine;

namespace EngineSim
{
    // With the simulated drivetrain, forced induction is machines on the simulated engine (native
    // induction.h): each fitted part's units (EngineSim.induction.cfg: set pressure and sizes) go to the
    // simulation, which works out the boost from what they deliver against what the engine draws. A
    // turbo and a supercharger can be fitted together (TwinchargeMount): the turbos feed the
    // supercharger. Whether a part is a turbo or a supercharger is its own FSM 'Type' ("turbo",
    // "supercharger"), which the game's mount reads too.
    //
    // The game's boost gauge (gauge_boost_level) turns its needle by NWH's boost (0..1) times the fitted
    // part's power gain, 450/7 degrees per unit; it is given the simulated pressure instead (GaugePatch).
    internal static class InductionMachines
    {
        private const string TypeFsm = "Type", TypeVar = "type";

        public static bool IsSupercharger(GameObject part)
        {
            var fsm = part != null ? part.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == TypeFsm) : null;
            var type = fsm != null ? fsm.FsmVariables.GetFsmString(TypeVar) : null;
            return type != null && type.Value == "supercharger";
        }

        // A part's sounds, as it hands them to the game when fitted (its FSM 'CheckTag' sets the game's
        // whistle clip and, for a turbo, its blow-off flutter clip)
        public static void Clips(GameObject part, out AudioClip whistle, out AudioClip flutter)
        {
            whistle = flutter = null;
            var fsm = part != null ? part.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "CheckTag") : null;
            if (fsm == null) return;
            foreach (var state in fsm.Fsm.States)
                foreach (var action in state.Actions)
                {
                    if (action == null || action.GetType().Name != "SetProperty") continue;
                    var f = action.GetType().GetField("targetProperty");
                    var property = f != null ? f.GetValue(action) as FsmProperty : null;
                    if (property == null || property.PropertyName == null || property.ObjectParameter == null) continue;
                    var clip = property.ObjectParameter.Value as AudioClip;
                    if (clip == null) continue;
                    if (property.PropertyName.EndsWith("turboWhistleComponent.source.clip")) whistle = clip;
                    else if (property.PropertyName.EndsWith("turboFlutterComponent.Clip")) flutter = clip;
                }
        }

        // what is fitted, for the simulation; the key changes when it does
        public static Native.InductionDesc Describe(FittedParts fitted, out string key)
        {
            var desc = new Native.InductionDesc { turboSize = new float[Native.MaxTurbos] };
            var sb = new StringBuilder();
            foreach (var kv in fitted.Induction)
            {
                bool supercharger = IsSupercharger(kv.Value);
                var part = PartTable.Induction(kv.Key, supercharger);
                sb.Append(kv.Key).Append(supercharger ? "s" : "t").Append(';');
                if (supercharger)
                {
                    if (desc.superchargerSize > 0f) continue;   // one blower
                    // a part with twin blowers: one blower of their combined displacement
                    desc.superchargerSize = part.Sizes.Sum();
                    desc.superchargerReliefPsi = part.Psi;
                }
                else
                {
                    foreach (float size in part.Sizes)
                        if (desc.turbos < Native.MaxTurbos) desc.turboSize[desc.turbos++] = size;
                    desc.wastegatePsi = Mathf.Max(desc.wastegatePsi, part.Psi);
                }
            }
            key = sb.ToString();
            return desc;
        }

        public static string Describe(Native.InductionDesc d)
        {
            if (d.superchargerSize <= 0f && d.turbos == 0) return "none";
            var parts = new List<string>();
            if (d.superchargerSize > 0f) parts.Add("supercharger " + d.superchargerSize.ToString("0.##") + " (relief " + d.superchargerReliefPsi.ToString("0.#") + " psi)");
            if (d.turbos > 0) parts.Add("turbo " + string.Join(" + ", d.turboSize.Take(d.turbos).Select(x => x.ToString("0.##")).ToArray())
                + " (wastegate " + d.wastegatePsi.ToString("0.#") + " psi)");
            return string.Join(", ", parts.ToArray());
        }

        // drive mode: keep the simulation's machines matching the fitted parts (checked twice a second)
        public static void Update(EngineSession s)
        {
            if (Time.unscaledTime < s.NextInductionScan) return;
            s.NextInductionScan = Time.unscaledTime + 0.5f;
            string key;
            var desc = Describe(PartTable.Fitted(s.Vc), out key);
            if (key == s.InductionKey) return;
            s.InductionKey = key;
            if (s.Tune != null) s.Tune.ApplyInduction(ref desc);
            s.SetInduction(desc);
            Plugin.Verbose(s.PartName + ": forced induction " + Describe(desc));
        }

        // The game's gauge: its needle turns 450/7 degrees per unit of NWH's boost times the part's power
        // gain. Scale assumed until known: 100 psi at full scale, the stock reading of the biggest
        // supercharger at full boost (x1.9) taken as 50 psi.
        public const float GaugeDegreesPerPsi = 450f / 7f * 1.9f / 50f;

        // NWH's own boost (a made-up spool value, and its wastegate flutter event) is skipped for a coupled
        // engine: its boost value only carries the simulated pressure to the gauge.
        [HarmonyLib.HarmonyPatch(typeof(EngineComponent.ForcedInduction), nameof(EngineComponent.ForcedInduction.Update))]
        internal static class GaugePatch
        {
            private static bool Prefix(EngineComponent engine) { return DriveCoupling.SessionOf(engine) == null; }

            private static void Postfix(EngineComponent.ForcedInduction __instance, EngineComponent engine)
            {
                var session = DriveCoupling.SessionOf(engine);
                if (session == null) return;
                float gain = Mathf.Max(0.05f, __instance.powerGainMultiplier);
                __instance.boost = session.Machines ? Mathf.Max(0f, session.BoostPsi) * GaugeDegreesPerPsi / (450f / 7f * gain) : 0f;
            }
        }
    }
}
