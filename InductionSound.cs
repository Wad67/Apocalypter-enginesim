using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using NWH.VehiclePhysics2;
using NWH.VehiclePhysics2.Powertrain;
using NWH.VehiclePhysics2.Sound.SoundComponents;
using UnityEngine;

namespace EngineSim
{
    // The game's turbo / supercharger spool sound (NWH TurboWhistleComponent), re-proportioned and
    // split per unit. Stock, one whistle follows NWH's boost value (volume boost^2, pitch boost), which
    // is clamp01(rpm% + 0.5): it tops out at half the rev range, and for a turbo drops to 0 the moment
    // the wastegate opens.
    //
    // Here each turbo of the fitted part (EngineSim.induction.cfg: one relative size per turbo) has
    // its own shaft and its own source playing the part's clip: it spools with exhaust flow (rpm x
    // load), lags low in the revs and coasts down after a lift; small turbos spool sooner and whistle
    // higher than large ones. A supercharger is belt driven: its whine follows the crank, lower and
    // louder for a bigger blower. The stock whistle source is kept silent; the clip the part sets,
    // play / stop and the wastegate flutter stay the game's.
    [HarmonyPatch(typeof(TurboWhistleComponent), nameof(TurboWhistleComponent.VC_Update))]
    internal static class InductionSound
    {
        private sealed class Unit
        {
            public float Size, Weight, Detune, Spool;
            public AudioSource Source;
            // with the simulated drivetrain: the unit's own clip, and which simulated turbo it is (-1: the blower)
            public AudioClip Clip;
            public int Turbo = -1;
        }

        private sealed class State
        {
            public long PartId = -1;
            public bool Supercharger;
            public float[] Sizes;
            public Unit[] Units = new Unit[0];
            public GameObject Holder;
            public Rolloff Rolloff;
            public float Load, NextScan, NextStats;
            // with the simulated drivetrain: the fitted parts the units were built for, and the blow-off clip
            public string MachineKey;
            public AudioClip Flutter;
            public AudioSource FlutterSource;
        }

        private static readonly ConditionalWeakTable<TurboWhistleComponent, State> States =
            new ConditionalWeakTable<TurboWhistleComponent, State>();

        private static void Postfix(TurboWhistleComponent __instance)
        {
            var st = States.GetOrCreateValue(__instance);
            var stock = __instance.source;
            var vc = __instance.vehicleController;
            if (!Plugin.InductionEntry.Value || vc == null || stock == null)
            {
                Release(st);   // the stock whistle takes over again
                return;
            }
            var engine = vc.powertrain.engine;

            // with the simulated drivetrain the units are the simulation's machines
            var session = DriveCoupling.SessionOf(engine);
            if (session != null)
            {
                MachineSound(__instance, st, session);
                return;
            }
            if (st.MachineKey != null) Release(st);

            var fi = engine.forcedInduction;
            if (fi == null || !fi.useForcedInduction)
            {
                Release(st);
                return;
            }

            // which part is fitted (and so how many turbos), re-checked every second
            bool supercharger = fi.forcedInductionType == EngineComponent.ForcedInduction.ForcedInductionType.Supercharger;
            if (Time.unscaledTime >= st.NextScan || supercharger != st.Supercharger)
            {
                st.NextScan = Time.unscaledTime + 1f;
                long id = PartTable.Fitted(vc).InductionId;
                var sizes = PartTable.InductionUnits(id, supercharger);
                if (id != st.PartId || supercharger != st.Supercharger || !ReferenceEquals(sizes, st.Sizes) || st.Holder == null)
                {
                    Build(st, stock, sizes);
                    st.PartId = id;
                    st.Supercharger = supercharger;
                    Plugin.Verbose("induction " + vc.name + ": part " + id + ", " + (supercharger ? "supercharger" : "turbo")
                        + " x" + sizes.Length + " (sizes " + string.Join(", ", sizes.Select(x => x.ToString("0.##")).ToArray())
                        + "), clip " + (stock.clip != null ? stock.clip.name : "none") + ", gain x" + fi.powerGainMultiplier
                        + ", spool-up " + fi.spoolUpTime + " s");
                }
            }

            float dt = Time.deltaTime;
            bool running = engine.IsRunning;
            float rpm = Mathf.Clamp01(engine.OutputRPM / Mathf.Max(1000f, engine.revLimiterRPM));
            float throttle = running && !vc.powertrain.transmission.isShifting ? engine.ThrottlePosition : 0f;
            st.Load = Mathf.Lerp(st.Load, throttle, 1f - Mathf.Exp(-dt / 0.1f));
            float largest = st.Units.Max(u => u.Size);
            // 2D sources, attenuated by distance only: Unity's 3D panning mutes them at some listener angles
            var ear = Listener.Current;
            float gain = ear == null ? 1f : st.Rolloff.Gain(Vector3.Distance(ear.transform.position, st.Holder.transform.position));

            foreach (var u in st.Units)
            {
                if (u.Source == null) continue;
                if (u.Source.clip != stock.clip) { u.Source.clip = stock.clip; if (u.Source.isPlaying) u.Source.Play(); }
                float volume, pitch;
                if (st.Supercharger)
                {
                    u.Spool = running ? rpm : 0f;
                    pitch = Plugin.SuperchargerPitchEntry.Value * Mathf.Max(0.05f, rpm) / Mathf.Sqrt(u.Size);
                    volume = Plugin.SuperchargerVolumeEntry.Value * u.Weight * (0.2f + 0.8f * rpm) * Mathf.Lerp(0.4f, 1f, st.Load);
                    if (!running) volume = 0f;
                }
                else
                {
                    // shaft speed the exhaust can hold: rpm x load, full at TurboFullSpoolRpm (scaled by size)
                    // with the throttle open
                    float fullAt = Mathf.Clamp(Plugin.TurboFullSpoolEntry.Value * Mathf.Sqrt(u.Size), 0.2f, 1.2f);
                    float target = running ? Mathf.Clamp01(rpm * Mathf.Lerp(0.15f, 1f, throttle) / fullAt) : 0f;
                    float tau = target > u.Spool
                        ? Mathf.Max(0.05f, fi.spoolUpTime) * (u.Size / largest) * Mathf.Lerp(2.5f, 1f, rpm)   // less exhaust low down: more lag
                        : Plugin.TurboSpinDownEntry.Value * Mathf.Sqrt(u.Size);
                    u.Spool += (target - u.Spool) * (1f - Mathf.Exp(-dt / tau));
                    pitch = Mathf.Lerp(Plugin.TurboPitchLowEntry.Value, Plugin.TurboPitchHighEntry.Value, u.Spool) / Mathf.Sqrt(u.Size);
                    volume = Plugin.TurboVolumeEntry.Value * u.Weight * u.Spool * u.Spool * Mathf.Lerp(0.3f, 1f, st.Load);
                }
                u.Source.pitch = Mathf.Max(0.01f, __instance.pitchRange * pitch * u.Detune);
                u.Source.volume = Mathf.Clamp01(volume * gain * vc.soundManager.masterVolume);
                if (volume > 0.001f && !u.Source.isPlaying && stock.clip != null)
                {
                    u.Source.time = Random.value * stock.clip.length;   // units out of phase
                    u.Source.Play();
                }
                else if (volume <= 0.001f && u.Source.isPlaying) u.Source.Stop();
            }
            stock.volume = 0f;

            if (Plugin.VerboseLogEntry.Value && Time.unscaledTime >= st.NextStats)
            {
                st.NextStats = Time.unscaledTime + 5f;
                var sb = new StringBuilder("[induction] " + vc.name + ": " + (st.Supercharger ? "supercharger" : "turbo")
                    + " rpm " + (rpm * 100f).ToString("0") + " %, throttle " + throttle.ToString("0.00")
                    + ", clip " + (stock.clip != null ? stock.clip.name : "none")
                    + ", mixer " + (stock.outputAudioMixerGroup != null ? stock.outputAudioMixerGroup.name : "none")
                    + ", distance gain " + gain.ToString("0.00"));
                foreach (var u in st.Units)
                    sb.Append("; size ").Append(u.Size.ToString("0.##")).Append(" spool ").Append(u.Spool.ToString("0.00"))
                      .Append(" vol ").Append(u.Source != null ? u.Source.volume.ToString("0.000") : "-")
                      .Append(" pitch ").Append(u.Source != null ? u.Source.pitch.ToString("0.00") : "-")
                      .Append(u.Source != null && u.Source.isPlaying ? "" : " (stopped)");
                Plugin.Log.LogInfo(sb.ToString());
            }
        }

        // Experimental: each fitted part's units, each with the part's own clip (a turbo and a supercharger
        // together each sound as themselves). A turbo whistles with its simulated shaft speed; the blower
        // whines with the crank, louder the harder it is driven; the blow-off valve flutters when the
        // simulated one lets go, at the game's flutter volume and as loud as the boost it dumps.
        private static void MachineSound(TurboWhistleComponent whistle, State st, EngineSession session)
        {
            var stock = whistle.source;
            var vc = whistle.vehicleController;
            var engine = vc.powertrain.engine;
            if (Time.unscaledTime >= st.NextScan || st.Holder == null)
            {
                st.NextScan = Time.unscaledTime + 1f;
                if (session.InductionKey != st.MachineKey || st.Holder == null) BuildMachines(st, stock, vc);
            }
            stock.volume = 0f;
            if (st.Units.Length == 0) return;

            float rpm = Mathf.Clamp01(engine.OutputRPM / Mathf.Max(1000f, engine.revLimiterRPM));
            bool turning = engine.OutputRPM > 50f;
            var ear = Listener.Current;
            float gain = ear == null ? 1f : st.Rolloff.Gain(Vector3.Distance(ear.transform.position, st.Holder.transform.position));
            float master = vc.soundManager.masterVolume;

            foreach (var u in st.Units)
            {
                if (u.Source == null || u.Clip == null) continue;
                float volume, pitch;
                if (u.Turbo < 0)
                {
                    // driven 1.3x crank speed; louder with the power going into it
                    float work = Mathf.Clamp01(session.SuperchargerKw / (10f * u.Size));
                    u.Spool = turning ? rpm : 0f;
                    pitch = Plugin.SuperchargerPitchEntry.Value * Mathf.Max(0.05f, rpm) / Mathf.Sqrt(u.Size);
                    volume = turning ? Plugin.SuperchargerVolumeEntry.Value * u.Weight * (0.2f + 0.8f * rpm) * Mathf.Lerp(0.4f, 1f, work) : 0f;
                }
                else
                {
                    u.Spool = session.TurboSpeed(u.Turbo);
                    pitch = Mathf.Lerp(Plugin.TurboPitchLowEntry.Value, Plugin.TurboPitchHighEntry.Value, u.Spool) / Mathf.Sqrt(u.Size);
                    volume = Plugin.TurboVolumeEntry.Value * u.Weight * u.Spool * u.Spool;
                }
                u.Source.pitch = Mathf.Max(0.01f, whistle.pitchRange * pitch * u.Detune);
                u.Source.volume = Mathf.Clamp01(volume * gain * master);
                if (volume > 0.001f && !u.Source.isPlaying)
                {
                    u.Source.time = Random.value * u.Clip.length;   // units out of phase
                    u.Source.Play();
                }
                else if (volume <= 0.001f && u.Source.isPlaying) u.Source.Stop();
            }

            float letGo;
            if (session.TakeBlowOffs(out letGo) > 0 && st.Flutter != null && st.FlutterSource != null)
            {
                st.FlutterSource.pitch = Random.Range(0.95f, 1.05f);
                // at the game's own flutter volume, by how much boost it lets go of
                var flutter = vc.soundManager.turboFlutterComponent;
                float baseVolume = flutter != null ? flutter.baseVolume : 1f;
                st.FlutterSource.PlayOneShot(st.Flutter, Mathf.Clamp01(0.3f + letGo / 10f) * baseVolume * gain * master);
            }

            if (Plugin.VerboseLogEntry.Value && Time.unscaledTime >= st.NextStats)
            {
                st.NextStats = Time.unscaledTime + 5f;
                var sb = new StringBuilder("[induction] " + vc.name + ": boost " + session.BoostPsi.ToString("0.0") + " psi, blower "
                    + session.SuperchargerKw.ToString("0.0") + " kW, distance gain " + gain.ToString("0.00"));
                foreach (var u in st.Units)
                    sb.Append("; ").Append(u.Turbo < 0 ? "blower " : "turbo ").Append(u.Size.ToString("0.##"))
                      .Append(" (").Append(u.Clip != null ? u.Clip.name : "no clip").Append(") speed ").Append(u.Spool.ToString("0.00"))
                      .Append(" vol ").Append(u.Source != null ? u.Source.volume.ToString("0.000") : "-");
                Plugin.Log.LogInfo(sb.ToString());
            }
        }

        // the units of every fitted part, in the order the simulation numbers its turbos (InductionMachines.Describe)
        private static void BuildMachines(State st, AudioSource stock, VehicleController vc)
        {
            Release(st);
            var units = new System.Collections.Generic.List<Unit>();
            int turbo = 0;
            bool blower = false;
            AudioClip flutter = null;
            foreach (var kv in PartTable.Fitted(vc).Induction)
            {
                bool supercharger = InductionMachines.IsSupercharger(kv.Value);
                var part = PartTable.Induction(kv.Key, supercharger);
                AudioClip clip, partFlutter;
                InductionMachines.Clips(kv.Value, out clip, out partFlutter);
                if (clip == null) clip = stock.clip;
                if (supercharger)
                {
                    if (blower) continue;
                    blower = true;
                    units.Add(new Unit { Size = part.Sizes.Sum(), Clip = clip });
                }
                else
                {
                    if (flutter == null) flutter = partFlutter;
                    foreach (float size in part.Sizes)
                        if (turbo < Native.MaxTurbos) units.Add(new Unit { Size = size, Clip = clip, Turbo = turbo++ });
                }
            }
            st.Holder = new GameObject("EngineSim Induction");
            st.Holder.transform.SetParent(stock.transform, false);
            st.Rolloff = new Rolloff(stock);
            float sizeSum = Mathf.Max(1f, units.Sum(u => u.Size));
            for (int i = 0; i < units.Count; ++i)
            {
                var u = units[i];
                u.Source = NewSource(st.Holder, stock, u.Clip);
                u.Weight = Mathf.Sqrt(u.Size) / Mathf.Sqrt(sizeSum);
                u.Detune = 1f + 0.02f * (i - (units.Count - 1) / 2f);
            }
            st.Units = units.ToArray();
            st.Flutter = flutter;
            st.FlutterSource = flutter != null ? NewSource(st.Holder, stock, null) : null;
            st.MachineKey = PartTable.Fitted(vc).Induction.Count > 0 ? InductionKeyOf(vc) : "";
            Plugin.Verbose("induction sound " + vc.name + ": " + string.Join(", ", st.Units.Select(u => (u.Turbo < 0 ? "blower " : "turbo ")
                + u.Size.ToString("0.##") + " (" + (u.Clip != null ? u.Clip.name : "no clip") + ")").ToArray())
                + (flutter != null ? ", blow-off " + flutter.name : ""));
        }

        private static string InductionKeyOf(VehicleController vc)
        {
            string key;
            InductionMachines.Describe(PartTable.Fitted(vc), out key);
            return key;
        }

        private static AudioSource NewSource(GameObject holder, AudioSource stock, AudioClip clip)
        {
            var src = holder.AddComponent<AudioSource>();
            src.clip = clip;
            src.outputAudioMixerGroup = stock.outputAudioMixerGroup;
            src.loop = clip != null;
            src.playOnAwake = false;
            src.spatialBlend = 0f;
            src.dopplerLevel = 0f;
            src.priority = stock.priority;
            src.volume = clip != null ? 0f : 1f;
            return src;
        }

        // One source per unit, playing like the stock whistle source (same clip and mixer, its distance
        // rolloff applied by hand) but 2D.
        private static void Build(State st, AudioSource stock, float[] sizes)
        {
            Release(st);
            st.Holder = new GameObject("EngineSim Induction");
            st.Holder.transform.SetParent(stock.transform, false);
            st.Rolloff = new Rolloff(stock);
            float sizeSum = sizes.Sum();
            st.Units = new Unit[sizes.Length];
            for (int i = 0; i < sizes.Length; ++i)
            {
                var src = st.Holder.AddComponent<AudioSource>();
                src.clip = stock.clip;
                src.outputAudioMixerGroup = stock.outputAudioMixerGroup;
                src.loop = true;
                src.playOnAwake = false;
                src.spatialBlend = 0f;
                src.dopplerLevel = 0f;
                src.priority = stock.priority;
                src.volume = 0f;
                st.Units[i] = new Unit
                {
                    Size = sizes[i],
                    // loudness shared out by size: a single unit of size 1 plays at the set volume
                    Weight = Mathf.Sqrt(sizes[i]) / Mathf.Sqrt(Mathf.Max(1f, sizeSum)),
                    // twin units are never quite identical
                    Detune = 1f + 0.02f * (i - (sizes.Length - 1) / 2f),
                    Source = src,
                };
            }
            st.Sizes = sizes;
        }

        private static void Release(State st)
        {
            if (st.Holder != null) Object.Destroy(st.Holder);
            st.Holder = null;
            st.Units = new Unit[0];
            st.PartId = -1;
            st.Sizes = null;
            st.MachineKey = null;
            st.Flutter = null;
            st.FlutterSource = null;
            st.NextScan = 0f;   // rebuilt on the next update
        }
    }
}
