using System.Collections.Generic;
using System.Linq;
using System.Text;
using HutongGames.PlayMaker;
using UnityEngine;

namespace EngineSim
{
    // VerboseLog only: what a part carries besides its name (components, PlayMaker
    // variables), once per part.
    internal static class PartDump
    {
        private static readonly HashSet<string> Seen = new HashSet<string>();

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
