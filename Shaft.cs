using System.Collections.Generic;
using NWH.VehiclePhysics2;
using NWH.VehiclePhysics2.Powertrain;
using UnityEngine;

namespace EngineSim
{
    // A load on an engine's crankshaft that isn't the game's drivetrain: a propeller, a rotor's pre-rotator, a pump.
    // It turns with the crank (its inertia is added to the crank's) and takes a torque that depends on the crank's
    // speed. Called on the main thread, every engine step, so keep it cheap.
    public interface IShaftLoad
    {
        // kg m2, as seen at the crank (a load behind a reduction of ratio r counts r^2 times its own)
        float Inertia { get; }

        // the torque it takes from the crank at crank speed w (rad/s, >= 0) over the next dt, N m, and its slope
        // dTorque/dw (N m s); the slope lets the crank step stay stable however stiff the load is
        float Torque(float w, float dt, out float slope);
    }

    // Loads on each vehicle's engine. With any attached, the engine's step (EngineComponent.IntegrateDownwards)
    // turns the crank on its own inertia plus theirs, driven by the engine's torque and held back by their torque
    // and, in gear, by what it handed to the clutch. In neutral that is all that loads it.
    public static class Shaft
    {
        private static readonly Dictionary<VehicleController, List<IShaftLoad>> Loads = new Dictionary<VehicleController, List<IShaftLoad>>();

        public static void Attach(VehicleController vc, IShaftLoad load)
        {
            if (vc == null || load == null) return;
            if (!Loads.TryGetValue(vc, out var list)) Loads[vc] = list = new List<IShaftLoad>();
            if (!list.Contains(load)) list.Add(load);
        }

        public static void Detach(VehicleController vc, IShaftLoad load)
        {
            if (vc == null || load == null || !Loads.TryGetValue(vc, out var list)) return;
            list.Remove(load);
            if (list.Count == 0) Loads.Remove(vc);
        }

        // the crank's speed after a step of dt from w0 under the engine's torque, the clutch's (0 in neutral) and the
        // loads', at the crank's inertia plus theirs; false when nothing is attached. Each load is taken semi-
        // implicitly (its torque linearised about w0), so a stiff one can't make the step overshoot.
        internal static bool Step(VehicleController vc, float w0, float engineTorque, float clutchTorque, float crankInertia, float dt, out float w)
        {
            w = w0;
            if (vc == null || !Loads.TryGetValue(vc, out var list) || list.Count == 0) return false;
            float inertia = Mathf.Max(1e-4f, crankInertia), torque = engineTorque - clutchTorque, slope = 0f;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var load = list[i];
                if (load == null || (load is Object o && o == null)) { list.RemoveAt(i); continue; }
                inertia += Mathf.Max(0f, load.Inertia);
                torque -= load.Torque(Mathf.Max(0f, w0), dt, out float s);
                slope += Mathf.Max(0f, s);
            }
            // I dw/dt = T(w0) - s (w - w0)  ->  w = w0 + dt T(w0) / (I + dt s)
            w = w0 + dt * torque / (inertia + dt * slope);
            return true;
        }
    }
}
