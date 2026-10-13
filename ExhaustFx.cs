using System.Collections.Generic;
using System.Linq;
using HutongGames.PlayMaker;
using UnityEngine;

namespace EngineSim
{
    // The fitted exhaust part's own smoke, from the simulated engine. Each exhaust part carries its
    // emitters (one per outlet, 'ExhaustSmoke', 'ExhaustSmoke (1)' ...) and a PlayMaker FSM
    // ('ExhaustSmoke') that plays them while the car's rpm is over 30, at a simulation speed of
    // rpm x 0.003 (which also ages the smoke 12x as fast at 4000 rpm: it barely lives), and nothing
    // else. While the engine is simulated that FSM is paused and the emitters follow the engine, in
    // real time: the exhaust's push is their start speed, and the smoke lives in the world (it doesn't
    // move with the vehicle, which leaves it behind).
    //
    //   flow : how much gas leaves the pipe (the cylinders' exhaust flow, smoothed over ~0.25 s, against
    //          what the engine moves at its limit): how many particles and how fast they leave
    //   dirt : what is in it, 0 for a healthy engine: unburnt fuel (limiter, misfires), soot (boost under
    //          throttle: rolling coal, most on diesels; diesel load; a worn engine running rich) and oil
    //          (blow-by, as the engine wears, more under load). Dirt makes the smoke visible and dark, makes
    //          it linger and makes it billow: a healthy engine leaves a faint haze that barely spreads, a
    //          finished one black or blue-grey clouds that expand tenfold and hang
    //
    // The smoke only has its exit push: no lift (the emitters' own negative gravity, lifetime velocity
    // and force, and noise are off), and air drag slows it, so it streams out and hangs. It thins as it
    // spreads: its opacity falls over its life.
    //
    // Particles are budgeted: each emitter's rate is held so that rate x lifetime stays under its share
    // of a fixed budget, so an emitter never runs into its particle limit (which stops it emitting
    // until old smoke dies) and the cost stays bounded however dirty the engine.
    //
    // Lighting: a smoke particle is a flat card turned to the camera, and the game's smoke material lights it as a
    // flat surface, so the same smoke is fullbright with the sun behind the camera and dull from the other side.
    // Smoke is a cloud that scatters light about the same whichever way it is seen, so it is drawn unlit, with
    // the scene's light worked into its colour: the ambient light plus the sun's share, from how high the sun is
    // (none at night). If the game has no unlit particle shader, its own material is kept and the particles get
    // spherical normals instead (each lit as a ball, not a card).
    //
    // Backfires (detected by the simulation) come out of each outlet: unburnt fuel lighting in the pipe throws a
    // short jet of flame (ExhaustFire: the game's own fire) and a slug of dark, fuel-rich smoke that leaves fast,
    // slows in the air and billows into a cloud bigger than the running smoke; an oily engine's also spits burning
    // drops of oil. A brief orange flash at the outlet, and a report at the exhaust: one of the game's gunshot
    // sounds, a little lower, all outside the engine's sound. An engine burning a lot of oil, worked hard, blows
    // burning drops out of the pipe as it runs, too.
    internal sealed class ExhaustFx
    {
        private const string SmokeFsm = "ExhaustSmoke";
        private const int ParticleBudget = 2400;    // per exhaust, shared by its emitters
        private static readonly Color Haze = new Color(0.75f, 0.75f, 0.75f, 1f);
        private static readonly Color Soot = new Color(0.04f, 0.04f, 0.04f, 1f);
        private static readonly Color Oil = new Color(0.4f, 0.45f, 0.58f, 1f);
        private static readonly Color Flame = new Color(1f, 0.55f, 0.15f, 1f);
        private static readonly string[] Gunshots = { "gunshot_1", "gunshot_3", "gunshot_6", "gunshot_7" };
        private static Dictionary<string, AudioClip> _gameClips;
        private static AudioClip _generatedBang;
        private static Shader _fadeShader, _addShader;
        private static bool _shadersLooked;
        private static int _lightFrame = -1;
        private static Color _light = Color.white;
        private static Light _sun;
        private static float _sunLooked = -100f;

        private sealed class Emitter
        {
            public ParticleSystem System;
            public float Rate, SimulationSpeed, LifetimeSeconds, Gravity;
            public bool VelocityOn, ForceOn, NoiseOn, LimitOn, ColorOn;
            public float LimitDrag;
            public ParticleSystem.MinMaxCurve Limit;
            public ParticleSystem.MinMaxGradient ColorOverLife;
            public int MaxParticles;
            public bool Loop, InheritOn, SizeOverLifeOn;
            public ParticleSystemSimulationSpace Space;
            public ParticleSystem.MinMaxCurve Size, Speed, Lifetime, SizeOverLife;
            public ParticleSystem.MinMaxGradient Color;
            public float GrowFrom;    // the emitter's own growth at birth, relative to its end
            public ParticleSystemRenderer Renderer;
            public Material Material;
            public float NormalDirection;
            public ExhaustFire Jet, Drops;
            public float DropsOwed;   // burning drops due but not yet thrown (a fraction carries over)
        }

        private readonly EngineSession _session;
        private readonly List<Emitter> _emitters = new List<Emitter>();
        private readonly PlayMakerFSM _fsm;
        private readonly Light _flash;
        private readonly AudioSource _bang;
        private float _flow, _dirt, _soot, _oil, _flashLeft, _appliedExpansion = -1f;
        private Material _smokeMaterial, _flameMaterial;
        private bool _unlit;

        public ExhaustFx(EngineSession session)
        {
            _session = session;
            var part = session.ExhaustPart;
            if (part == null) return;
            _fsm = part.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == SmokeFsm);
            var systems = part.GetComponentsInChildren<ParticleSystem>(true);
            int share = Mathf.Max(200, ParticleBudget / Mathf.Max(1, systems.Length));
            foreach (var ps in systems)
            {
                var main = ps.main;
                var growth = ps.sizeOverLifetime;
                var e = new Emitter
                {
                    System = ps,
                    Rate = Mathf.Max(1f, ps.emission.rateOverTime.constant),
                    SimulationSpeed = main.simulationSpeed,
                    LifetimeSeconds = Mathf.Max(0.2f, Max(main.startLifetime)),
                    MaxParticles = main.maxParticles,
                    Loop = main.loop,
                    InheritOn = ps.inheritVelocity.enabled,
                    SizeOverLifeOn = growth.enabled,
                    Space = main.simulationSpace,
                    Size = main.startSize,
                    Speed = main.startSpeed,
                    Lifetime = main.startLifetime,
                    SizeOverLife = growth.size,
                    Color = main.startColor,
                    GrowFrom = growth.enabled ? Mathf.Clamp(Evaluate(growth.size, 0f) / Mathf.Max(0.01f, Evaluate(growth.size, 1f)), 0.05f, 1f) : 1f,
                    Gravity = main.gravityModifierMultiplier,
                    VelocityOn = ps.velocityOverLifetime.enabled,
                    ForceOn = ps.forceOverLifetime.enabled,
                    NoiseOn = ps.noise.enabled,
                    LimitOn = ps.limitVelocityOverLifetime.enabled,
                    LimitDrag = ps.limitVelocityOverLifetime.drag.constant,
                    Limit = ps.limitVelocityOverLifetime.limit,
                    ColorOn = ps.colorOverLifetime.enabled,
                    ColorOverLife = ps.colorOverLifetime.color,
                };
                // only the exit push: no lift, air drag slows it
                main.gravityModifierMultiplier = 0f;
                var velocity = ps.velocityOverLifetime; velocity.enabled = false;
                var force = ps.forceOverLifetime; force.enabled = false;
                var noise = ps.noise; noise.enabled = false;
                var limit = ps.limitVelocityOverLifetime;
                limit.enabled = true;
                limit.limit = new ParticleSystem.MinMaxCurve(1000f);   // no speed cap, only drag
                limit.drag = new ParticleSystem.MinMaxCurve(1.5f);
                limit.multiplyDragByParticleSize = false;
                limit.multiplyDragByParticleVelocity = false;
                // thinning as it spreads: opaque near the outlet, fading out over its life
                var fade = ps.colorOverLifetime;
                fade.enabled = true;
                var gradient = new Gradient();
                gradient.SetKeys(
                    new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                    new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.85f, 0.1f), new GradientAlphaKey(0.4f, 0.45f), new GradientAlphaKey(0f, 1f) });
                fade.color = new ParticleSystem.MinMaxGradient(gradient);
                _emitters.Add(e);
                // looping, in real time, in the world, from its emitter's push only
                main.loop = true;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.simulationSpeed = 1f;
                main.maxParticles = share;
                var inherit = ps.inheritVelocity;
                inherit.enabled = false;
                e.Renderer = ps.GetComponent<ParticleSystemRenderer>();
                if (e.Renderer != null)
                {
                    e.Material = e.Renderer.sharedMaterial;
                    e.NormalDirection = e.Renderer.normalDirection;
                }
            }
            if (_emitters.Count == 0) return;
            Materials();
            if (_fsm != null) _fsm.enabled = false;

            var outlet = _emitters[0].System.gameObject;
            _flash = outlet.AddComponent<Light>();
            _flash.type = LightType.Point;
            _flash.color = Flame;
            _flash.range = 1.5f;   // the smoke and pipe at the outlet, not the whole vehicle
            _flash.intensity = 0f;
            _flash.enabled = false;

            _bang = outlet.AddComponent<AudioSource>();
            _bang.spatialBlend = 0f;   // heard by distance (the engine's rolloff), not panned, like the engine
            _bang.playOnAwake = false;
            var pop = session.Vc.soundManager != null ? session.Vc.soundManager.exhaustPopComponent : null;
            if (pop != null && pop.source != null) _bang.outputAudioMixerGroup = pop.source.outputAudioMixerGroup;
        }

        public bool Active { get { return _emitters.Count > 0; } }

        public void Update(int backfires)
        {
            if (_emitters.Count == 0) return;
            float dt = Time.deltaTime, amount = Plugin.SmokeEntry.Value;
            var s = _session;
            var engine = s.Vc.powertrain.engine;

            float flow = Mathf.Clamp(s.ExhaustFlow / s.MaxExhaustFlow, 0f, 2f);
            _flow += (flow - _flow) * (1f - Mathf.Exp(-dt / 0.25f));

            float throttle = engine.ThrottlePosition;
            float wear = 1f - Mathf.Clamp01(s.Condition);
            bool diesel = s.Desc.engine.diesel != 0;
            float unburnt = Mathf.Clamp01((s.ExhaustFuel - 0.0075f) / 0.012f);
            float coal = throttle * Mathf.Clamp01((s.Boost - 1f) / 0.35f) * (diesel ? 1f : 0.55f);
            float dieselLoad = diesel ? throttle * Mathf.Clamp01(_flow) * 0.35f : 0f;
            float soot = Mathf.Clamp01(unburnt + coal + dieselLoad + 0.3f * wear * throttle);
            float oil = Mathf.Clamp01(wear * wear * (0.4f + 0.6f * throttle) * 1.3f);
            float k = 1f - Mathf.Exp(-dt / 0.4f);
            _soot += (soot - _soot) * k;
            _oil += (oil - _oil) * k;
            _dirt = Mathf.Clamp01(_soot + 0.8f * _oil);

            // what it looks like: a faint light haze when clean, blue-grey with oil, black with soot
            Color color = Color.Lerp(Haze, Oil, _oil / Mathf.Max(0.001f, _soot + _oil) * Mathf.Clamp01(_dirt * 2f));
            color = Color.Lerp(color, Soot, _soot / Mathf.Max(0.001f, _soot + _oil) * Mathf.Clamp01(_dirt * 1.5f));
            color.a = 0.07f + 0.85f * _dirt;
            color = Lit(color);
            float lifetime = 1f + 3f * _dirt;          // dirty smoke hangs
            float size = 1f + 0.5f * _flow + 1.5f * _dirt;
            float expansion = 2f + 8f * _dirt;         // and billows: x2 clean, x10 filthy

            bool turning = s.Rpm > 30f && amount > 0f;
            bool regrow = _appliedExpansion < 0f || Mathf.Abs(expansion - _appliedExpansion) > 0.25f;
            if (regrow) _appliedExpansion = expansion;
            foreach (var e in _emitters)
            {
                if (e.System == null) continue;
                var emission = e.System.emission;
                var main = e.System.main;
                if (!turning)
                {
                    if (e.System.isPlaying) e.System.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                    continue;
                }
                if (!e.System.isPlaying) e.System.Play();
                emission.enabled = true;

                // how much: the flow (a little even at idle), more when dirty; held under the particle budget
                float rate = e.Rate * amount * (0.4f + 6f * _flow) * (1f + 2f * _dirt);
                float maxRate = 0.8f * main.maxParticles / (e.LifetimeSeconds * lifetime);
                emission.rateOverTime = Mathf.Min(rate, maxRate);

                // the push the game gave it through its simulation speed (rpm x 0.003), from the flow instead
                main.startSpeed = Scale(e.Speed, 1.5f + 8f * _flow);
                main.startLifetime = Scale(e.Lifetime, lifetime);
                // growth over life: from the emitter's own start (GrowFrom) to 'expansion' times its own end;
                // the start size carries the expansion, the curve takes it back to the birth size
                main.startSize = Scale(e.Size, size * _appliedExpansion);
                main.startColor = color;
                if (regrow)
                {
                    var growth = e.System.sizeOverLifetime;
                    growth.enabled = true;
                    growth.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
                        new Keyframe(0f, e.GrowFrom / _appliedExpansion), new Keyframe(1f, 1f)));
                }

                // an engine burning a lot of oil, worked hard, blows burning drops of it out of the pipe: none
                // below half its worst, up to a few a second flat out
                if (e.Drops != null)
                {
                    e.DropsOwed += dt * 4f * amount * Mathf.Clamp01((_oil - 0.5f) / 0.5f) * throttle * Mathf.Clamp01(_flow);
                    if (e.DropsOwed >= 1f)
                    {
                        Vector3 at = e.System.transform.position, dir = Outward(e.System);
                        for (; e.DropsOwed >= 1f; e.DropsOwed -= 1f) ThrowDrop(e, at, dir, 1.5f, 4f);
                    }
                }
            }

            if (backfires > 0 && Plugin.BackfireEntry.Value > 0f) Backfire();
            if (_flash != null && _flashLeft > 0f)
            {
                _flashLeft -= dt;
                _flash.intensity = 1.2f * Mathf.Clamp01(_flashLeft / 0.05f);
                if (_flashLeft <= 0f) _flash.enabled = false;
            }
        }

        private void Backfire()
        {
            foreach (var e in _emitters)
            {
                if (e.System == null) continue;
                var main = e.System.main;
                Vector3 at = e.System.transform.position, dir = Outward(e.System);
                // the flame: a jet out of the pipe, gone in a few hundredths of a second
                if (e.Jet != null)
                    for (int i = 0; i < 12; i++)
                        e.Jet.Emit(at + dir * Random.Range(0f, 0.05f), Spread(dir, 6f) * Random.Range(8f, 16f), Random.Range(0.1f, 0.2f), Random.Range(0.04f, 0.09f));
                // an oily engine's backfire spits burning oil with it
                if (e.Drops != null)
                {
                    int drops = Mathf.RoundToInt(10f * _oil * Random.Range(0.6f, 1.4f));
                    for (int i = 0; i < drops; i++) ThrowDrop(e, at, dir, 3f, 7f);
                }
                // the slug of fuel-rich smoke: leaves fast and billows past the running smoke's size; born at the
                // running smoke's full size (its growth curve takes it back to the birth size and out again)
                float size = Max(main.startSize) * 1.6f, life = Max(main.startLifetime) * 1.5f;
                for (int i = 0; i < 10; i++)
                {
                    var puff = new ParticleSystem.EmitParams
                    {
                        position = at + dir * Random.Range(0f, 0.1f),
                        velocity = Spread(dir, 15f) * Random.Range(4f, 9f),
                        startColor = Lit(new Color(0.06f, 0.055f, 0.05f, 0.9f)),
                        startSize = size * Random.Range(0.8f, 1.2f),
                        startLifetime = life * Random.Range(0.8f, 1.2f),
                        applyShapeToPosition = false,
                    };
                    e.System.Emit(puff, 1);
                }
            }
            if (_flash != null)
            {
                _flash.enabled = true;
                _flashLeft = 0.05f;
            }
            if (_bang != null)
            {
                var clip = BackfireClip();
                bool gunshot = clip != _generatedBang;
                _bang.pitch = gunshot ? Random.Range(0.8f, 0.92f) : Random.Range(0.9f, 1.1f);   // an exhaust, a little lower than a rifle
                float gain = _session.DistanceGain(_bang.transform.position) * Plugin.VolumeEntry.Value;
                _bang.PlayOneShot(clip, Plugin.BackfireEntry.Value * gain);
            }
        }

        // the chosen report: one of the game's gunshot sounds (loaded with the game's guns), else generated
        private static AudioClip BackfireClip()
        {
            string choice = Plugin.BackfireSoundEntry.Value;
            if (choice != "generated")
            {
                if (_gameClips == null)
                {
                    _gameClips = new Dictionary<string, AudioClip>();
                    foreach (var c in Resources.FindObjectsOfTypeAll<AudioClip>())
                        if (c != null && System.Array.IndexOf(Gunshots, c.name) >= 0 && !_gameClips.ContainsKey(c.name)) _gameClips[c.name] = c;
                    Plugin.Verbose("backfire reports found: " + (_gameClips.Count > 0 ? string.Join(", ", _gameClips.Keys.ToArray()) : "none, using the generated one"));
                }
                AudioClip clip;
                if (_gameClips.Count == 0) { _gameClips = null; return Report(); }   // not loaded yet: look again next time
                if (choice == "gunshots") return _gameClips.Values.ElementAt(Random.Range(0, _gameClips.Count));
                if (_gameClips.TryGetValue(choice, out clip)) return clip;
            }
            return Report();
        }

        // a burning drop of oil out of the pipe: thrown at a few m/s, then it falls, lands and burns out
        private static void ThrowDrop(Emitter e, Vector3 at, Vector3 dir, float slowest, float fastest)
        {
            e.Drops.Emit(at + dir * 0.03f, Spread(dir, 20f) * Random.Range(slowest, fastest), Random.Range(0.04f, 0.1f), Random.Range(1f, 3f));
        }

        // which way the emitter blows: its shape's axis (a cone's), in the world
        private static Vector3 Outward(ParticleSystem ps)
        {
            var shape = ps.shape;
            var local = shape.enabled ? Quaternion.Euler(shape.rotation) * Vector3.forward : Vector3.forward;
            return ps.transform.TransformDirection(local).normalized;
        }

        private static Vector3 Spread(Vector3 dir, float degrees)
        {
            return Quaternion.AngleAxis(Random.Range(0f, degrees), Vector3.Cross(dir, Random.onUnitSphere).normalized) * dir;
        }

        // The smoke's colour in the scene's light: the ambient light (its probe, around the horizon and up) plus
        // the sun's share, half its light (a cloud scatters about half the light falling on it towards any eye)
        // as far as the sun is up. With the game's own (lit) material the colour is left as it is.
        private Color Lit(Color albedo)
        {
            if (!_unlit) return albedo;
            if (_lightFrame != Time.frameCount)
            {
                _lightFrame = Time.frameCount;
                var probe = RenderSettings.ambientProbe;
                var dirs = new[] { Vector3.up, Vector3.forward, Vector3.back, Vector3.left, Vector3.right };
                var got = new Color[dirs.Length];
                probe.Evaluate(dirs, got);
                Color ambient = Color.black;
                foreach (var c in got) ambient += c;
                ambient /= got.Length;
                if (_sun == null && Time.time - _sunLooked > 5f)
                {
                    _sunLooked = Time.time;
                    _sun = RenderSettings.sun != null ? RenderSettings.sun
                        : Object.FindObjectsOfType<Light>().Where(l => l.type == LightType.Directional).OrderByDescending(l => l.intensity).FirstOrDefault();
                }
                Color sun = Color.black;
                if (_sun != null && _sun.isActiveAndEnabled)
                    sun = _sun.color * _sun.intensity * 0.5f * Mathf.Clamp01(-_sun.transform.forward.y * 5f);
                _light = ambient + sun;
            }
            return new Color(Mathf.Min(1f, albedo.r * _light.r), Mathf.Min(1f, albedo.g * _light.g), Mathf.Min(1f, albedo.b * _light.b), albedo.a);
        }

        // An unlit smoke material (the game's texture, alpha blended) and an additive one for flame, if the game
        // has the shaders; else the game's material with spherical normals, and no flame jet.
        private void Materials()
        {
            if (!_shadersLooked)
            {
                _shadersLooked = true;
                var standard = Shader.Find("Particles/Standard Unlit");
                _fadeShader = standard ?? Shader.Find("Legacy Shaders/Particles/Alpha Blended") ?? Shader.Find("Mobile/Particles/Alpha Blended");
                _addShader = standard ?? Shader.Find("Legacy Shaders/Particles/Additive") ?? Shader.Find("Mobile/Particles/Additive");
            }
            var first = _emitters[0];
            Plugin.Verbose("exhaust smoke: " + _emitters.Count + " emitter(s), game material '" + (first.Material != null ? first.Material.name : "none")
                + "' (shader '" + (first.Material != null && first.Material.shader != null ? first.Material.shader.name : "none") + "', normals "
                + first.NormalDirection.ToString("0.##") + "); unlit smoke " + (_fadeShader != null ? "'" + _fadeShader.name + "'" : "unavailable")
                + ", flame " + (_addShader != null ? "'" + _addShader.name + "'" : "unavailable"));
            var texture = first.Material != null && first.Material.HasProperty("_MainTex") ? first.Material.mainTexture : null;
            if (_fadeShader != null)
            {
                _smokeMaterial = new Material(_fadeShader) { name = "EngineSim smoke", mainTexture = texture };
                Blend(_smokeMaterial, false);
                _unlit = true;
            }
            if (_addShader != null)
            {
                _flameMaterial = new Material(_addShader) { name = "EngineSim flame", mainTexture = texture };
                Blend(_flameMaterial, true);
            }
            foreach (var e in _emitters)
            {
                if (e.Renderer == null) continue;
                if (_smokeMaterial != null) e.Renderer.sharedMaterial = _smokeMaterial;
                else e.Renderer.normalDirection = 0f;
                e.Jet = ExhaustFire.At(e.System.transform, false, _flameMaterial);
                e.Drops = ExhaustFire.At(e.System.transform, true, _flameMaterial);
            }
        }

        // Particles/Standard Unlit's own blend settings (what its material editor sets): alpha blended (Fade) or
        // additive; the legacy shaders blend by themselves
        private static void Blend(Material m, bool additive)
        {
            if (m.shader.name != "Particles/Standard Unlit") return;
            m.SetFloat("_Mode", additive ? 4f : 2f);
            m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)(additive ? UnityEngine.Rendering.BlendMode.One : UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha));
            m.SetInt("_ZWrite", 0);
            m.DisableKeyword("_ALPHATEST_ON");
            m.EnableKeyword("_ALPHABLEND_ON");
            m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            m.DisableKeyword("_ALPHAMODULATE_ON");
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }

        // a MinMaxCurve scaled, whatever its mode (the 'multiplier' properties don't scale two constants)
        private static ParticleSystem.MinMaxCurve Scale(ParticleSystem.MinMaxCurve c, float f)
        {
            switch (c.mode)
            {
            case ParticleSystemCurveMode.Constant: return new ParticleSystem.MinMaxCurve(c.constant * f);
            case ParticleSystemCurveMode.TwoConstants: return new ParticleSystem.MinMaxCurve(c.constantMin * f, c.constantMax * f);
            case ParticleSystemCurveMode.Curve: return new ParticleSystem.MinMaxCurve(c.curveMultiplier * f, c.curve);
            default: return new ParticleSystem.MinMaxCurve(c.curveMultiplier * f, c.curveMin, c.curveMax);
            }
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

        private static float Evaluate(ParticleSystem.MinMaxCurve c, float t)
        {
            switch (c.mode)
            {
            case ParticleSystemCurveMode.Constant: return c.constant;
            case ParticleSystemCurveMode.TwoConstants: return 0.5f * (c.constantMin + c.constantMax);
            case ParticleSystemCurveMode.Curve: return c.curveMultiplier * c.curve.Evaluate(t);
            default: return c.curveMultiplier * 0.5f * (c.curveMin.Evaluate(t) + c.curveMax.Evaluate(t));
            }
        }

        // A generated report: the crack of the pressure front (a near-instant rise, a few ms of broadband
        // burst), a short low thump of the gas leaving the pipe, and a brief ring of the pipe itself.
        private static AudioClip Report()
        {
            if (_generatedBang != null) return _generatedBang;
            int rate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;
            int n = (int)(0.25f * rate);
            var data = new float[n];
            var rng = new System.Random(7);
            float peak = 1e-6f;
            for (int i = 0; i < n; ++i)
            {
                float t = (float)i / rate;
                float noise = (float)(rng.NextDouble() * 2 - 1);
                float crack = Mathf.Exp(-t / 0.003f) * noise * (t < 0.0003f ? t / 0.0003f : 1f);
                float thump = 0.8f * Mathf.Exp(-t / 0.035f) * Mathf.Sin(2 * Mathf.PI * 85f * t);
                float ring = 0.18f * Mathf.Exp(-t / 0.06f) * Mathf.Sin(2 * Mathf.PI * 410f * t + 0.6f * noise);
                data[i] = crack + thump + ring;
                peak = Mathf.Max(peak, Mathf.Abs(data[i]));
            }
            for (int i = 0; i < n; ++i) data[i] *= 0.95f / peak;
            _generatedBang = AudioClip.Create("EngineSim backfire", n, 1, rate, false);
            _generatedBang.SetData(data, 0);
            return _generatedBang;
        }

        public void Release()
        {
            foreach (var e in _emitters)
            {
                if (e.System == null) continue;
                var emission = e.System.emission;
                emission.rateOverTime = e.Rate;
                var main = e.System.main;
                main.startSize = e.Size;
                main.startSpeed = e.Speed;
                main.startLifetime = e.Lifetime;
                main.startColor = e.Color;
                main.simulationSpeed = e.SimulationSpeed;
                main.maxParticles = e.MaxParticles;
                main.simulationSpace = e.Space;
                main.loop = e.Loop;
                var inherit = e.System.inheritVelocity;
                inherit.enabled = e.InheritOn;
                var growth = e.System.sizeOverLifetime;
                growth.size = e.SizeOverLife;
                growth.enabled = e.SizeOverLifeOn;
                main.gravityModifierMultiplier = e.Gravity;
                var velocity = e.System.velocityOverLifetime; velocity.enabled = e.VelocityOn;
                var force = e.System.forceOverLifetime; force.enabled = e.ForceOn;
                var noise = e.System.noise; noise.enabled = e.NoiseOn;
                var limit = e.System.limitVelocityOverLifetime;
                limit.drag = new ParticleSystem.MinMaxCurve(e.LimitDrag);
                limit.limit = e.Limit;
                limit.enabled = e.LimitOn;
                var fade = e.System.colorOverLifetime;
                fade.color = e.ColorOverLife;
                fade.enabled = e.ColorOn;
                if (e.Renderer != null)
                {
                    e.Renderer.sharedMaterial = e.Material;
                    e.Renderer.normalDirection = e.NormalDirection;
                }
                e.Jet?.Destroy();
                e.Drops?.Destroy();
            }
            if (_smokeMaterial != null) Object.Destroy(_smokeMaterial);
            if (_flameMaterial != null) Object.Destroy(_flameMaterial);
            if (_fsm != null) _fsm.enabled = true;
            if (_flash != null) Object.Destroy(_flash);
            if (_bang != null) Object.Destroy(_bang);
        }
    }
}
