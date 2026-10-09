using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using UnityEngine;

namespace EngineSim
{
    // With the simulated drivetrain, an engine takes a turbo and a supercharger together. The game's
    // mount (the engine's forcedinduction_attacher: FSM 'vehPart_Attach' with a 'hinge' and a place for
    // each type) takes one part; with one fitted, the other type is fitted here, into the same hinge at its
    // own place, the way the game's mount does it: the part in hand, held against the mount, Use. Parts in
    // the hinge are saved and loaded by the game as they are (their parent is the engine's own hinge), and
    // come off as any part does (their own de_Attach / CheckTag).
    //
    // The game shows the carburettor unless the first part in the hinge is a supercharger (FSM
    // 'hasSupercharger'), so a supercharger is kept first and that FSM re-checks when the pair changes.
    internal static class TwinchargeMount
    {
        private const string MountFsm = "vehPart_Attach", CarbFsm = "hasSupercharger";
        private const string PartId = "forcedinduction", FittedTag = "vehPart", LooseTag = "vehPartRemoved";
        private const int FittedLayer = 8;   // as the game's mount sets it

        private sealed class Mount
        {
            public PlayMakerFSM Fsm, Carb;
            public Transform Hinge, TurboPos, SuperchargerPos;
            public Collider Trigger;
            public object CursorWrench, CursorPoint, NotHold;
            public AudioClip Wrench;
        }

        private static readonly Dictionary<GameObject, Mount> Mounts = new Dictionary<GameObject, Mount>();
        private static Mount _over;
        private static float _nextCarbCheck;

        // main thread, every frame, with the simulated drivetrain on
        public static void Update(IEnumerable<EngineSession> sessions)
        {
            HeldPart();
            if (Time.unscaledTime >= _nextCarbCheck)
            {
                _nextCarbCheck = Time.unscaledTime + 0.5f;
                foreach (var s in sessions)
                    if (s.EnginePart != null)
                        foreach (var fsm in s.EnginePart.GetComponentsInChildren<PlayMakerFSM>(false))
                            if (fsm.FsmName == MountFsm) CheckCarb(Get(fsm.gameObject));
            }
        }

        private static void HeldPart()
        {
            var hand = FsmVariables.GlobalVariables.GetFsmGameObject("Hand");
            GameObject item = null;
            if (hand != null && hand.Value != null)
                foreach (Transform c in hand.Value.transform)
                    if (c.CompareTag(LooseTag) && IdOf(c.gameObject) == PartId) { item = c.gameObject; break; }

            Mount target = item != null ? Target(item) : null;
            if (target != _over)
            {
                if (_over != null) Send(_over, _over.CursorPoint);
                _over = target;
            }
            if (target == null) return;
            Send(target, target.CursorWrench);
            if (Input.GetButtonDown("Use")) Attach(target, item);
        }

        // a mount near the part in hand with one part of the other type in its hinge
        private static Mount Target(GameObject item)
        {
            var itemColliders = item.GetComponentsInChildren<Collider>();
            if (itemColliders.Length == 0) return null;
            bool supercharger = InductionMachines.IsSupercharger(item);
            foreach (var c in Physics.OverlapSphere(item.transform.position, 1.5f, ~0, QueryTriggerInteraction.Collide))
            {
                var fsm = c.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == MountFsm);
                if (fsm == null) continue;
                var m = Get(fsm.gameObject);
                if (m == null || m.Hinge == null || (supercharger ? m.SuperchargerPos : m.TurboPos) == null) continue;
                var fitted = Fitted(m);
                if (fitted.Count != 1 || InductionMachines.IsSupercharger(fitted[0]) == supercharger) continue;
                if (m.Trigger != null && !itemColliders.Any(ic => ic.bounds.Intersects(m.Trigger.bounds))) continue;
                return m;
            }
            return null;
        }

        private static void Attach(Mount m, GameObject item)
        {
            bool supercharger = InductionMachines.IsSupercharger(item);
            var pos = supercharger ? m.SuperchargerPos : m.TurboPos;
            if (m.Wrench != null) AudioSource.PlayClipAtPoint(m.Wrench, m.Fsm.transform.position, 1f);
            Send(m, m.NotHold);
            Send(m, m.CursorPoint);
            var rb = item.GetComponent<Rigidbody>();
            if (rb != null) Object.DestroyImmediate(rb);
            item.tag = FittedTag;
            item.layer = FittedLayer;
            item.transform.SetParent(m.Hinge, true);
            item.transform.localPosition = pos.localPosition;
            item.transform.localEulerAngles = pos.localEulerAngles;
            if (supercharger) item.transform.SetAsFirstSibling();
            _over = null;
            CheckCarb(m, true);
            Plugin.Verbose("twincharged: " + item.name + " fitted beside " + string.Join(", ", Fitted(m).Where(p => p != item).Select(p => p.name).ToArray()));
        }

        // the carburettor shows with no supercharger in the hinge (the game's FSM looks at the first part)
        private static void CheckCarb(Mount m, bool force = false)
        {
            if (m == null || m.Carb == null || m.Hinge == null) return;
            var fitted = Fitted(m);
            var sc = fitted.FirstOrDefault(InductionMachines.IsSupercharger);
            if (sc != null && sc.transform.GetSiblingIndex() != 0) { sc.transform.SetAsFirstSibling(); force = true; }
            string state = m.Carb.ActiveStateName;
            bool wrong = (sc != null && state == "hasTrubo") || (sc == null && state == "hasSupercharger");
            if ((force || wrong) && fitted.Count > 0) m.Carb.SetState("checkType");
        }

        private static List<GameObject> Fitted(Mount m)
        {
            var list = new List<GameObject>();
            foreach (Transform c in m.Hinge) if (c.CompareTag(FittedTag)) list.Add(c.gameObject);
            return list;
        }

        private static Mount Get(GameObject owner)
        {
            Mount m;
            if (Mounts.TryGetValue(owner, out m)) return m;
            var fsm = owner.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == MountFsm);
            if (fsm == null) { Mounts[owner] = null; return null; }
            m = new Mount { Fsm = fsm, Carb = owner.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == CarbFsm) };
            // everything from the game's own mount FSM: its hinge, the place for each type, its events and sound
            var hinge = fsm.FsmVariables.GetFsmGameObject("Hinge");
            m.Hinge = hinge != null && hinge.Value != null ? hinge.Value.transform : null;
            if (m.Hinge == null)
            {
                var name = Field(Action(fsm, "partAttached", "GetChild", null), "childName") as FsmString;
                m.Hinge = name != null ? owner.transform.Find(name.Value) : null;
            }
            m.TurboPos = Place(fsm, "Attach turbo");
            m.SuperchargerPos = Place(fsm, "Attach supercharger");
            m.Trigger = owner.GetComponents<Collider>().FirstOrDefault(c => c.isTrigger) ?? owner.GetComponent<Collider>();
            m.CursorWrench = Action(fsm, "Over", "SendEvent", null);
            m.CursorPoint = Action(fsm, "partMissing", "SendEvent", null);
            m.NotHold = Action(fsm, "Attach turbo", "SendEvent", "not_Hold");
            var sound = Field(Action(fsm, "Attach turbo", "PlaySound", null), "clip") as FsmObject;
            m.Wrench = sound != null ? sound.Value as AudioClip : null;
            Mounts[owner] = m;
            return m;
        }

        // the place a state attaches to: its GetChild other than the hinge
        private static Transform Place(PlayMakerFSM fsm, string state)
        {
            var s = fsm.Fsm.States.FirstOrDefault(x => x.Name == state);
            if (s == null) return null;
            foreach (var a in s.Actions)
            {
                if (a == null || a.GetType().Name != "GetChild") continue;
                var name = Field(a, "childName") as FsmString;
                if (name == null || name.Value == "hinge") continue;
                var t = fsm.transform.Find(name.Value);
                if (t != null) return t;
            }
            return null;
        }

        private static string IdOf(GameObject go)
        {
            var fsm = go.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == "ID");
            var v = fsm != null ? fsm.FsmVariables.GetFsmString("ID") : null;
            return v != null ? v.Value : null;
        }

        // replay one of the mount FSM's SendEvent actions, to the same target
        private static void Send(Mount m, object sendEvent)
        {
            if (sendEvent == null) return;
            var target = Field(sendEvent, "eventTarget") as FsmEventTarget;
            var ev = Field(sendEvent, "sendEvent") as FsmEvent;
            if (target != null && ev != null) m.Fsm.Fsm.Event(target, ev.Name);
        }

        private static object Action(PlayMakerFSM fsm, string state, string type, string eventName)
        {
            var s = fsm.Fsm.States.FirstOrDefault(x => x.Name == state);
            if (s == null) return null;
            foreach (var a in s.Actions)
            {
                if (a == null || a.GetType().Name != type) continue;
                if (eventName == null) return a;
                var ev = Field(a, "sendEvent") as FsmEvent;
                if (ev != null && ev.Name == eventName) return a;
            }
            return null;
        }

        private static object Field(object action, string name)
        {
            if (action == null) return null;
            var f = action.GetType().GetField(name);
            return f != null ? f.GetValue(action) : null;
        }
    }
}
