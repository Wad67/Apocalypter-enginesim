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

        // motor torque on free wheels from all couplings, this step
        private static readonly Dictionary<WheelController, float> Motor = new Dictionary<WheelController, float>();
        private static float _step = -1f;
        private float _nextLog;

        private void OnEnable() { StartCoroutine(Run()); }

        private void OnDisable()
        {
            Torque = 0f;
            Release(a);
            Release(b);
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
            // a new physics step: last step's motor torques start again from nothing
            if (Time.fixedTime != _step)
            {
                _step = Time.fixedTime;
                foreach (var w in Motor.Keys)
                    if (w != null && w.AutoSimulate) w.MotorTorque = 0f;
                Motor.Clear();
            }
            if (a == null || b == null || !a.isActiveAndEnabled || !b.isActiveAndEnabled || a.wheel == null || b.wheel == null || dt <= 0f) return;
            float e = Mathf.Clamp01(engagement);
            float ia = Mathf.Max(1e-4f, a.Inertia), ib = Mathf.Max(1e-4f, b.Inertia);
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
            Apply(b, t, ib, dt);
            Apply(a, -ratio * t, ia, dt);
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

        private static void Apply(WheelController w, float torque, float inertia, float dt)
        {
            if (w.AutoSimulate)
            {
                float sum;
                Motor.TryGetValue(w, out sum);
                Motor[w] = sum + torque;
                w.MotorTorque = sum + torque;
            }
            else w.wheel.angularVelocity += torque / inertia * dt;
        }

        private static void Release(WheelController w)
        {
            if (w != null && w.AutoSimulate && Motor.Remove(w)) w.MotorTorque = 0f;
        }
    }
}
