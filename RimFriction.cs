using NWH.WheelController3D;
using UnityEngine;

namespace EngineSim
{
    // The game's wheels collide through frictionless collision meshes: the grip is the wheel's own tyre model,
    // which only works on the ground below the wheel as the vehicle stands. On anything else (a vehicle on
    // its side or its roof resting on a wheel or flywheel, a rim against a wall) the wheel is a frictionless
    // disc and the vehicle skates. Add this next to a WheelController: whenever its tyre model isn't
    // gripping, its collision meshes take `friction` (steel on dirt ~0.5), and go back to frictionless when it
    // is.
    public sealed class RimFriction : MonoBehaviour
    {
        public WheelController wheel;
        public float friction = 0.5f;

        private int _rim = -1;   // rim friction on: 1, off: 0, not set yet: -1

        private void Awake()
        {
            if (wheel == null) wheel = GetComponent<WheelController>();
        }

        private void FixedUpdate()
        {
            if (wheel == null || wheel.wheel == null) return;
            int rim = wheel.IsGrounded ? 0 : 1;
            if (rim == _rim) return;
            _rim = rim;
            Set(wheel.wheel.topMeshCollider);
            Set(wheel.wheel.bottomMeshCollider);
        }

        private void Set(MeshCollider c)
        {
            if (c == null) return;
            var m = c.material;   // the wheel's own instance
            if (_rim == 1) { m.dynamicFriction = friction; m.staticFriction = friction; m.frictionCombine = PhysicMaterialCombine.Average; }
            else { m.dynamicFriction = 0f; m.staticFriction = 0f; m.frictionCombine = PhysicMaterialCombine.Minimum; }
        }
    }
}
