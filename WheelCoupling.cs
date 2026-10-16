using System.Collections;
using System.Collections.Generic;
using NWH.WheelController3D;
using UnityEngine;

namespace EngineSim
{
    // Two wheels turned together by something outside the game's drivetrain, which only knows a tree from
    // the gearbox down to wheels: a belt from one wheel's pulley to another axle, a shaft or a solid axle
    // between two wheels. Add it to any object and point it at the two wheels:
    //   a, b        the two WheelControllers; b turns `ratio` times as fast as a, the same way round
    //               (a belt: a's pulley radius over b's)
    //   capacity    the most torque it passes, N m at b (a belt's grip, a clutch); 0 = rigid
    //   engagement  0..1, scales the capacity (a belt tensioner or clutch lever); 0 = free
    //
    // The game's wheel only pushes the car through the torque on it (motor and brake torque, up to its grip);
    // its spin is pulled to the road's speed with no force on the car. So a coupling works the way the game's
    // drivetrain does: after each physics step it works out the torque that would bring the two to one speed
    // (each at the inertia the game steps it with, which for a driven wheel includes the gearbox and engine
    // behind it), capped by the capacity, and puts it on the wheels for the next step, equal and opposite
    // through the ratio. A free wheel (one the game's drivetrain doesn't drive) takes it as motor torque, so on
    // the ground it drives the car and the road loads the belt; a driven wheel's motor torque is the
    // drivetrain's, so it takes it as the change of speed the torque makes over the step. Couplings sharing
    // a wheel add up.
    public sealed class WheelCoupling : MonoBehaviour
    {
        public WheelController a, b;
        public float ratio = 1f;
        public float capacity;
        [Range(0f, 1f)] public float engagement = 1f;

        // the torque passed in the last step, N m at b
        public float Torque { get; private set; }

        private float _nextLog;

        private void OnEnable() { StartCoroutine(Run()); }

        private void OnDisable()
        {
            Torque = 0f;
            WheelTorque.Release(this, a);
            WheelTorque.Release(this, b);
        }

        private IEnumerator Run()
        {
            var wait = new WaitForFixedUpdate();
            while (true)
            {
                yield return wait;
                Step(Time.fixedDeltaTime);
            }
        }

        private void Step(float dt)
        {
            if (a == null || b == null || !a.isActiveAndEnabled || !b.isActiveAndEnabled || a.wheel == null || b.wheel == null || dt <= 0f) return;
            float e = Mathf.Clamp01(engagement);
            float ia = WheelTorque.InertiaOf(a), ib = WheelTorque.InertiaOf(b);
            float slip = ratio * a.wheel.angularVelocity - b.wheel.angularVelocity;
            // the torque at b that would leave both at one speed after a step
            float t = slip / (dt * (ratio * ratio / ia + 1f / ib));
            if (capacity > 0f)
            {
                float max = capacity * e;
                t = Mathf.Clamp(t, -max, max);
            }
            else if (e <= 0f) t = 0f;
            Torque = t;
            WheelTorque.Set(this, b, t, dt);
            WheelTorque.Set(this, a, -ratio * t, dt);
            if (Plugin.VerboseLogEntry.Value && Time.unscaledTime >= _nextLog && (e > 0f || Mathf.Abs(slip) > 1f))
            {
                _nextLog = Time.unscaledTime + 5f;
                const float rpm = 30f / Mathf.PI;
                Plugin.Log.LogInfo("[coupling] " + name + ": engagement " + e.ToString("0.00") + ", torque " + t.ToString("0") + " N m at b"
                    + (capacity > 0f ? " (holds " + (capacity * e).ToString("0") + ")" : " (rigid)")
                    + " | a " + (a.wheel.angularVelocity * rpm).ToString("0") + " rpm x" + ratio.ToString("0.00") + " = " + (ratio * a.wheel.angularVelocity * rpm).ToString("0")
                    + " rpm, b " + (b.wheel.angularVelocity * rpm).ToString("0") + " rpm | b " + (b.AutoSimulate ? "free" : "driven")
                    + (b.IsGrounded ? ", on the ground, load " + b.Load.ToString("0") + " N" : ", off the ground")
                    + ", radius " + b.Radius.ToString("0.00") + " m, grip " + b.forwardFriction.grip.ToString("0.00")
                    + ", motor " + b.MotorTorque.ToString("0") + " N m, road force " + b.forwardFriction.force.ToString("0") + " N");
            }
        }
    }
}

namespace EngineSim
{
    // Torque on a wheel from outside the game's drivetrain (a belt, a coupling, a generator). Each source owns its share of
    // a wheel's torque and sets it again every step it acts (whenever in the step that is: a coupling after the physics
    // step, a generator during it); a free wheel (the game's drivetrain doesn't step it) takes the sum as motor torque, a
    // driven one each share as the change of speed it makes over the step, at the wheel's inertia. A source switched off
    // takes its share back (Release).
    public static class WheelTorque
    {
        private static readonly Dictionary<WheelController, Dictionary<object, float>> Shares = new Dictionary<WheelController, Dictionary<object, float>>();

        public static void Set(object owner, WheelController w, float torque, float dt)
        {
            if (w == null || w.wheel == null || owner == null) return;
            if (w.AutoSimulate)
            {
                if (!Shares.TryGetValue(w, out var shares)) Shares[w] = shares = new Dictionary<object, float>();
                shares[owner] = torque;
                float sum = 0f;
                foreach (var v in shares.Values) sum += v;
                w.MotorTorque = sum;
            }
            else
            {
                Release(owner, w);   // driven now: its motor torque is the drivetrain's
                w.wheel.angularVelocity += torque / InertiaOf(w) * dt;
            }
        }

        public static void Release(object owner, WheelController w)
        {
            if (w == null || owner == null || !Shares.TryGetValue(w, out var shares) || !shares.Remove(owner)) return;
            if (w.AutoSimulate)
            {
                float sum = 0f;
                foreach (var v in shares.Values) sum += v;
                w.MotorTorque = sum;
            }
            if (shares.Count == 0) Shares.Remove(w);
        }

        // The inertia a torque from outside turns the wheel against. The game sets a driven wheel's to its own plus
        // the drivetrain's behind it, and in neutral that still counts the engine's and clutch's, which a gearbox
        // in neutral doesn't connect: then it is the wheel's own (its base inertia, else half its mass by its
        // radius squared, a disc).
        private static readonly Dictionary<WheelController, NWH.VehiclePhysics2.VehicleController> Vehicles = new Dictionary<WheelController, NWH.VehiclePhysics2.VehicleController>();
        public static float InertiaOf(WheelController w)
        {
            if (w == null) return 1e-4f;
            if (!Vehicles.TryGetValue(w, out var vc) || (vc == null && !ReferenceEquals(vc, null))) Vehicles[w] = vc = w.GetComponentInParent<NWH.VehiclePhysics2.VehicleController>();
            bool neutral = vc != null && vc.powertrain != null && vc.powertrain.transmission != null && vc.powertrain.transmission.Gear == 0;
            if (!w.AutoSimulate && neutral)
            {
                float own = w.wheel.baseInertia > 1e-6f ? w.wheel.baseInertia : 0.5f * w.Mass * w.Radius * w.Radius;
                return Mathf.Max(1e-4f, own);
            }
            return Mathf.Max(1e-4f, w.Inertia);
        }
    }
}
