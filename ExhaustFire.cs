using System.Collections.Generic;
using System.Linq;
using ES3Internal;
using UnityEngine;

namespace EngineSim
{
    // Fire at an exhaust outlet, emitted by hand: the jet of a backfire, and burning drops of oil blown out of the
    // pipe. It is the game's own fire, the tin can light's (its 'fire' effect, every particle system of it), copied
    // onto the outlet with its look kept (material, texture animation, colour and size over life) and its own
    // emission off. The can light is found by its Easy Save prefab id. If it isn't loaded, a plain additive flame
    // (unlit, cooling from yellow-white to red) stands in, when the game has an additive particle shader.
    //
    //   jet   : no weight, slowed hard by the air: a tongue of flame half a metre to a metre long, gone in a few
    //           hundredths of a second
    //   drops : burning oil: thrown out, falls, lands on whatever is below and burns out there
    internal sealed class ExhaustFire
    {
        private const long CanLightPrefab = 7935259516454344000L;

        private static ParticleSystem _canFire;
        private static bool _looked;

        private readonly List<ParticleSystem> _systems = new List<ParticleSystem>();
        private readonly List<float> _sizeRatio = new List<float>(), _lifeRatio = new List<float>();
        private readonly GameObject _root;

        // the can light's fire: the topmost particle system in the prefab (its children come with it)
        private static ParticleSystem CanFire()
        {
            if (_looked) return _canFire;
            _looked = true;
            var prefab = Resources.FindObjectsOfTypeAll<ES3Prefab>().FirstOrDefault(p => p != null && p.prefabId == CanLightPrefab && !p.gameObject.scene.IsValid());
            if (prefab != null)
                _canFire = prefab.GetComponentsInChildren<ParticleSystem>(true)
                    .FirstOrDefault(ps => ps.transform.parent == null || ps.transform.parent.GetComponentInParent<ParticleSystem>() == null);
            Plugin.Verbose("exhaust fire: " + (_canFire != null ? "the tin can light's ('" + _canFire.name + "')" : "tin can light not found (prefab " + CanLightPrefab + "), plain flame"));
            return _canFire;
        }

        // fire at an outlet, or null if there is neither the can light's fire nor an additive shader for a flame
        public static ExhaustFire At(Transform outlet, bool drops, Material flame)
        {
            var source = CanFire();
            if (source == null && flame == null) return null;
            return new ExhaustFire(outlet, drops, source, flame);
        }

        private ExhaustFire(Transform outlet, bool drops, ParticleSystem source, Material flame)
        {
            if (source != null)
            {
                _root = Object.Instantiate(source.gameObject, outlet, false);
                _root.SetActive(true);
                foreach (var a in _root.GetComponentsInChildren<AudioSource>(true)) Object.Destroy(a);   // its crackle
                foreach (var l in _root.GetComponentsInChildren<Light>(true)) Object.Destroy(l);
            }
            else _root = Plain(outlet, flame);
            _root.name = drops ? "EngineSim burning oil" : "EngineSim backfire flame";
            _root.transform.localPosition = Vector3.zero;
            _root.transform.localRotation = Quaternion.identity;

            var all = _root.GetComponentsInChildren<ParticleSystem>(true);
            float rootSize = Mathf.Max(0.001f, Max(all[0].main.startSize)), rootLife = Mathf.Max(0.001f, Max(all[0].main.startLifetime));
            foreach (var ps in all)
            {
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                var main = ps.main;
                _sizeRatio.Add(Max(main.startSize) / rootSize);
                _lifeRatio.Add(Max(main.startLifetime) / rootLife);
                main.loop = true;
                main.playOnAwake = false;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.scalingMode = ParticleSystemScalingMode.Shape;   // sizes as given, whatever the outlet's scale
                main.maxParticles = drops ? 96 : 64;
                main.simulationSpeed = 1f;
                var emission = ps.emission; emission.enabled = false;
                var inherit = ps.inheritVelocity; inherit.enabled = false;
                var drag = ps.limitVelocityOverLifetime;
                drag.enabled = true;
                drag.limit = new ParticleSystem.MinMaxCurve(1000f);
                drag.multiplyDragByParticleSize = false;
                drag.multiplyDragByParticleVelocity = false;
                if (drops)
                {
                    // a drop of oil: its weight and a little air drag, nothing lifting it, and it lands
                    main.gravityModifier = 1f;
                    drag.drag = new ParticleSystem.MinMaxCurve(0.3f);
                    var lift = ps.velocityOverLifetime; lift.enabled = false;
                    var force = ps.forceOverLifetime; force.enabled = false;
                    var collision = ps.collision;
                    collision.enabled = true;
                    collision.type = ParticleSystemCollisionType.World;
                    collision.mode = ParticleSystemCollisionMode.Collision3D;
                    collision.dampen = 0.9f;
                    collision.bounce = 0.05f;
                    collision.lifetimeLoss = 0f;
                    collision.quality = ParticleSystemCollisionQuality.Medium;
                    collision.radiusScale = 0.3f;
                }
                else
                {
                    main.gravityModifier = 0f;
                    drag.drag = new ParticleSystem.MinMaxCurve(6f);
                }
                ps.Play();
            }
            _systems.AddRange(all);
        }

        // one flame or drop at a point, flying at a velocity, about this big and this long-lived; each of the
        // fire's systems gets its share in its own proportions
        public void Emit(Vector3 position, Vector3 velocity, float size, float lifetime)
        {
            for (int i = 0; i < _systems.Count; i++)
            {
                var ps = _systems[i];
                if (ps == null) continue;
                ps.Emit(new ParticleSystem.EmitParams
                {
                    position = position,
                    velocity = velocity,
                    startSize = size * _sizeRatio[i],
                    startLifetime = lifetime * _lifeRatio[i],
                    applyShapeToPosition = false,
                }, 1);
            }
        }

        public void Destroy()
        {
            if (_root != null) Object.Destroy(_root);
        }

        // the stand-in: an additive flame, growing and cooling from yellow-white through orange to nothing
        private static GameObject Plain(Transform outlet, Material material)
        {
            var go = new GameObject("EngineSim flame");
            go.transform.SetParent(outlet, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.startColor = Color.white;
            main.startSize = 0.15f;
            main.startLifetime = 0.07f;
            var shape = ps.shape; shape.enabled = false;
            var growth = ps.sizeOverLifetime;
            growth.enabled = true;
            growth.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(1f, 2.5f)));
            var cooling = ps.colorOverLifetime;
            cooling.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(new Color(1f, 0.92f, 0.7f), 0f), new GradientColorKey(new Color(1f, 0.5f, 0.12f), 0.4f), new GradientColorKey(new Color(0.6f, 0.12f, 0.03f), 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.8f, 0.4f), new GradientAlphaKey(0f, 1f) });
            cooling.color = new ParticleSystem.MinMaxGradient(gradient);
            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = material;
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return go;
        }

        private static float Max(ParticleSystem.MinMaxCurve c)
        {
            switch (c.mode)
            {
            case ParticleSystemCurveMode.Constant: return c.constant;
            case ParticleSystemCurveMode.TwoConstants: return c.constantMax;
            default: return c.curveMultiplier;
            }
        }
    }
}
