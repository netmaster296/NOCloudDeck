using System;
using HarmonyLib;
using UnityEngine;

namespace CloudDeck
{
    /// <summary>
    /// Occasional lightning that reaches the ground.
    ///
    /// Stock (Lightning.cs, game 0.34.6): the Lightning object is on only in a WeatherSet with lightning (Thunderstorm,
    /// CloudLayer.cs:191-198). Update (:72-87) counts strikeTimer down and calls GenerateStrike, then sets
    /// strikeTimer = Random.Range(2, 20) (int overload: 2-19 s). GenerateStrike (:34-70) puts a strike about 10 km
    /// ahead of the camera +-10 km, emits 30 ribbon-trail particles from cloudHeight + 50 m down in 60 m steps
    /// (a fixed 1.7 km bolt that may end in mid-air or under ground), turns on a 1e7 flash light at a position that
    /// mixes world and global coordinates (:41, 46, 50), and within 8 km plays one of four thunder clips through
    /// ExplosionAudioManager, which delays it at 340 m/s (ExplosionAudioManager.cs:42-50).
    ///
    /// Here, while active:
    ///  - a postfix on Update replaces the timer after each strike (and when taking over) with
    ///    Random.Range(LightningMinInterval, LightningMaxInterval); the first strike comes after 25-50 % of that;
    ///  - a prefix replaces GenerateStrike: candidate points lie 30-90 % of the local under-base visibility away (1.5 to
    ///    8 km) within 70 degrees of the camera's heading, so that most strikes can be seen through the rain haze and
    ///    all are within the stock 8 km thunder range; they are kept with a probability that rises with the rain map
    ///    (0.2 + 0.8 x intensity) and must have the ground, found by a raycast from 9.5 km down the game's statics
    ///    layer, at least 150 m under the base. A point with no ground hit (open water) is used only if no candidate
    ///    hits the ground; if none fits at all, the strike is skipped and retried after 5-15 s. The bolt runs from
    ///    base + 50 m to the ground; the flash sits at the bolt's mean in Datum.origin-local (= global) coordinates,
    ///    its parent being Datum.origin (Lightning.cs:28); the thunder is the stock code path (clip, AudioSource
    ///    settings, ExplosionAudioManager, 35 s lifetime), heard within 8 km horizontally.
    /// Strikes are local: each player sees different ones, as in stock.
    /// </summary>
    internal static class Storm
    {
        private static AccessTools.FieldRef<Lightning, ParticleSystem> _system;
        private static AccessTools.FieldRef<Lightning, ParticleSystem.EmitParams> _emit;
        private static AccessTools.FieldRef<Lightning, Light> _flash;
        private static AccessTools.FieldRef<Lightning, float> _flashIntensity;
        private static AccessTools.FieldRef<Lightning, AudioClip[]> _thunder;
        private static AccessTools.FieldRef<Lightning, float> _timer;

        private static bool _timerOk;
        private static bool _strikeOk;
        private static bool _failed;
        private static Lightning _owner;
        private static bool _struck;
        private static bool _retry;
        private const int Candidates = 12;

        internal static void Apply(Harmony harmony)
        {
            try
            {
                _system = AccessTools.FieldRefAccess<Lightning, ParticleSystem>("lightningSystem");
                _emit = AccessTools.FieldRefAccess<Lightning, ParticleSystem.EmitParams>("emitParams");
                _flash = AccessTools.FieldRefAccess<Lightning, Light>("flashLight");
                _flashIntensity = AccessTools.FieldRefAccess<Lightning, float>("flashIntensity");
                _thunder = AccessTools.FieldRefAccess<Lightning, AudioClip[]>("thunderClips");
                _timer = AccessTools.FieldRefAccess<Lightning, float>("strikeTimer");
            }
            catch (Exception e)
            {
                Plugin.ErrorOnce("storm-fields", "finding the lightning's fields (the game changed?)", e);
                return;
            }
            try
            {
                harmony.Patch(AccessTools.Method(typeof(Lightning), "Update"),
                    postfix: new HarmonyMethod(typeof(Storm), nameof(AfterUpdate)));
                _timerOk = true;
                // OnEnable sets strikeTimer = Random.Range(2, 20) (Lightning.cs:30); take it over again from there
                harmony.Patch(AccessTools.Method(typeof(Lightning), "OnEnable"),
                    postfix: new HarmonyMethod(typeof(Storm), nameof(AfterOnEnable)));
            }
            catch (Exception e)
            {
                Plugin.ErrorOnce("storm-patch1", "patching Lightning.Update", e);
            }
            try
            {
                harmony.Patch(AccessTools.Method(typeof(Lightning), "GenerateStrike"),
                    prefix: new HarmonyMethod(typeof(Storm), nameof(StrikePrefix)));
                _strikeOk = true;
            }
            catch (Exception e)
            {
                Plugin.ErrorOnce("storm-patch2", "patching Lightning.GenerateStrike", e);
            }
        }

        private static float MinInterval => Mathf.Max(2f, Plugin.LightningMinInterval.Value);
        private static float MaxInterval => Mathf.Max(MinInterval, Plugin.LightningMaxInterval.Value);

        private static void AfterUpdate(Lightning __instance)
        {
            if (!_timerOk)
                return;
            try
            {
                ref float timer = ref _timer(__instance);
                if (Plugin.Active && !GameManager.IsHeadless)
                {
                    if (_owner != __instance)
                    {
                        _owner = __instance;
                        _struck = false;
                        timer = UnityEngine.Random.Range(0.25f, 0.5f) * UnityEngine.Random.Range(MinInterval, MaxInterval);
                    }
                    else if (_struck)
                    {
                        _struck = false;
                        // replaces Lightning.cs:85; a skipped strike (no place for it) is retried after 5-15 s
                        timer = _retry ? UnityEngine.Random.Range(5f, 15f) : UnityEngine.Random.Range(MinInterval, MaxInterval);
                        _retry = false;
                    }
                }
                else if (_owner == __instance)
                {
                    _owner = null;
                    timer = Mathf.Min(timer, UnityEngine.Random.Range(2, 20)); // back to the stock cadence
                }
            }
            catch (Exception e)
            {
                _timerOk = false;
                Plugin.ErrorOnce("storm-timer", "the lightning interval", e);
            }
        }

        private static void AfterOnEnable(Lightning __instance)
        {
            try
            {
                if (_owner == __instance)
                    _owner = null; // the next AfterUpdate sets the first-strike delay
            }
            catch (Exception e)
            {
                Plugin.ErrorOnce("storm-enable", "the lightning take-over", e);
            }
        }

        private static bool StrikePrefix(Lightning __instance)
        {
            if (!Plugin.Active)
                return true;
            _struck = true;
            if (!_strikeOk || _failed)
                return true;
            try
            {
                return !Strike(__instance);
            }
            catch (Exception e)
            {
                _failed = true;
                Plugin.ErrorOnce("storm-strike", "the lightning strike", e);
                return true;
            }
        }

        /// <summary>Draws a strike. Returns false to let the stock GenerateStrike run instead. When no candidate point
        /// fits (for example the camera is over mountains that reach into the deck), no strike is drawn this time and
        /// the next one is tried after a short delay (see AfterUpdate).</summary>
        private static bool Strike(Lightning lightning)
        {
            CameraStateManager csm = SceneSingleton<CameraStateManager>.i;
            LevelInfo li = NetworkSceneSingleton<LevelInfo>.i;
            ParticleSystem system = _system(lightning);
            Light flash = _flash(lightning);
            if (csm == null || li == null || system == null || flash == null || Datum.origin == null)
                return false;
            Transform cam = csm.transform;
            float b = li.cloudHeight;
            Vector3 forward = cam.forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude > 1e-4f ? forward.normalized : Vector3.forward;

            // Placement. Stock puts strikes 0-22 km away (Lightning.cs:37-38), but under the base the rain haze limits
            // visibility to 5-12 km (Atmosphere.VisibilityBelow) and thunder is only played within 8 km (:53), so most
            // stock-placed strikes would be neither seen nor heard. Here a strike lands 30-90 % of the local visibility
            // away (at most 8 km, at least 1.5 km), within 70 degrees of the camera's heading.
            GlobalPosition camG = cam.position.ToGlobalPosition();
            float reach = Mathf.Min(8000f, Atmosphere.VisibilityBelow(Plugin.RainIntensityAt(camG.x, camG.z)) * 1000f);

            GlobalPosition at = default;
            float ground = 0f;
            bool found = false;
            bool haveSea = false;
            GlobalPosition sea = default;
            for (int k = 0; k < Candidates && !found; k++)
            {
                float bearing = UnityEngine.Random.Range(-70f, 70f);
                float distance = Mathf.Max(1500f, UnityEngine.Random.Range(0.3f, 0.9f) * reach);
                Vector3 world = cam.position + Quaternion.AngleAxis(bearing, Vector3.up) * forward * distance;
                GlobalPosition g = world.ToGlobalPosition();
                bool last = k == Candidates - 1;
                float wet = Plugin.UseRainMap.Value && RainMap.Loaded ? Plugin.RainMapAt(g.x, g.z) : Plugin.FlatIntensity;
                if (!last && UnityEngine.Random.value > 0.2f + 0.8f * wet)
                    continue;
                if (GroundHeight(g, b, out float h))
                {
                    if (h > b - 150f)
                        continue; // ground too close to (or above) the base: no room for a visible bolt
                    at = g;
                    ground = Mathf.Max(0f, h);
                    found = true;
                }
                else if (!haveSea)
                {
                    // nothing static under the point: open water, or (less likely within 8 km of the camera) terrain
                    // with no collider loaded. Kept as a fallback, used only if no candidate hits the ground.
                    haveSea = true;
                    sea = g;
                }
            }
            if (!found && haveSea)
            {
                at = sea;
                ground = 0f;
                found = true;
            }
            if (!found)
            {
                _retry = true;
                return true; // skip this strike (the stock one would also be in the hills); try again soon
            }

            // the bolt: 30 ribbon points from base + 50 m to the ground, in the system's custom space (Datum.origin,
            // Lightning.cs:26-27), whose local coordinates are global ones
            const int points = 30;
            ref ParticleSystem.EmitParams emit = ref _emit(lightning);
            float top = Plugin.LocalBaseAt(at.x, at.z) + 50f;
            float step = (top - ground) / (points - 1);
            Vector3 p = new Vector3(at.x, top, at.z);
            Vector3 sum = Vector3.zero;
            flash.enabled = true;
            for (int i = 0; i < points; i++)
            {
                if (i == points - 1)
                    p.y = ground;
                emit.position = p;
                system.Emit(emit, 1);
                sum += p;
                float jitter = Mathf.Lerp(40f, 15f, i / (points - 1f));
                p += new Vector3(UnityEngine.Random.Range(-jitter, jitter), -step, UnityEngine.Random.Range(-jitter, jitter));
            }
            flash.transform.localPosition = sum / points; // parent is Datum.origin (Lightning.cs:28)
            flash.intensity = _flashIntensity(lightning);

            // thunder, as stock (Lightning.cs:52-69), within 8 km
            Vector3 strikeWorld = new GlobalPosition(at.x, 0f, at.z).ToLocalPosition();
            Vector3 d = strikeWorld - cam.position;
            d.y = 0f;
            AudioClip[] clips = _thunder(lightning);
            ExplosionAudioManager audio = SceneSingleton<ExplosionAudioManager>.i;
            if (d.sqrMagnitude <= 8000f * 8000f && clips != null && clips.Length > 0 && audio != null)
            {
                var go = new GameObject("Lightning Sound");
                go.transform.SetParent(Datum.origin);
                go.transform.position = new Vector3(strikeWorld.x, cam.position.y, strikeWorld.z);
                AudioSource source = go.AddComponent<AudioSource>();
                source.maxDistance = 8000f;
                source.spatialBlend = 1f;
                source.dopplerLevel = 0f;
                source.spread = 20f;
                source.rolloffMode = AudioRolloffMode.Linear;
                source.clip = clips[UnityEngine.Random.Range(0, clips.Length)];
                AudioLowPassFilter filter = go.AddComponent<AudioLowPassFilter>();
                audio.AddExplosionAudio(source, filter, 0f);
                UnityEngine.Object.Destroy(go, 35f);
            }
            return true;
        }

        /// <summary>Ground height (m above sea level) under a point: a ray down the game's statics layer (terrain,
        /// buildings: PhysicsLayers.StaticsMask, as AIHeloTransportState.cs:135 uses). The ray starts far above any
        /// terrain (the game does the same: Aircraft.cs:1430 linecasts 10 km down, AIHeloTransportState.cs:135 from
        /// +4 km), so it cannot start inside a mountain. False if nothing is hit (open water, or no collider there).</summary>
        private static bool GroundHeight(GlobalPosition g, float b, out float height)
        {
            float start = Mathf.Max(b, 5000f) + 4500f;
            Vector3 from = new GlobalPosition(g.x, start, g.z).ToLocalPosition();
            if (Physics.Raycast(from, Vector3.down, out RaycastHit hit, start + 500f, PhysicsLayers.StaticsMask))
            {
                height = hit.point.GlobalY();
                return true;
            }
            height = 0f;
            return false;
        }

        internal static void StandDown()
        {
            // the timer goes back to the stock 2-19 s in AfterUpdate; nothing else is kept
            _struck = false;
            _retry = false;
        }

        internal static void ResetFailure()
        {
            _failed = false;
        }
    }
}
