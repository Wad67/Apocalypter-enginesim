using System.Collections.Generic;
using System.Linq;
using System.Text;
using HutongGames.PlayMaker;
using UnityEngine;

namespace EngineSim
{
    // VerboseLog only: what a part carries besides its name (components, PlayMaker
    // variables), once per part name. Groundwork for wear, failures and forced induction.
    internal static class PartDump
    {
        private static readonly HashSet<string> Seen = new HashSet<string>();

        // Where does this vehicle keep forced-induction state? NWH's own, and any PlayMaker variable
        // that sounds like it. Once per vehicle name.
        public static void InductionOnce(NWH.VehiclePhysics2.VehicleController vc)
        {
            if (vc == null || !Seen.Add("induction:" + vc.name)) return;
            var fi = vc.powertrain.engine.forcedInduction;
            var sb = new StringBuilder("[induction] " + vc.name + ": NWH forcedInduction "
                + (fi == null ? "missing" : "use=" + fi.useForcedInduction + " type=" + fi.forcedInductionType + " boost=" + fi.boost
                    + " gain x" + fi.powerGainMultiplier + " wastegate=" + fi.hasWastegate + " spool=" + fi.spoolUpTime + "s"));
            foreach (var fsm in vc.GetComponentsInChildren<PlayMakerFSM>(true))
            {
                foreach (var x in fsm.FsmVariables.FloatVariables)
                {
                    var n = x.Name.ToLowerInvariant();
                    if (n.Contains("boost") || n.Contains("turbo") || n.Contains("supercharg") || n.Contains("psi") || n.Contains("induction"))
                        sb.Append("\n  ").Append(fsm.gameObject.name).Append(" / fsm '").Append(fsm.FsmName).Append("' float ")
                          .Append(x.Name).Append(" = ").Append(x.Value);
                }
            }
            Plugin.Log.LogInfo(sb.ToString());
        }

        public static void Once(GameObject part, string name)
        {
            if (part == null || name == null || !Seen.Add(name)) return;
            var sb = new StringBuilder("[part] '" + name + "'");
            foreach (var t in part.GetComponentsInChildren<Transform>(true).Take(64))
            {
                var comps = t.GetComponents<Component>().Where(c => c != null && !(c is Transform)).ToArray();
                if (comps.Length == 0) continue;
                sb.Append("\n  ").Append(t == part.transform ? "." : t.name).Append(": ")
                  .Append(string.Join(", ", comps.Select(c => c.GetType().Name).ToArray()));
                foreach (var fsm in comps.OfType<PlayMakerFSM>())
                {
                    var v = fsm.FsmVariables;
                    sb.Append("\n    fsm '").Append(fsm.FsmName).Append("' state '").Append(fsm.ActiveStateName).Append("'");
                    foreach (var x in v.FloatVariables) sb.Append("\n      float ").Append(x.Name).Append(" = ").Append(x.Value);
                    foreach (var x in v.IntVariables) sb.Append("\n      int ").Append(x.Name).Append(" = ").Append(x.Value);
                    foreach (var x in v.BoolVariables) sb.Append("\n      bool ").Append(x.Name).Append(" = ").Append(x.Value);
                    foreach (var x in v.StringVariables) sb.Append("\n      string ").Append(x.Name).Append(" = ").Append(x.Value);
                }
            }
            Plugin.Log.LogInfo(sb.ToString());
        }
    }
}
