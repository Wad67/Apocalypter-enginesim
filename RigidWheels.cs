using NWH.WheelController3D;
using UnityEngine;

namespace EngineSim
{
    // A wheel without suspension travel (spring.maxLength 0: a solid axle bolted to the frame) can't take up a
    // change of size: when a bigger wheel goes on (fitted, or set again as a save loads) its collision mesh
    // grows straight into the ground, and the physics engine either leaves it buried or throws the vehicle
    // off it. A sprung wheel just compresses its spring. So when such a wheel grows while standing on
    // something, the vehicle is lifted by as much as the wheel would sink in, as it would be jacked up to fit
    // it.
    [HarmonyLib.HarmonyPatch(typeof(WheelController), nameof(WheelController.Radius), HarmonyLib.MethodType.Setter)]
    internal static class RigidWheelPatch
    {
        private static void Prefix(WheelController __instance, out float __state)
        {
            __state = __instance.wheel != null ? __instance.wheel.radius : 0f;
        }

        private static void Postfix(WheelController __instance, float __state)
        {
            if (__instance.wheel == null || __instance.spring == null || __instance.spring.maxLength > 0f) return;
            float r = __instance.wheel.radius;
            if (r <= __state + 1e-4f) return;
            var body = __instance.GetComponentInParent<Rigidbody>();
            if (body == null) return;
            // the ground under the axle, looked for from above the new wheel's top
            Vector3 centre = __instance.transform.position;
            RaycastHit hit;
            if (!Physics.Raycast(centre + Vector3.up * r, Vector3.down, out hit, 2f * r, __instance.layerMask, QueryTriggerInteraction.Ignore)) return;
            float sink = r - (hit.distance - r);
            if (sink <= 0f) return;
            var lift = Vector3.up * (Mathf.Min(sink, r - __state) + 0.005f);
            body.transform.position += lift;
            body.position = body.transform.position;
            Plugin.Verbose(body.name + ": wheel " + __instance.name + " grew " + __state.ToString("0.00") + " -> " + r.ToString("0.00")
                + " m on a solid axle; vehicle lifted " + lift.y.ToString("0.00") + " m");
        }
    }
}
