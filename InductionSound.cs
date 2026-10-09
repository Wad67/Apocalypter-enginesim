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
            st.NextScan = 0f;   // rebuilt on the next update
        }
    }
}
