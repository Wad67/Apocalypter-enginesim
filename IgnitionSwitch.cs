using System.Linq;
using HutongGames.PlayMaker;
using NWH.VehiclePhysics2;
using UnityEngine;

namespace EngineSim
{
    // The dashboard ignition switch with the simulated drivetrain: a key and nothing more. A click turns
    // the ignition on or off; held, it cranks the starter for as long as it is held (the ignition on).
    //
    // The game's own switch (PlayMaker FSM 'Start' on the vehicle's START object) runs a fixed-time
    // start and follows the game's engine (IsRunning, its fuel and engine checks); while coupled it is
    // disabled, and its parts are used as they are: the key it turns, its sounds (the click of the
    // ignition and the starter loop) and the hover text. Released, the FSM is put back in the state
    // matching the key.
    internal sealed class IgnitionSwitch
    {
        private const string FsmName = "Start";
        private const string StateOff = "off", StateOn = "off2", StateRunning = "off3", StateIgnition = "Ignition", StateStart = "Start";
        private static readonly string[] OnStates = { "Ignition", "off2", "over2", "Start", "wait", "off3", "over3" };
        private const float HoldToCrank = 0.25f;   // held longer than this: the starter, shorter: a click
        private const float Reach = 2f;             // the game's own pick distance (MousePickEvent rayDistance)
        private const int PickMask = 1;             // its layer mask: layer 0

        private readonly PlayMakerFSM _fsm;
        private readonly GameObject _owner;
        private readonly Transform _key;
        private readonly float _offAngle = -30f, _onAngle = 30f;
        private readonly AudioClip _toggleClip, _starterClip;
        private readonly AudioSource _audio;
        private readonly Component _itemText;

        private bool _pressed, _pressedWhileOn, _hover;
        private float _pressTime;
        private string _shown;

        public bool On { get; private set; }
        public bool Cranking { get; private set; }
        public AudioSource Audio { get { return _audio; } }
        public AudioClip StarterClip { get { return _starterClip; } }

        // the vehicle's switch, without taking it over: whether its key is on
        public static PlayMakerFSM FindFsm(VehicleController vc)
        {
            return vc.GetComponentsInChildren<PlayMakerFSM>(true)
                .FirstOrDefault(f => f.FsmName == FsmName && f.Fsm != null && f.Fsm.HasState(StateRunning));
        }

        public static bool IsOn(PlayMakerFSM fsm) { return fsm != null && OnStates.Contains(fsm.ActiveStateName); }

        public static IgnitionSwitch TakeOver(VehicleController vc, bool keyOn)
        {
            var fsm = FindFsm(vc);
            return fsm != null ? new IgnitionSwitch(fsm, keyOn) : null;
        }

        private IgnitionSwitch(PlayMakerFSM fsm, bool keyOn)
        {
            _fsm = fsm;
            _owner = fsm.gameObject;
            _audio = _owner.GetComponent<AudioSource>();

            // the key, its angles and the sounds, from the FSM's own actions
            object turnOff = Action(StateOff, "SetRotation"), turnOn = Action(StateIgnition, "SetRotation");
            _key = Target(turnOn) ?? Target(turnOff);
            _offAngle = Float(turnOff, "xAngle", _offAngle);
            _onAngle = Float(turnOn, "xAngle", _onAngle);
            _toggleClip = Clip(Action(StateIgnition, "PlaySound"), "clip");
            _starterClip = Clip(Action(StateStart, "SetAudioClip"), "audioClip");
            var itemName = FsmVariables.GlobalVariables.GetFsmGameObject("UI_ItemName");
            _itemText = itemName != null && itemName.Value != null ? itemName.Value.GetComponent("Text") : null;

            _fsm.enabled = false;
            if (_audio != null) _audio.Stop();
            On = keyOn;
            ShowKey();
            // the player pressed the game's switch and is still holding it as the coupling took over
            if (On && Input.GetMouseButton(0) && Pointed())
            {
                _pressed = true;
                _pressedWhileOn = true;
                _pressTime = Time.time;
            }
        }

        // main thread, every frame: the player's hand on the key
        public void Update()
        {
            bool pointed = Pointed();
            if (!_pressed && pointed && Input.GetMouseButtonDown(0))
            {
                _pressed = true;
                _pressedWhileOn = On;
                _pressTime = Time.time;
                if (!On) Turn(true);   // off -> on at once; held on, it goes on to the starter
            }
            if (_pressed)
            {
                if (Input.GetMouseButton(0)) Cranking = Time.time - _pressTime >= HoldToCrank;
                else
                {
                    if (!Cranking && _pressedWhileOn && Time.time - _pressTime < HoldToCrank) Turn(false);   // a click: off
                    _pressed = false;
                    Cranking = false;
                }
            }
            ShowKey();

            // the hover text, as the game shows it
            if (pointed != _hover || (pointed && Text() != _shown))
            {
                _hover = pointed;
                SetText(pointed ? Text() : "");
            }
        }

        private string Text() { return On ? "Hold: start   Click: off" : "Ignition   Hold: start"; }

        private void Turn(bool on)
        {
            On = on;
            if (_toggleClip != null) AudioSource.PlayClipAtPoint(_toggleClip, _owner.transform.position, 1f);
            Plugin.Verbose("ignition " + (on ? "on" : "off"));
        }

        private void ShowKey()
        {
            if (_key == null) return;
            var e = _key.localEulerAngles;
            float x = On ? _onAngle : _offAngle;
            if (Cranking) x = _onAngle + 15f;   // turned on against its spring
            if (Mathf.Abs(Mathf.DeltaAngle(e.x, x)) > 0.01f) _key.localEulerAngles = new Vector3(x, e.y, e.z);
        }

        // as the game's MousePickEvent: the camera's ray through the cursor hits the switch itself
        private bool Pointed()
        {
            var cam = Camera.main;
            if (cam == null) return false;
            RaycastHit hit;
            return Physics.Raycast(cam.ScreenPointToRay(Input.mousePosition), out hit, Reach, PickMask)
                && hit.collider != null && hit.collider.gameObject == _owner;
        }

        private void SetText(string text)
        {
            _shown = text;
            if (_itemText == null) return;
            var p = _itemText.GetType().GetProperty("text");
            if (p != null) p.SetValue(_itemText, text, null);
        }

        // the game's switch back, in the state matching the key: off, on, or on with the engine turning
        public void Release(bool turning)
        {
            if (_hover) SetText("");
            Cranking = false;
            _pressed = false;
            if (_fsm == null) return;
            _fsm.enabled = true;
            _fsm.SetState(!On ? StateOff : turning ? StateRunning : StateOn);
        }

        private object Action(string state, string type)
        {
            var s = _fsm.Fsm.States.FirstOrDefault(x => x.Name == state);
            return s != null ? s.Actions.FirstOrDefault(a => a != null && a.GetType().Name == type) : null;
        }

        private static object Field(object action, string name)
        {
            if (action == null) return null;
            var f = action.GetType().GetField(name);
            return f != null ? f.GetValue(action) : null;
        }

        private Transform Target(object setRotation)
        {
            var owner = Field(setRotation, "gameObject") as FsmOwnerDefault;
            var go = owner != null ? _fsm.Fsm.GetOwnerDefaultTarget(owner) : null;
            return go != null ? go.transform : null;
        }

        private static float Float(object action, string name, float fallback)
        {
            var f = Field(action, name) as FsmFloat;
            return f != null && !f.IsNone ? f.Value : fallback;
        }

        private static AudioClip Clip(object action, string name)
        {
            var o = Field(action, name) as FsmObject;
            return o != null ? o.Value as AudioClip : null;
        }
    }
}
