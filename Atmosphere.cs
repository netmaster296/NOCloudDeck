using System;
using HarmonyLib;
using UnityEngine;

namespace CloudDeck
{
    /// <summary>
    /// Light and fog, as a postfix on the private LevelInfo.UpdateTimeOfDayLighting(bool) (LevelInfo.cs:428-502).
    ///
    /// Stock: called every frame from LevelInfo.Update -> UpdateVisuals (:791-827) on every machine that shows effects;
    /// its body runs at most once a second (:430) and not at all when the camera is below sea level (:439-440; with
    /// NOCustomMaps 1.4.2 also below a raised lake, its transpiler on this method). When conditions > 0.6 and the camera
    /// is below the layer + 200 m it greys the fog and sky (:452-460) and turns the sun off at num3 >= 0.9 (:478-479);
    /// it sets exponential fog density = max(airDensity * 0.00014 * hazeAmount + num4 * 0.001, 3e-5) (:469).
    ///
    /// Here (postfix only: NOCustomMaps already has a transpiler on this method), every frame while active:
    ///  - under the base B (the LOCAL base where the regional deck is on, CloudMap.cs): exponential fog for the rain
    ///    haze, visibility from the local rain intensity and, in the lee, the clear-air bonus of the cloud map; the
    ///    stock grey fog colour, sky and sun-off stay as stock made them (the camera is below the stock layer);
    ///  - inside B..T: dense fog (InCloudVisibility) scaled by the local cloud cover (a thin patchy deck east of the
    ///    crest is not solid fog), a grey fog colour, the sky occluded and the sun hidden, by the same factor;
    ///  - above T: clear air (the stock haze term) and the stock sun;
    ///  - 40 m blends at B and T so climbing through the deck has no hard step;
    ///  - as the weather clears (Plugin.Wetness) the rain haze and the in-cloud fog thin out, and during the final
    ///    fade-out everything blends back to the stock values (Plugin.FadeFactor).
    /// The sun is hidden by deactivating its GameObject only, as the stock code does (:478): the IR seeker reads
    /// sun.color and sun.transform (IRSeeker.cs:208-209), which are never touched. RenderSettings.fog itself is never
    /// switched (TargetCam.cs:99-107 toggles it per camera). Under water (CameraStateManager.underwater, set in
    /// CameraStateManager.LateUpdate :436-446 and made lake-aware by NOCustomMaps) nothing is written, so the stock
    /// underwater fog (density 0.05) stays.
    /// Exponential fog: transmittance exp(-d r); visibility V (2 % contrast) <=> d = 3.912 / V. If the scene uses
    /// Exp2 (transmittance exp(-(d r)^2)) it is d = sqrt(3.912) / V = 1.978 / V; the mode is read from
    /// RenderSettings.fogMode and logged (1.1.x assumed Exp, which under Exp2 gave about half the visibility).
    /// Ambient (1.2.0): see LiftAmbient.
    /// </summary>
    internal static class Atmosphere
    {
        private const float Blend = 40f;
        private const float Koschmieder = 3.912f;               // Exp fog: d = 3.912 / V
        private const float KoschmiederExp2 = 1.97777f;           // Exp2 fog: d = sqrt(3.912) / V
        private const float LiftSeconds = 3f;                      // the ambient lift eases in and out over this long

        private static AccessTools.FieldRef<LevelInfo, float> _lightingLastUpdated;
        private static AccessTools.FieldRef<LevelInfo, CloudLayer> _cloudLayer;
        private static AccessTools.FieldRef<LevelInfo, LevelInfo.SkyAnimData> _skyAnimData;
        private static AccessTools.FieldRef<CameraStateManager, bool> _underwater;

        private static readonly int IdFogColor = Shader.PropertyToID("_FogColor");
        private static readonly int IdCloudOcclusion = Shader.PropertyToID("_CloudOcclusion");

        private static bool _ok;
        private static bool _failed;
        private static float _lastStamp = float.NaN;

        // what the stock body wrote the last time it ran
        private static bool _cached;
        private static Color _stockFogColor;
        private static float _stockFogDensity;
        private static Color _stockSkyFog;
        private static float _stockSkyOcclusion;
        private static Material _stockSkybox;

        private static bool _overriding;

        // ambient light lift (W2): the stock ambient intensity the last stock body wrote, and the eased lift weight 0..1
        private static float _stockAmbient = 1f;
        private static float _lift;
        private static bool _liftOn;
        private static Color _stockAmbSky, _stockAmbEq, _stockAmbGround;
        private static bool _ambWritten, _ambModeLogged;
        private static FogMode _fogModeSeen = (FogMode)(-1);

        // the sun state this class last set (for the hysteresis)
        private static bool _sunKnown;
        private static bool _sunOn;
        private static float _sunChanged;

        internal static void Apply(Harmony harmony)
        {
            try
            {
                _lightingLastUpdated = AccessTools.FieldRefAccess<LevelInfo, float>("lightingLastUpdated");
                _cloudLayer = AccessTools.FieldRefAccess<LevelInfo, CloudLayer>("cloudLayer");
                _skyAnimData = AccessTools.FieldRefAccess<LevelInfo, LevelInfo.SkyAnimData>("skyAnimData");
                _underwater = AccessTools.FieldRefAccess<CameraStateManager, bool>("underwater");
                harmony.Patch(AccessTools.Method(typeof(LevelInfo), "UpdateTimeOfDayLighting", new[] { typeof(bool) }),
                    postfix: new HarmonyMethod(typeof(Atmosphere), nameof(Postfix)));
                _ok = true;
            }
            catch (Exception e)
            {
                Plugin.ErrorOnce("atmo-patch", "patching LevelInfo.UpdateTimeOfDayLighting (fog and light)", e);
            }
        }

        /// <summary>Camera under the sea or a lake, as CameraStateManager sees it.</summary>
        internal static bool Underwater(CameraStateManager csm)
        {
            try
            {
                return csm != null && _underwater != null && _underwater(csm);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void Postfix(LevelInfo __instance)
        {
            if (!_ok || _failed)
                return;
            try
            {
                Run(__instance);
            }
            catch (Exception e)
            {
                _failed = true;
                Plugin.ErrorOnce("atmo-run", "the cloud deck's fog and light", e);
                try { Restore(); } catch (Exception) { }
            }
        }

        private static void Run(LevelInfo li)
        {
            if (GameManager.IsHeadless)
                return;
            float stamp = _lightingLastUpdated(li);
            if (stamp != _lastStamp)
            {
                // the stock body ran in this call (it sets lightingLastUpdated, LevelInfo.cs:442): remember its result
                _lastStamp = stamp;
                Material stockSky = RenderSettings.skybox;
                _stockFogColor = RenderSettings.fogColor;
                _stockFogDensity = RenderSettings.fogDensity;
                _stockAmbient = RenderSettings.ambientIntensity;
                _stockAmbSky = RenderSettings.ambientSkyColor;
                _stockAmbEq = RenderSettings.ambientEquatorColor;
                _stockAmbGround = RenderSettings.ambientGroundColor;
                _stockSkybox = stockSky;
                if (stockSky != null)
                {
                    _stockSkyFog = stockSky.HasProperty(IdFogColor) ? stockSky.GetColor(IdFogColor) : _stockFogColor;
                    _stockSkyOcclusion = stockSky.HasProperty(IdCloudOcclusion) ? stockSky.GetFloat(IdCloudOcclusion) : 0f;
                }
                _cached = true;
            }
            if (!Plugin.Active || Bench.StockFog) // the benchmark's stock-fog phase: as if the mod were off, fog only
            {
                if (_overriding)
                    Restore();
                return;
            }
            if (!_cached)
                return;
            CameraStateManager csm = SceneSingleton<CameraStateManager>.i;
            Camera cam = GameManager.gameState == GameState.Encyclopedia ? Camera.main : csm != null ? csm.mainCamera : null;
            if (cam == null || Underwater(csm))
            {
                ReleaseLift();
                return;
            }
            float alt = cam.transform.position.GlobalY();
            if (alt < 0f)
            {
                ReleaseLift();
                return; // LevelInfo.cs:439-440: the stock method leaves the sky alone below sea level
            }

            // Only when the cloud replacement is really running are there clouds between B and T. Otherwise (another
            // mod owns CloudLayer.Update, or the replacement failed) the only cloud is the stock sheet at B, so there
            // is no in-cloud band: rain haze under B, the stock clear-air haze above it, and the stock sun rule.
            bool deck = Deck.Usable;
            if (!deck)
                Plugin.WarnOnce("atmo-nodeck", "the cloud deck itself is not running; the fog follows the stock cloud sheet (rain haze under it only)");
            GlobalPosition g = cam.transform.position.ToGlobalPosition();
            float t = deck ? Plugin.TopFor(Plugin.BaseNow) : li.cloudHeight;
            float b = deck ? Plugin.LocalBaseAt(g.x, g.z) : li.cloudHeight; // the cloud base here
            float rain = Plugin.RainIntensityAt(g.x, g.z);
            float fade = Plugin.FadeFactor;
            float wet = Plugin.WetnessOnly; // the fade is applied below
            float cover = deck ? CloudMap.CoverFactor(CloudMap.Cover(g.x, g.z)) : 1f;
            float clearBonus = deck ? CloudMap.HazeBonus(g.x, g.z) : 0f;

            float haze = Haze(li, alt);
            // the rain haze thins with the wetness (rain already does), and the lee sees up to twice as far
            float visibility = Mathf.Min(VisibilityBelow(rain) * (1f + clearBonus), 60f);
            float k = FogK();
            bool exp2 = RenderSettings.fogMode == FogMode.ExponentialSquared;
            float below = AddDensity(haze, k / (visibility * 1000f) * Mathf.Lerp(0.35f, 1f, Mathf.Clamp01(wet)), exp2);
            float cloud = k / (Mathf.Max(0.05f, Plugin.InCloudVisibility.Value) * 1000f);
            // patchy or thin cloud: the in-cloud fog is only as dense as the cover and the wetness make it
            float cf = cover * fade * Mathf.Lerp(0.5f, 1f, Mathf.Clamp01(wet));
            cloud = Mathf.Exp(Mathf.Lerp(Mathf.Log(below), Mathf.Log(cloud), cf));
            float wb = Smooth((alt - (b - Blend)) / (2f * Blend)); // 0 under the base, 1 above it
            float wt = Smooth((alt - (t - Blend)) / (2f * Blend)); // 0 under the top, 1 above it
            float inside;
            float lnDensity;
            if (deck)
            {
                inside = wb * (1f - wt) * cf;
                lnDensity = Mathf.Lerp(Mathf.Lerp(Mathf.Log(below), Mathf.Log(cloud), wb), Mathf.Log(haze), wt);
            }
            else
            {
                inside = 0f;
                lnDensity = Mathf.Lerp(Mathf.Log(below), Mathf.Log(haze), wb);
            }
            RenderSettings.fogDensity = Mathf.Lerp(_stockFogDensity, Mathf.Exp(lnDensity), fade);

            Color grey = CloudGrey(_stockFogColor, Mathf.InverseLerp(b, t, alt));
            RenderSettings.fogColor = Color.Lerp(_stockFogColor, grey, inside);
            Material sky = RenderSettings.skybox;
            if (sky != null && sky == _stockSkybox)
            {
                if (sky.HasProperty(IdFogColor))
                    sky.SetColor(IdFogColor, Color.Lerp(_stockSkyFog, grey, inside));
                if (sky.HasProperty(IdCloudOcclusion))
                {
                    float occ = Mathf.Lerp(_stockSkyOcclusion, 1f, inside);
                    // under a patchy or lifted lee deck the stock grey-out (keyed to the raw plane) is relaxed
                    occ = Mathf.Lerp(occ, 0f, (1f - cover) * (1f - wb) * fade);
                    sky.SetFloat(IdCloudOcclusion, occ);
                }
            }
            SetSun(li, cam, inside, false, deck ? b : float.NaN, cover);
            LiftAmbient(li, cam, inside, deck ? wt : 0f, deck ? b : float.NaN, cover, Mathf.Lerp(0.5f, 1f, Mathf.Clamp01(wet)) * fade);
            _overriding = true;
        }

        /// <summary>The fog constant for the fog mode the scene really uses: 3.912 for Exp, sqrt(3.912) for Exp2. The mode
        /// is logged once, and again if it ever changes. Linear fog ignores the density, so it is treated as Exp (and
        /// warned about).</summary>
        private static float FogK()
        {
            FogMode mode = RenderSettings.fogMode;
            if (mode != _fogModeSeen)
            {
                _fogModeSeen = mode;
                Plugin.Log?.LogInfo("fog mode is " + mode + (mode == FogMode.ExponentialSquared
                    ? ": densities use sqrt(3.912) / visibility (Exp2), so the visibilities in the config are met as written"
                    : mode == FogMode.Exponential ? ": densities use 3.912 / visibility (Exp)"
                    : ": NOT an exponential mode, the fog density is ignored by the renderer and the visibilities will not match"));
            }
            return mode == FogMode.ExponentialSquared ? KoschmiederExp2 : Koschmieder;
        }

        internal static string FogModeText()
        {
            try { return RenderSettings.fogMode.ToString(); } catch (Exception) { return "?"; }
        }

        /// <summary>Two fog densities in one: they add under Exp, and their squares add under Exp2 (the exponent is (d r)^2).</summary>
        private static float AddDensity(float a, float b, bool exp2)
        {
            return exp2 ? Mathf.Sqrt(a * a + b * b) : a + b;
        }

        /// <summary>
        /// Ambient light under the deck (W2). The stock rule turns the sun off under the cloud and leaves ambient only,
        /// which in the 11-08 photos left the ground at about 20 % of its clear-noon brightness. This multiplies
        /// RenderSettings.ambientIntensity (the stock body writes it, LevelInfo.cs:465, once a second; this runs every
        /// frame on the cached stock value) by up to AmbientLift. The weight is the shade (how far the sun is blocked: the
        /// stock num3 rule, or the in-cloud share, whichever is larger), zero above the deck top and at night, times the
        /// wetness, fade-out and local cover, eased over LiftSeconds. The sun's colour and transform are not touched.
        /// </summary>
        private static void LiftAmbient(LevelInfo li, Camera cam, float inside, float aboveTop, float localBase, float coverFactor, float wetFade)
        {
            float lift = Mathf.Max(1f, Plugin.AmbientLift.Value);
            Light sun = li.sun;
            bool day = sun != null && sun.color.r > 0f;
            float shade = Mathf.Max(inside, StockNum3(li, cam, localBase, coverFactor));
            float target = day ? Mathf.Clamp01(shade) * (1f - aboveTop) * wetFade : 0f;
            if (lift <= 1f)
                target = 0f;
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.25f);
            _lift = Mathf.MoveTowards(_lift, target, dt / LiftSeconds);
            if (_lift > 0f || _ambWritten)
                WriteAmbient(Mathf.Lerp(1f, lift, Smooth(_lift)));
            if (!_liftOn && _lift > 0.5f)
            {
                _liftOn = true;
                Plugin.Log?.LogInfo("ambient lift engaged: x" + lift.ToString("0.##") + " on the stock ambient intensity " + _stockAmbient.ToString("0.###") +
                                    " (camera " + cam.transform.position.GlobalY().ToString("0") + " m, local base " + (float.IsNaN(localBase) ? "stock" : localBase.ToString("0") + " m") + ")");
            }
            else if (_liftOn && _lift < 0.1f)
            {
                _liftOn = false;
                Plugin.Log?.LogInfo("ambient lift released");
            }
        }

        /// <summary>
        /// Scale the ambient by k. In Skybox ambient mode the intensity multiplier does it; in Trilight or Flat mode the
        /// intensity is ignored, so the cached stock sky/equator/ground colours are scaled instead (re-cached whenever the
        /// stock body runs).
        /// </summary>
        private static void WriteAmbient(float k)
        {
            var mode = RenderSettings.ambientMode;
            if (!_ambModeLogged)
            {
                _ambModeLogged = true;
                Plugin.Log?.LogInfo("ambient mode is " + mode + (mode == UnityEngine.Rendering.AmbientMode.Skybox ? ": lifting through ambientIntensity" : ": lifting by scaling the sky, equator and ground ambient colours"));
            }
            if (mode == UnityEngine.Rendering.AmbientMode.Skybox)
            {
                RenderSettings.ambientIntensity = _stockAmbient * k;
            }
            else
            {
                RenderSettings.ambientIntensity = _stockAmbient;
                RenderSettings.ambientSkyColor = Scale(_stockAmbSky, k);
                RenderSettings.ambientEquatorColor = Scale(_stockAmbEq, k);
                RenderSettings.ambientGroundColor = Scale(_stockAmbGround, k);
            }
            _ambWritten = k > 1.0001f;
        }

        private static Color Scale(Color c, float k) { return new Color(c.r * k, c.g * k, c.b * k, c.a); }

        private static void PutAmbientBack()
        {
            _lift = 0f;
            _liftOn = false;
            _ambWritten = false;
            if (!_cached)
                return;
            try
            {
                RenderSettings.ambientIntensity = _stockAmbient;
                RenderSettings.ambientSkyColor = _stockAmbSky;
                RenderSettings.ambientEquatorColor = _stockAmbEq;
                RenderSettings.ambientGroundColor = _stockAmbGround;
            }
            catch (Exception) { }
        }

        /// <summary>Camera under the sea or a lake: the stock body does not run there, so put the ambient back by hand.</summary>
        private static void ReleaseLift()
        {
            if (_lift <= 0f && !_ambWritten)
                return;
            PutAmbientBack();
        }

        /// <summary>Visibility under the base, km: BelowBaseVisibility at intensity 0.25 (light rain), less where wetter.</summary>
        internal static float VisibilityBelow(float intensity)
        {
            float v0 = Plugin.BelowBaseVisibility.Value;
            float v = v0 * Mathf.Pow(0.25f / Mathf.Max(intensity, 0.05f), 0.6f);
            return Mathf.Clamp(v, 0.3f * v0, 40f);
        }

        /// <summary>The stock clear-air haze term (LevelInfo.cs:444, 469 without the weather term).</summary>
        private static float Haze(LevelInfo li, float alt)
        {
            float density = 1.225f;
            float hazeAmount = 0.35f; // level1's serialized value, used if the field cannot be read
            try
            {
                density = GameAssets.i.airDensityAltitude.Evaluate(alt * 0.001f);
                LevelInfo.SkyAnimData data = _skyAnimData(li);
                if (data != null)
                    hazeAmount = data.hazeAmount;
            }
            catch (Exception e)
            {
                Plugin.ErrorOnce("atmo-haze", "reading the stock haze", e);
            }
            return Mathf.Max(density * 0.00014f * hazeAmount, 3e-5f);
        }

        /// <summary>The fog colour inside cloud: the stock colour's brightness, darker at the base, lighter at the top.</summary>
        private static Color CloudGrey(Color stock, float height01)
        {
            float lum = Mathf.Max(0.07f, 0.3f * stock.r + 0.59f * stock.g + 0.11f * stock.b);
            float v = lum * Mathf.Lerp(0.75f, 1.05f, height01);
            return new Color(v, v, v, stock.a);
        }

        /// <summary>The sun on or off. Off inside the deck; otherwise what the stock code would decide right now
        /// (LevelInfo.cs:452-454, 478-479). Only the GameObjects' active state changes. The stock body decides once a
        /// second; this runs every frame, so it has hysteresis (inside: off above 0.55, on below 0.45; stock num3:
        /// 0.92/0.88) and holds a new state for at least 0.5 s, so a camera hovering at a threshold does not flicker the
        /// directional light. <paramref name="immediate"/> skips the hold (restoring stock).</summary>
        private static void SetSun(LevelInfo li, Camera cam, float inside, bool immediate, float localBase = float.NaN, float coverFactor = 1f)
        {
            Light sun = li.sun;
            if (sun == null)
                return;
            bool current = _sunKnown ? _sunOn : sun.gameObject.activeSelf;
            bool stock = sun.color.r > 0f && StockNum3(li, cam, localBase, coverFactor) < (current ? 0.92f : 0.88f);
            bool allowed = inside < (current ? 0.55f : 0.45f);
            bool want = allowed && stock;
            float now = Time.unscaledTime;
            if (want != current && !immediate && _sunKnown && now - _sunChanged < 0.5f)
                want = current;
            if (want != current)
                _sunChanged = now;
            _sunOn = want;
            _sunKnown = !immediate;
            if (sun.gameObject.activeSelf != want)
                sun.gameObject.SetActive(want);
            if (li.sunObject != null && li.sunObject.activeSelf != want)
                li.sunObject.SetActive(want);
        }

        private static float StockNum3(LevelInfo li, Camera cam, float localBase, float coverFactor)
        {
            CloudLayer layer = _cloudLayer(li);
            if (li.conditions <= 0.6f || layer == null)
                return 0f;
            // the stock rule uses the raw plane; with the regional deck it uses the local base (MSL, as the camera's
            // global altitude) and is scaled by the cover here
            if (!float.IsNaN(localBase))
                return Mathf.Clamp01((localBase + 200f - cam.transform.position.GlobalY()) * 0.005f) * Mathf.Clamp01(coverFactor);
            return Mathf.Clamp01((layer.transform.position.y + 200f - cam.transform.position.y) * 0.005f);
        }

        private static float Smooth(float x)
        {
            x = Mathf.Clamp01(x);
            return x * x * (3f - 2f * x);
        }

        /// <summary>Put back what the stock body last wrote and the stock sun state.</summary>
        private static void Restore()
        {
            _overriding = false;
            PutAmbientBack();
            if (!_cached)
                return;
            CameraStateManager csm = SceneSingleton<CameraStateManager>.i;
            LevelInfo li = NetworkSceneSingleton<LevelInfo>.i;
            if (csm == null || li == null || Underwater(csm))
                return;
            Camera cam = csm.mainCamera;
            if (cam == null || cam.transform.position.GlobalY() < 0f)
                return;
            RenderSettings.fogDensity = _stockFogDensity;
            RenderSettings.fogColor = _stockFogColor;
            Material sky = RenderSettings.skybox;
            if (sky != null && sky == _stockSkybox)
            {
                if (sky.HasProperty(IdFogColor))
                    sky.SetColor(IdFogColor, _stockSkyFog);
                if (sky.HasProperty(IdCloudOcclusion))
                    sky.SetFloat(IdCloudOcclusion, _stockSkyOcclusion);
            }
            SetSun(li, cam, 0f, true);
        }

        internal static void StandDown()
        {
            if (_overriding)
                Restore();
            _sunKnown = false;
        }

        internal static void ResetFailure()
        {
            _failed = false;
            _sunKnown = false;
        }
    }
}
