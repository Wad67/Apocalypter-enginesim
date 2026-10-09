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
    // Backfires (detected by the simulation) come out of the same emitters: a small burst of flame-lit
    // and dark smoke, a dim orange flicker at the outlet, and a report at the exhaust: one of the game's
    // gunshot sounds, a little lower, all outside the engine's sound.
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
        }

        private readonly EngineSession _session;
        private readonly List<Emitter> _emitters = new List<Emitter>();
        private readonly PlayMakerFSM _fsm;
        private readonly Light _flash;
        private readonly AudioSource _bang;
        private float _flow, _dirt, _soot, _oil, _flashLeft, _appliedExpansion = -1f;

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
            }
            if (_emitters.Count == 0) return;
            if (_fsm != null) _fsm.enabled = false;

            var outlet = _emitters[0].System.gameObject;
            _flash = outlet.AddComponent<Light>();
            _flash.type = LightType.Point;
            _flash.color = Flame;
            _flash.range = 0.8f;   // the smoke at the outlet, not the vehicle
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
                float baseSize = Max(e.Size);
                var flame = new ParticleSystem.EmitParams { startColor = new Color(0.9f, 0.45f, 0.12f, 0.5f), startSize = baseSize, startLifetime = 0.08f };
                e.System.Emit(flame, 2);
                var puff = new ParticleSystem.EmitParams { startColor = new Color(0.05f, 0.05f, 0.05f, 0.85f), startSize = baseSize * 2f };
                e.System.Emit(puff, 4);
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
            }
            if (_fsm != null) _fsm.enabled = true;
            if (_flash != null) Object.Destroy(_flash);
            if (_bang != null) Object.Destroy(_bang);
        }
    }
}
