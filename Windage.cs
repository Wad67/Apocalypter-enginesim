using NWH.WheelController3D;
using UnityEngine;

namespace EngineSim
{
    // Air drag on a spinning wheel or flywheel (the game's wheels only have a fixed rolling resistance, so a
    // big flywheel turns nearly forever). Add it next to a WheelController; the wheel is treated as a disc of
    // its radius and width spinning in still air:
    //   faces  both sides, moment coefficient Cm = 3.87 / sqrt(Re) laminar, 0.146 / Re^0.2 turbulent
    //          (von Karman; Re = w r^2 / nu), torque = 0.5 rho w^2 r^5 Cm
    //   rim    skin friction of the edge moving at w r, Cf = 0.0725 / Re^0.2, over its area 2 pi r width
    // The torque goes on as extra rolling resistance (always against the spin, never past stopping it), on top
    // of the wheel's own. A 1 m, 0.23 m wide flywheel at 1000 rpm loses about 70 N m to the air.
    public sealed class Windage : MonoBehaviour
    {
        public WheelController wheel;
        public float scale = 1f;                // 0 = off

        private const float Rho = 1.225f;       // air, kg/m3
        private const float Nu = 1.5e-5f;       // air, kinematic viscosity, m2/s
        private float _base = -1f;

        public float Torque { get; private set; }

        private void Awake()
        {
            if (wheel == null) wheel = GetComponent<WheelController>();
        }

        private void FixedUpdate()
        {
            if (wheel == null || wheel.wheel == null) return;
            if (_base < 0f) _base = wheel.RollingResistanceTorque;
            float w = Mathf.Abs(wheel.wheel.angularVelocity), r = wheel.wheel.radius, width = wheel.wheel.width;
            Torque = scale * Drag(w, r, width);
            wheel.RollingResistanceTorque = _base + Torque;
        }

        private void OnDisable()
        {
            if (wheel != null && _base >= 0f) wheel.RollingResistanceTorque = _base;
            _base = -1f;
        }

        public static float Drag(float w, float r, float width)
        {
            if (w < 1e-3f || r <= 0f) return 0f;
            float re = w * r * r / Nu;
            float cm = Mathf.Max(3.87f / Mathf.Sqrt(re), 0.146f * Mathf.Pow(re, -0.2f));
            float faces = 0.5f * Rho * w * w * Mathf.Pow(r, 5f) * cm;
            float v = w * r;
            float cf = 0.0725f * Mathf.Pow(Mathf.Max(re, 1f), -0.2f);
            float rim = 0.5f * Rho * v * v * cf * (2f * Mathf.PI * r * Mathf.Max(width, 0f)) * r;
            return faces + rim;
        }
    }
}
