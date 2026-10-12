using System.Linq;
using HutongGames.PlayMaker;
using UnityEngine;

namespace EngineSim
{
    // The game-side half of a simulated engine's effects (main thread, every frame):
    //
    //   exhaust   : the fitted exhaust part's smoke and the simulated engine's backfires (ExhaustFx);
    //               the game's own random exhaust flashes (rev limiter, shifts) and pop sounds are off
    //               while the engine is simulated
    //   engine rock: the engine part rocks on its mounts with the engine's torque, a damped spring
    //               about the axis the game's own shake uses, in place of the game's constant shake
    //               (PlayMaker FSM 'Shake' on the engine hinge, a fixed 0.5 degree loop): it lurches
    //               on starting and stopping, kicks on revs and shudders on misfires
    //
    // Released, the game's own behaviour is put back.
    internal sealed class EngineEffects
    {
        private const string ShakeFsm = "Shake";
        private const float RockDegrees = 1.6f;          // at the engine's peak torque, strength 1
        private const float MountHz = 5f, MountDamping = 0.3f;

        private readonly EngineSession _session;
        private readonly NWH.VehiclePhysics2.Effects.ExhaustFlash _flash;
        private readonly bool _flashOnLimiter, _flashOnShift;
        private readonly AudioSource _popSource;
        private readonly bool _popMuted;
        private readonly ExhaustFx _exhaust;

        private GameObject _part;
        private PlayMakerFSM _shake;
        private Quaternion _rest;
        private bool _rocking;
        private float _angle, _speed;

        public EngineEffects(EngineSession session)
        {
            _session = session;
            var vc = session.Vc;
            _flash = vc.effectsManager != null ? vc.effectsManager.exhaustFlash : null;
            if (_flash != null)
            {
                _flashOnLimiter = _flash.flashOnRevLimiter;
                _flashOnShift = _flash.flashOnShift;
                _flash.flashOnRevLimiter = false;
                _flash.flashOnShift = false;
            }
            var pop = vc.soundManager != null ? vc.soundManager.exhaustPopComponent : null;
            _popSource = pop != null ? pop.source : null;
            if (_popSource != null)
            {
                _popMuted = _popSource.mute;
                _popSource.mute = true;
            }
            _exhaust = new ExhaustFx(session);
        }

        // The game's exhaust flash goes through its meshes and lights without looking: they belong to the exhaust
        // part, so with it taken off they are gone and the flash throws. Without them (or textures) there is no
        // tailpipe to flash from anyway.
        private bool FlashUsable()
        {
            if (_flash == null || _flash.flashTextures == null || _flash.flashTextures.Count == 0) return false;
            if (_flash.meshRenderers != null) foreach (var r in _flash.meshRenderers) if (r == null) return false;
            if (_flash.flashLights != null) foreach (var l in _flash.flashLights) if (l == null) return false;
            return true;
        }

        public void Update()
        {
            // smoke and backfires at the exhaust part; without one, backfires only flash the game's exhaust flash
            int backfires = _session.TakeBackfires();
            if (backfires > 0)
                Plugin.Verbose("[backfire] " + _session.PartName + ": " + backfires + " detected, " + (Plugin.BackfireEntry.Value > 0f ? "played" : "silenced")
                    + " (Backfires " + Plugin.BackfireEntry.Value.ToString("0.###") + ", BackfireChance " + Plugin.BackfireChanceEntry.Value.ToString("0.###")
                    + (_exhaust.Active ? ", at the exhaust)" : ", no exhaust: flash only)"));
            if (_exhaust.Active) _exhaust.Update(backfires);
            else if (backfires > 0 && Plugin.BackfireEntry.Value > 0f && FlashUsable()) _flash.Flash(false);

            float strength = Plugin.EngineRockEntry.Value;
            if (strength <= 0f || _session.EnginePart == null)
            {
                StopRocking();
                return;
            }
            if (!_rocking) StartRocking();
            if (!_rocking) return;

            // a damped spring driven by the engine's torque against its peak (its reaction rolls the block)
            float peak = Mathf.Max(20f, _session.PeakTorque > 0f ? _session.PeakTorque : 200f);
            float target = Mathf.Clamp(_session.RawTorque / peak, -1.5f, 1.5f) * RockDegrees * strength;
            float w = 2f * Mathf.PI * MountHz;
            const int substeps = 4;
            float h = Mathf.Min(Time.deltaTime, 0.05f) / substeps;
            for (int i = 0; i < substeps; ++i)
            {
                float accel = w * w * (target - _angle) - 2f * MountDamping * w * _speed;
                _speed += accel * h;
                _angle += _speed * h;
            }
            _part.transform.localRotation = _rest * Quaternion.Euler(0f, 0f, _angle);
        }

        private void StartRocking()
        {
            _part = _session.EnginePart;
            var hinge = _part.transform.parent;
            _shake = hinge != null ? hinge.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == ShakeFsm) : null;
            if (_shake != null)
            {
                _shake.enabled = false;
                // its tween keeps turning the part: remove it, and rest where the game rests it (its idle state)
                foreach (var c in _part.GetComponents<MonoBehaviour>())
                    if (c != null && c.GetType().Name == "iTween") Object.Destroy(c);
                _rest = Quaternion.identity;
            }
            else _rest = _part.transform.localRotation;
            _angle = _speed = 0f;
            _rocking = true;
        }

        private void StopRocking()
        {
            if (!_rocking) return;
            _rocking = false;
            if (_part != null) _part.transform.localRotation = _rest;
            if (_shake != null) _shake.enabled = true;
            _shake = null;
            _part = null;
        }

        public void Release()
        {
            StopRocking();
            _exhaust.Release();
            if (_flash != null)
            {
                _flash.flashOnRevLimiter = _flashOnLimiter;
                _flash.flashOnShift = _flashOnShift;
            }
            if (_popSource != null) _popSource.mute = _popMuted;
        }
    }
}
