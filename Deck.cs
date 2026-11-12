using System;
using HarmonyLib;
using UnityEngine;

namespace CloudDeck
{
    /// <summary>
    /// The cloud deck: clouds fill base B (= LevelInfo.cloudHeight) to top T (Plugin.TopFor) instead of one sheet at B.
    ///
    /// Stock (CloudLayer.cs, game 0.34.6): Update (207-294) places the layer at LocalSeaY + cloudHeight (222), emits
    /// ~4 mesh-cloud puffs a frame within 7 km of the camera (229-250), all at y = cloudHeight for Overcast/Thunderstorm
    /// because their particle sampler is 1.0 everywhere (240), and runs a fly-through particle system when a probe 30 m
    /// ahead of the camera is within about -100..+133 m of the layer (251-293). UpdateWeatherSets (156-205, every 1 s)
    /// sizes the distant cloud ring (188-189).
    ///
    /// Here, while the plugin is active, a prefix replaces Update with the same steps except that:
    ///  - the puffs are spread over the deck: some with their bottoms at the base (a ragged base up to 40 m below B),
    ///    some with their tops at the top (bulging up to 150 m above T), some in between, the mix chosen by the camera's
    ///    side (Bands); sizes, lifetimes, the random-walk placement and the rate are the stock ones, with sizes capped
    ///    so no puff is taller than the deck, and the particle cap is the stock one x CloudParticleBudget,
    ///  - a copy of the flat cloud plane (same mesh, material and scale) is drawn at T, the stock plane stays at B,
    ///  - the fly-through system runs anywhere inside B..T (fading 40 m below B and 60 m above T), at stock strength
    ///    near the base and the top and reduced deep inside (FlyThroughInterior),
    ///  - the wind drift of the puffs uses windVelocity.z for the northward part (stock uses .y, which is 0, :242),
    ///  - the deck is regional (Clouds.RegionalDeck, CloudMap.cs): a puff is kept only where a spatially coherent noise
    ///    is under the local cover of the cloud map and sits on the LOCAL base (low on the windward slopes, high in the
    ///    lee), the fly-through wisps scale with the cover, the stock plane at the base is hidden where the local base
    ///    is more than 250 m above the mission's (the stock sheet would hang under the lifted deck), and the distant
    ///    ring is thinned and lifted by the map's mean over the ring,
    ///  - the deck follows the weather: puff density and the top come down with the wetness (Plugin.Wetness) and
    ///    everything fades out over about 10 s when the weather goes lighter than Moderate (Plugin.FadeFactor);
    ///    the mission's cloud height is followed at 30 m/s (Plugin.BaseNow) so a ModifyEnvironment step does not pop.
    /// A postfix on UpdateWeatherSets gives the distant ring the flat puff meshes and sizes/heights that keep it in B..T.
    /// The layer transform stays at LocalSeaY + cloudHeight, which LevelInfo reads (LevelInfo.cs:454, 541).
    /// Particle positions are in the systems' custom simulation space, Datum.origin (CloudLayer.cs:103-112), whose local
    /// coordinates are global coordinates (GlobalPositionExtensions.ToGlobalPosition: world - Datum.originPosition).
    /// </summary>
    internal static class Deck
    {
        // Vertical extent of the stock puff meshes, in units of particle size (mesh bounds read from sharedassets1:
        // meshCloud1 centre 0.028 extent 0.348, meshCloud2 centre 0.054 extent 0.510). The worst case (meshCloud2)
        // is used so a puff never leaves the deck.
        private const float MeshBelow = 0.456f;
        private const float MeshAbove = 0.564f;
        private const float MeshSpan = MeshBelow + MeshAbove;
        private const float BaseRag = 40f;
        private const float TopBulge = 150f;

        private static AccessTools.FieldRef<LevelInfo, CloudLayer> _levelCloudLayer;
        private static AccessTools.FieldRef<CloudLayer, WeatherSet[]> _weatherSets;
        private static AccessTools.FieldRef<CloudLayer, WeatherSet> _currentWeatherSet;
        private static AccessTools.FieldRef<CloudLayer, ParticleSystem> _cloudSystem;
        private static AccessTools.FieldRef<CloudLayer, ParticleSystem> _distantCloudSystem;
        private static AccessTools.FieldRef<CloudLayer, ParticleSystem> _flyThroughSystem;
        private static AccessTools.FieldRef<CloudLayer, MeshRenderer> _cloudRenderer;
        private static AccessTools.FieldRef<CloudLayer, Material> _layerMaterial;
        private static AccessTools.FieldRef<CloudLayer, float> _densityMapScale;
        private static AccessTools.FieldRef<CloudLayer, float> _layerHeight;
        private static AccessTools.FieldRef<CloudLayer, float> _cloudSizeMin;
        private static AccessTools.FieldRef<CloudLayer, float> _cloudSizeMax;
        private static AccessTools.FieldRef<CloudLayer, float> _cloudSizeVariation;
        private static AccessTools.FieldRef<CloudLayer, float> _cloudLifeMin;
        private static AccessTools.FieldRef<CloudLayer, float> _cloudLifeMax;
        private static AccessTools.FieldRef<CloudLayer, float> _cloudDrawDist;
        private static AccessTools.FieldRef<CloudLayer, int> _generationRate;
        private static AccessTools.FieldRef<CloudLayer, float> _cloudDetail;
        private static AccessTools.FieldRef<CloudLayer, float> _sizeFactor;
        private static AccessTools.FieldRef<CloudLayer, Vector2> _windDisplacement;
        private static AccessTools.FieldRef<CloudLayer, Vector3> _camVelPrev;
        private static AccessTools.FieldRef<CloudLayer, Lightning> _lightning;
        private static AccessTools.FieldRef<CloudLayer, int> _maxParticles;

        private static readonly int IdCloudsOriginOffset = Shader.PropertyToID("_CloudsOriginOffset");

        private static bool _fieldsOk;
        private static bool _updatePatched;
        private static bool _failed;
        private static bool _foreign;
        private static ParticleSystem.EmitParams _emit;

        // plane culling: hysteresis states, and the stock plane renderer this class switched off (null if none)
        private const float CullHysteresis = 25f; // +-25 m around the margin: 50 m between hiding and showing again
        private static bool _setsLogged;
        private static bool _topCulled;
        private static bool _topPatchHidden; // the top plane is hidden under a patchy (low-cover) deck
        private static bool _baseCulled;
        private static bool _baseLeeHidden; // the stock plane is hidden because the local base is far above the mission's
        private const float LeeHideMargin = 250f;     // hard-hide fallback (no usable alpha property): hide above +250 +60 m
        private const float LeeFadeStart = 60f;        // the stock plane starts to fade this far over the mission base...
        private const float LeeFadeEnd = 310f;         // ...and is gone (hidden) this far over it: 250 m of fade, no pop
        private static float _leeAlpha = 1f;           // 1 = the stock plane at full strength
        private static float _leeApplied = 1f;         // the alpha last written to the plane's property block
        private static int _leeKind = -1;              // -1 not probed, 0 no usable property, 1 float, 2 colour
        private static int _leeProp;
        private static string _leePropName;
        private static float _leeBaseFloat = 1f;
        private static Color _leeBaseColor = Color.white;
        private static MaterialPropertyBlock _leeBlock;
        private static MeshRenderer _leeRenderer;      // the renderer a property block was set on
        private static int _leeLog;                    // 0 off, 1 fading, 2 hidden (for the log lines)
        private static readonly string[] LeeFloatNames = { "_Opacity", "_Alpha", "_CloudOpacity", "_OpacityMultiplier", "_Fade" };
        private static readonly string[] LeeColorNames = { "_BaseColor", "_Color" };
        private static MeshRenderer _baseOff;

        // the distant ring's emission rate before this class thinned it (NaN = not read yet)
        private static float _ringRate = float.NaN;

        // puff budget: the layer whose cloudSystem.maxParticles this class changed (null if none)
        private static CloudLayer _budgetLayer;
        private static ParticleSystem.Particle[] _trim;
        private const int FillExtra = 16; // extra puffs a frame while the benchmark refills to a higher budget

        private static GameObject _topPlane;
        private static MeshRenderer _topRenderer;
        private static CloudLayer _distantOwner;
        private static Mesh[] _distantMeshes;
        private static ParticleSystemRenderer _distantRenderer; // the renderer whose meshes are swapped now
        private static Vector3 _distantLocalPos;

        /// <summary>The cloud replacement can run (fields found, Update patched, no failure, no other mod on Update).</summary>
        internal static bool Usable => _fieldsOk && _updatePatched && !_failed && !_foreign;

        internal static void Apply(Harmony harmony)
        {
            try
            {
                _levelCloudLayer = AccessTools.FieldRefAccess<LevelInfo, CloudLayer>("cloudLayer");
                _weatherSets = AccessTools.FieldRefAccess<CloudLayer, WeatherSet[]>("weatherSets");
                _currentWeatherSet = AccessTools.FieldRefAccess<CloudLayer, WeatherSet>("currentWeatherSet");
                _cloudSystem = AccessTools.FieldRefAccess<CloudLayer, ParticleSystem>("cloudSystem");
                _distantCloudSystem = AccessTools.FieldRefAccess<CloudLayer, ParticleSystem>("distantCloudSystem");
                _flyThroughSystem = AccessTools.FieldRefAccess<CloudLayer, ParticleSystem>("flyThroughSystem");
                _cloudRenderer = AccessTools.FieldRefAccess<CloudLayer, MeshRenderer>("cloudRenderer");
                _layerMaterial = AccessTools.FieldRefAccess<CloudLayer, Material>("layerMaterial");
                _densityMapScale = AccessTools.FieldRefAccess<CloudLayer, float>("densityMapScale");
                _layerHeight = AccessTools.FieldRefAccess<CloudLayer, float>("layerHeight");
                _cloudSizeMin = AccessTools.FieldRefAccess<CloudLayer, float>("cloudSizeMin");
                _cloudSizeMax = AccessTools.FieldRefAccess<CloudLayer, float>("cloudSizeMax");
                _cloudSizeVariation = AccessTools.FieldRefAccess<CloudLayer, float>("cloudSizeVariation");
                _cloudLifeMin = AccessTools.FieldRefAccess<CloudLayer, float>("cloudLifeMin");
                _cloudLifeMax = AccessTools.FieldRefAccess<CloudLayer, float>("cloudLifeMax");
                _cloudDrawDist = AccessTools.FieldRefAccess<CloudLayer, float>("cloudDrawDist");
                _generationRate = AccessTools.FieldRefAccess<CloudLayer, int>("generationRate");
                _cloudDetail = AccessTools.FieldRefAccess<CloudLayer, float>("cloudDetail");
                _sizeFactor = AccessTools.FieldRefAccess<CloudLayer, float>("sizeFactor");
                _windDisplacement = AccessTools.FieldRefAccess<CloudLayer, Vector2>("windDisplacement");
                _camVelPrev = AccessTools.FieldRefAccess<CloudLayer, Vector3>("camVelPrev");
                _lightning = AccessTools.FieldRefAccess<CloudLayer, Lightning>("lightning");
                _maxParticles = AccessTools.FieldRefAccess<CloudLayer, int>("maxParticles");
                _fieldsOk = true;
            }
            catch (Exception e)
            {
                Plugin.ErrorOnce("deck-fields", "finding the cloud layer's fields (the game changed?)", e);
                return;
            }
            try
            {
                harmony.Patch(AccessTools.Method(typeof(CloudLayer), "Update"),
                    prefix: new HarmonyMethod(typeof(Deck), nameof(UpdatePrefix)));
                _updatePatched = true;
            }
            catch (Exception e)
            {
                Plugin.ErrorOnce("deck-patch", "patching CloudLayer.Update", e);
            }
            try
            {
                harmony.Patch(AccessTools.Method(typeof(CloudLayer), "UpdateWeatherSets"),
                    postfix: new HarmonyMethod(typeof(Deck), nameof(AfterUpdateWeatherSets)));
            }
            catch (Exception e)
            {
                Plugin.ErrorOnce("deck-patch2", "patching CloudLayer.UpdateWeatherSets", e);
            }
        }

        /// <summary>Owner ids that never count as a foreign weather mod. NOTelemetry (our own recorder) only times
        /// CloudLayer.Update with void prefixes, which cannot skip the original or change what it does.</summary>
        private const string OwnerNoTelemetry = "com.jack.notelemetry";

        private static string _foreignLogged;

        /// <summary>Another mod with a patch on CloudLayer.Update that can change its behaviour: leave the clouds to it.
        /// Only two kinds count: a prefix whose patch method returns bool (it can skip the original) and a transpiler.
        /// Void prefixes, postfixes and finalizers do not (Harmony runs postfixes and finalizers even when a prefix
        /// skips the original, and a void prefix cannot skip it). Patches owned by this plugin and by
        /// com.jack.notelemetry are always ignored. The owners found and the owners ignored are logged once (again only
        /// if the picture changes). Called at activation and every 30 s while active (Plugin.Evaluate).</summary>
        internal static void CheckForeignPatches()
        {
            try
            {
                bool was = _foreign;
                _foreign = false;
                var info = Harmony.GetPatchInfo(AccessTools.Method(typeof(CloudLayer), "Update"));
                if (info == null)
                    return;
                var counted = new System.Collections.Generic.SortedSet<string>(StringComparer.Ordinal);
                var ignored = new System.Collections.Generic.SortedSet<string>(StringComparer.Ordinal);
                foreach (var p in info.Prefixes)
                {
                    string kind = "prefix";
                    bool canSkip;
                    if (p.PatchMethod == null)
                        canSkip = true; // cannot tell: treat it as able to skip
                    else if (p.PatchMethod.ReturnType == typeof(bool))
                        canSkip = true;
                    else
                    {
                        canSkip = false;
                        kind = "void prefix";
                    }
                    Classify(p.owner, kind, canSkip, counted, ignored);
                }
                foreach (var p in info.Transpilers)
                    Classify(p.owner, "transpiler", true, counted, ignored);
                foreach (var p in info.ILManipulators)
                    Classify(p.owner, "ilmanipulator", true, counted, ignored);
                foreach (var p in info.Postfixes)
                    Classify(p.owner, "postfix", false, counted, ignored);
                foreach (var p in info.Finalizers)
                    Classify(p.owner, "finalizer", false, counted, ignored);
                _foreign = counted.Count > 0;

                string picture = "counted: " + (counted.Count == 0 ? "none" : string.Join(", ", counted)) +
                                 "; ignored: " + (ignored.Count == 0 ? "none" : string.Join(", ", ignored));
                if (picture != _foreignLogged)
                {
                    _foreignLogged = picture;
                    Plugin.Log?.LogInfo("patches on CloudLayer.Update from other owners (" + picture +
                                        "); only a prefix returning bool or a transpiler from another owner makes the deck stand down, never " +
                                        OwnerNoTelemetry);
                }
                if (_foreign)
                {
                    Plugin.WarnOnce("deck-foreign", "another mod patches CloudLayer.Update in a way that can change it (" +
                                    string.Join(", ", counted) + "); the clouds are left to it (haze under the stock sheet, rain and lightning still run)");
                    if (!was)
                        StandDownClouds();
                }
            }
            catch (Exception e)
            {
                Plugin.ErrorOnce("deck-patchinfo", "reading Harmony patch info", e);
            }
        }

        private static void Classify(string owner, string kind, bool canChange,
            System.Collections.Generic.SortedSet<string> counted, System.Collections.Generic.SortedSet<string> ignored)
        {
            string id = string.IsNullOrEmpty(owner) ? "(no owner)" : owner;
            if (owner == Plugin.Id || owner == OwnerNoTelemetry)
                ignored.Add(id + " (" + kind + ", always ignored)");
            else if (canChange)
                counted.Add(id + " (" + kind + ")");
            else
                ignored.Add(id + " (" + kind + ", cannot change the original)");
        }


        internal static CloudLayer LayerOf(LevelInfo li)
        {
            if (li == null || _levelCloudLayer == null)
                return null;
            return _levelCloudLayer(li);
        }

        internal static Lightning LightningOf(CloudLayer layer)
        {
            return layer != null && _lightning != null ? _lightning(layer) : null;
        }

        internal static ParticleSystem CloudSystemOf(CloudLayer layer)
        {
            return layer != null && _fieldsOk ? _cloudSystem(layer) : null;
        }

        internal static ParticleSystem DistantOf(CloudLayer layer)
        {
            return layer != null && _fieldsOk ? _distantCloudSystem(layer) : null;
        }

        internal static ParticleSystem FlyThroughOf(CloudLayer layer)
        {
            return layer != null && _fieldsOk ? _flyThroughSystem(layer) : null;
        }

        /// <summary>The stock plane (at the base) is drawn: renderer enabled and its GameObject active.</summary>
        internal static bool BasePlaneOn(CloudLayer layer)
        {
            MeshRenderer r = layer != null && _fieldsOk ? _cloudRenderer(layer) : null;
            return r != null && r.enabled && r.gameObject.activeInHierarchy;
        }

        /// <summary>The deck's top plane exists and is drawn.</summary>
        internal static bool TopPlaneOn => _topPlane != null && _topRenderer != null && _topRenderer.enabled && _topPlane.activeInHierarchy;

        /// <summary>The WeatherSet index the stock code picks (CloudLayer.cs:158), or -1.</summary>
        internal static int WeatherIndex(CloudLayer layer, LevelInfo li)
        {
            if (_weatherSets == null || layer == null || li == null)
                return -1;
            WeatherSet[] sets = _weatherSets(layer);
            if (sets == null || sets.Length == 0)
                return -1;
            return Mathf.Clamp(Mathf.FloorToInt(li.conditions * sets.Length), 0, sets.Length - 1);
        }

        /// <summary>Puff budget in use: the benchmark's override, else CloudParticleBudget.</summary>
        internal static float EffectiveBudget =>
            Bench.Budget > 0f ? Bench.Budget : Mathf.Clamp(Plugin.CloudParticleBudget.Value, 0.05f, 1f);

        /// <summary>The cap the stock code gives cloudSystem: (int)(maxParticles * sqrt(cloudDetail)) (CloudLayer.cs:203),
        /// or -1 before the first UpdateWeatherSets (cloudDetail is -1 until then, :84).</summary>
        private static int StockCap(CloudLayer layer)
        {
            float detail = _cloudDetail(layer);
            if (detail <= 0f)
                return -1;
            return (int)((float)_maxParticles(layer) * Mathf.Sqrt(detail));
        }

        /// <summary>The WeatherSet the stock code picks (CloudLayer.cs:158) is the middle one (index 2 of 5, Moderate) or
        /// wetter. Below that the deck fades out and the stock clouds take over; between index 2 and the wet sets the
        /// wetness (Plugin.WetnessOf) thins the rain, haze and puffs.</summary>
        internal static bool IsDeckWeather(CloudLayer layer, LevelInfo li, out string name)
        {
            name = "unknown";
            if (_weatherSets == null)
                return false;
            WeatherSet[] sets = _weatherSets(layer);
            if (sets == null || sets.Length == 0)
                return false;
            int index = Mathf.Clamp(Mathf.FloorToInt(li.conditions * sets.Length), 0, sets.Length - 1);
            WeatherSet set = sets[index];
            if (!_setsLogged)
            {
                _setsLogged = true;
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < sets.Length; i++)
                    sb.Append(i).Append(':').Append(sets[i] != null ? sets[i].coverage.ToString("0.00") : "null").Append(' ');
                Plugin.Log.LogInfo("weather set coverages " + sb);
            }
            name = set != null && !string.IsNullOrEmpty(set.displayName) ? set.displayName : "set " + index;
            return set != null && index >= 2 && set.coverage > 0.1f;
        }

        // ------------------------------------------------------------------------------------------- CloudLayer.Update

        private static bool UpdatePrefix(CloudLayer __instance)
        {
            if (!Plugin.Active || !Usable)
                return true;
            try
            {
                return !DeckUpdate(__instance);
            }
            catch (Exception e)
            {
                _failed = true;
                Plugin.ErrorOnce("deck-update", "the cloud deck (CloudLayer.Update replacement)", e);
                StandDownClouds();
                return true;
            }
        }

        /// <summary>The replacement for CloudLayer.Update. Returns false to let the stock method run instead.</summary>
        private static bool DeckUpdate(CloudLayer layer)
        {
            CameraStateManager csm = SceneSingleton<CameraStateManager>.i;
            LevelInfo li = NetworkSceneSingleton<LevelInfo>.i;
            if (GameManager.IsHeadless || csm == null)
                return true; // CloudLayer.cs:209-212
            WeatherSet ws = _currentWeatherSet(layer);
            // Not ready yet (no level, no weather set, no origin). The stock body would throw a NullReferenceException
            // every frame here (CloudLayer.cs:214-215, 228), so it is skipped too and the next frame tries again.
            if (li == null || ws == null || ws.cookies == null || ws.cookies.Length == 0 || ws.cookies[0] == null ||
                Datum.origin == null)
                return true;
            Transform cam = csm.transform;
            float bRaw = li.cloudHeight;                 // the stock plane and the layer transform follow this at once
            float b = Plugin.BaseNow;                    // the deck follows it eased, so a mission step does not pop
            float t = Plugin.TopFor(b);
            _layerHeight(layer) = bRaw;
            GlobalPosition camG = cam.GlobalPosition();
            float baseHere = Plugin.LocalBaseAt(camG.x, camG.z);

            // cloud shadow cookie and pattern drift, as stock (CloudLayer.cs:215-221)
            Texture2D[] cookies = ws.cookies;
            int index = Mathf.Clamp(Mathf.FloorToInt((cam.position.y - (layer.transform.position.y + 200f)) * cookies.Length / 200f), 0, cookies.Length - 1);
            Texture2D cookie0 = cookies[0];
            ref Vector2 wind2 = ref _windDisplacement(layer);
            li.SetCookie(cookies[index], cookie0.width * _densityMapScale(layer) * 0.75f, wind2);
            wind2 += new Vector2(li.windVelocity.x, li.windVelocity.z) * Time.deltaTime;
            Material layerMaterial = _layerMaterial(layer);
            if (layerMaterial != null)
                layerMaterial.SetVector(IdCloudsOriginOffset, new Vector2(Datum.originPosition.x + wind2.x, Datum.originPosition.z + wind2.y));

            // the layer stays where LevelInfo expects it: the base (CloudLayer.cs:222)
            layer.transform.position = new Vector3(0f, Datum.LocalSeaY + bRaw, 0f);
            UpdateTopPlane(layer, layerMaterial, t);
            CullPlanes(layer, cam.position.y - Datum.LocalSeaY, b, t, bRaw, baseHere, CloudMap.Enabled ? CloudMap.Cover(camG.x, camG.z) : 1f);
            ApplyBudget(layer);
            if (ws.particleSampler == null)
                return true; // no density map: no puffs and no fly-through (stock would throw at CloudLayer.cs:234/270)
            EmitPuffs(layer, ws, csm, li, b, t, wind2, baseHere);
            FlyThrough(layer, ws, csm, b, t, wind2);
            return true;
        }

        internal static void ResetFailure()
        {
            _failed = false;
        }

        /// <summary>The cloud replacement stops mid-mission (another mod took CloudLayer.Update): drop the top plane
        /// and give the distant ring back its stock meshes. The stock Update takes over the near clouds by itself.</summary>
        private static void StandDownClouds()
        {
            try
            {
                DestroyTopPlane();
                RestoreBasePlane();
                ClearLee();
                _baseCulled = false;
                _baseLeeHidden = false;
                RestoreBudget();
                if (!ReferenceEquals(_distantOwner, null))
                    RestoreDistant(_distantOwner);
            }
            catch (Exception e)
            {
                Plugin.ErrorOnce("deck-standdown-clouds", "restoring the stock clouds", e);
            }
        }

        // ------------------------------------------------------------------------------------------- frame-rate knobs

        /// <summary>
        /// The far plane is hidden where it is behind the near one: the top plane while the camera is more than
        /// PlaneCullMargin under the base, the stock plane while it is more than PlaneCullMargin over the top. Each comes
        /// back 50 m closer (hysteresis), so a camera at the threshold does not flicker it. Only the renderers' enabled
        /// flags change; the stock plane's transform stays where LevelInfo reads it. The benchmark can also hide the top.
        /// The planes are blended layers, so the far one shows through gaps in the near one: switching it is a hard cut
        /// (no fade), and whether that pop is visible at the margin is not verified in game.
        /// </summary>
        private static void CullPlanes(CloudLayer layer, float camAlt, float b, float t, float bRaw, float baseHere, float cover)
        {
            // regional deck: where the local base is far above the mission's, the stock sheet at the mission base would
            // hang under the lifted (and patchy) deck. It is faded out with the height difference (alpha through a
            // property block on its renderer, if its shader has a usable property), then hidden at alpha 0. Without a
            // usable property it is a hard hide as in 1.1.x. The difference is taken against the eased base.
            MeshRenderer baseRenderer = _cloudRenderer(layer);
            if (CloudMap.Enabled)
            {
                ProbeLeeProperty(baseRenderer);
                float over = baseHere - b;
                _leeAlpha = 1f - Smooth01((over - LeeFadeStart) / (LeeFadeEnd - LeeFadeStart));
                if (_leeKind > 0)
                    _baseLeeHidden = _baseLeeHidden ? _leeAlpha < 0.06f : _leeAlpha < 0.02f;
                else
                    _baseLeeHidden = _baseLeeHidden ? over > LeeHideMargin - 60f : over > LeeHideMargin + 60f; // wider band: a mission step must not flip it
            }
            else
            {
                _baseLeeHidden = false;
                _leeAlpha = 1f;
            }
            ApplyLeeAlpha(baseRenderer, _baseLeeHidden ? 0f : _leeKind > 0 ? _leeAlpha : 1f, baseHere, b);
            // regional deck: the top plane is a full sheet, so under a patchy deck (cover below 0.85 here) it is hidden
            // while the camera is below the local base (hysteresis 0.05 on the cover)
            if (CloudMap.Enabled && camAlt < baseHere)
                _topPatchHidden = _topPatchHidden ? cover < 0.9f : cover < 0.85f;
            else
                _topPatchHidden = false;
            if (Plugin.PlaneCulling.Value)
            {
                float m = Mathf.Max(0f, Plugin.PlaneCullMargin.Value);
                float topEdge = b - m;
                float baseEdge = t + m;
                _topCulled = _topCulled ? camAlt < topEdge + CullHysteresis : camAlt < topEdge - CullHysteresis;
                _baseCulled = _baseCulled ? camAlt > baseEdge - CullHysteresis : camAlt > baseEdge + CullHysteresis;
            }
            else
            {
                _topCulled = false;
                _baseCulled = false;
            }
            bool topOn = !_topCulled && !_topPatchHidden && !Bench.HideTop && Plugin.FadeFactor >= 0.5f; // off at half fade: no pop at stand-down
            if (_topRenderer != null && _topRenderer.enabled != topOn)
                _topRenderer.enabled = topOn;
            if ((_baseCulled || _baseLeeHidden) && baseRenderer != null)
            {
                if (baseRenderer.enabled)
                {
                    baseRenderer.enabled = false;
                    _baseOff = baseRenderer;
                }
            }
            else
            {
                RestoreBasePlane();
            }
        }

        /// <summary>Find the stock plane shader's opacity property once (and list its properties in the log).</summary>
        private static void ProbeLeeProperty(MeshRenderer r)
        {
            if (_leeKind != -1 || r == null)
                return;
            _leeKind = 0;
            try
            {
                Material m = r.sharedMaterial;
                if (m == null || m.shader == null)
                    return;
                Shader sh = m.shader;
                var names = new System.Text.StringBuilder();
                for (int i = 0; i < sh.GetPropertyCount(); i++)
                    names.Append(i > 0 ? ", " : "").Append(sh.GetPropertyName(i)).Append(" (").Append(sh.GetPropertyType(i)).Append(')');
                Plugin.Log?.LogInfo("stock cloud plane shader " + sh.name + " properties: " + names + " (render queue " + m.renderQueue + ")");
                if (!Plugin.LeeFade.Value)
                {
                    Plugin.Log?.LogInfo("lee fade: Clouds/LeeFade is off; the stock cloud plane is hard-hidden where the local base is far over the mission base");
                    return;
                }
                foreach (string n in LeeFloatNames)
                {
                    if (m.HasProperty(n))
                    {
                        _leeKind = 1;
                        _leePropName = n;
                        _leeProp = Shader.PropertyToID(n);
                        _leeBaseFloat = m.GetFloat(_leeProp); // only for the log; ApplyLeeAlpha re-reads the live value
                        break;
                    }
                }
                if (_leeKind == 0 && m.renderQueue >= 3000)
                {
                    foreach (string n in LeeColorNames)
                    {
                        if (m.HasProperty(n))
                        {
                            _leeKind = 2;
                            _leePropName = n;
                            _leeProp = Shader.PropertyToID(n);
                            _leeBaseColor = m.GetColor(_leeProp);
                            break;
                        }
                    }
                }
                Plugin.Log?.LogInfo(_leeKind > 0
                    ? "lee fade: the stock cloud plane fades through " + _leePropName + " (unverified that this property is its opacity)"
                    : "lee fade: no known opacity property on the stock cloud plane; it is hard-hidden where the local base is more than " + (LeeHideMargin + 60f) + " m over the mission base");
            }
            catch (Exception e)
            {
                _leeKind = 0;
                Plugin.ErrorOnce("deck-lee-probe", "probing the stock cloud plane's properties", e);
            }
        }

        /// <summary>Write the stock plane's alpha (1 = untouched, 0 = hidden) and log the transitions.</summary>
        private static void ApplyLeeAlpha(MeshRenderer r, float alpha, float baseHere, float b)
        {
            int state = alpha >= 0.999f ? 0 : alpha <= 0f ? 2 : 1;
            if (_leeRenderer != null && r != _leeRenderer)
            {
                // a new renderer (new mission or scene): forget the old probe
                _leeKind = -1; _leeApplied = 1f; _leeRenderer = null; _leeLog = 0;
                return;
            }
            if (_leeKind > 0 && r != null && Mathf.Abs(alpha - _leeApplied) > 0.01f)
            {
                try
                {
                    Material live = r.sharedMaterial;
                    if (live != null && live.HasProperty(_leeProp))
                    {
                        if (_leeKind == 1) _leeBaseFloat = live.GetFloat(_leeProp);
                        else _leeBaseColor = live.GetColor(_leeProp);
                    }
                    if (_leeBlock == null)
                        _leeBlock = new MaterialPropertyBlock();
                    if (alpha >= 0.999f)
                    {
                        r.SetPropertyBlock(null);
                        _leeRenderer = null;
                    }
                    else
                    {
                        r.GetPropertyBlock(_leeBlock);
                        if (_leeKind == 1)
                            _leeBlock.SetFloat(_leeProp, _leeBaseFloat * alpha);
                        else
                            _leeBlock.SetColor(_leeProp, new Color(_leeBaseColor.r, _leeBaseColor.g, _leeBaseColor.b, _leeBaseColor.a * alpha));
                        r.SetPropertyBlock(_leeBlock);
                        _leeRenderer = r;
                    }
                    _leeApplied = alpha;
                }
                catch (Exception e)
                {
                    _leeKind = 0;
                    Plugin.ErrorOnce("deck-lee-apply", "fading the stock cloud plane", e);
                }
            }
            if (state != _leeLog)
            {
                string what = state == 0 ? "restored" : state == 2 ? "hidden" : "fading";
                Plugin.Log?.LogInfo("lee fade: stock cloud plane " + what + " (local base " + baseHere.ToString("0") + " m, mission base " + b.ToString("0") +
                                    " m, difference " + (baseHere - b).ToString("0") + " m, alpha " + alpha.ToString("0.00") + (_leeKind > 0 ? "" : ", hard hide only") + ")");
                _leeLog = state;
            }
        }

        /// <summary>Stand-down: drop the stock plane's alpha override.</summary>
        private static void ClearLee()
        {
            if (_leeRenderer != null)
            {
                try { _leeRenderer.SetPropertyBlock(null); } catch (Exception) { }
            }
            _leeRenderer = null;
            _leeApplied = 1f;
            _leeLog = 0;
            _leeAlpha = 1f;
            _leeKind = -1; // re-probe against whatever renderer comes next
        }

        /// <summary>Switch the stock plane back on if this class switched it off (only then).</summary>
        private static void RestoreBasePlane()
        {
            if (ReferenceEquals(_baseOff, null))
                return;
            if (_baseOff != null) // Unity null once the scene is gone
                _baseOff.enabled = true;
            _baseOff = null;
        }

        /// <summary>
        /// The deck's puff cap: EffectiveBudget x the stock cap. The stock code sets cloudSystem.maxParticles only when
        /// the Cloud Detail setting changes (CloudLayer.cs:199-204), so it is checked here each frame and set only when it
        /// differs. When the cap drops below the live count, the puffs beyond the cap are removed at once (through a buffer
        /// allocated on the first such drop), so a benchmark phase measures the new count, not a 15 s die-off.
        /// </summary>
        private static void ApplyBudget(CloudLayer layer)
        {
            ParticleSystem system = _cloudSystem(layer);
            int stock = StockCap(layer);
            if (system == null || stock <= 0)
                return;
            if (!ReferenceEquals(_budgetLayer, null) && _budgetLayer != layer)
                RestoreBudget();
            int want = Mathf.Max(1, Mathf.RoundToInt(stock * EffectiveBudget));
            var main = system.main;
            if (main.maxParticles == want)
                return;
            main.maxParticles = want;
            _budgetLayer = layer;
            int alive = system.particleCount;
            if (alive > want)
            {
                if (_trim == null || _trim.Length < alive)
                    _trim = new ParticleSystem.Particle[Mathf.Max(alive, stock, 4096)];
                int kept = system.GetParticles(_trim, want);
                system.SetParticles(_trim, kept);
            }
        }

        /// <summary>Give cloudSystem the stock cap back (stand-down, failure, another mod taking over).</summary>
        private static void RestoreBudget()
        {
            CloudLayer layer = _budgetLayer;
            _budgetLayer = null;
            if (ReferenceEquals(layer, null) || layer == null)
                return;
            ParticleSystem system = _cloudSystem(layer);
            int stock = StockCap(layer);
            if (system != null && stock > 0)
            {
                var main = system.main;
                main.maxParticles = stock;
            }
        }

        private static void EmitPuffs(CloudLayer layer, WeatherSet ws, CameraStateManager csm, LevelInfo li, float b, float t, Vector2 wind2, float baseHere)
        {
            float detail = _cloudDetail(layer);
            ParticleSystem system = _cloudSystem(layer);
            if (detail <= 0f || system == null)
                return; // cloudDetail is -1 until the first UpdateWeatherSets (CloudLayer.cs:84, 199-204)
            Transform cam = csm.transform;
            float topWorld = Datum.LocalSeaY + t;
            // spawn rate falls off with height above the deck, as stock does above the layer (CloudLayer.cs:226-229).
            // The rate is the stock one per frame and only the cap is scaled by the puff budget (ApplyBudget): the steady
            // count is min(cap, rate x fps x ~15 s life), so scaling the rate too would thin the deck most exactly when
            // the frame rate is low. Emits beyond the cap are dropped by the particle system, as in stock at the cap.
            float count = _generationRate(layer) * Mathf.Clamp01(1f + (topWorld - cam.position.y) * 0.00025f) * detail;
            if (Bench.Filling && system.particleCount < 0.95f * system.main.maxParticles)
                count += FillExtra; // benchmark only: reach a raised cap within its settle time
            float draw = _cloudDrawDist(layer);
            float scale = _densityMapScale(layer);
            float sizeFactor = _sizeFactor(layer);
            float variation = _cloudSizeVariation(layer);
            float lifeMin = _cloudLifeMin(layer);
            float lifeMax = Mathf.Max(lifeMin, _cloudLifeMax(layer));
            float sizeMin = _cloudSizeMin(layer);
            float sizeMax = Mathf.Max(sizeMin, _cloudSizeMax(layer));
            bool regional = CloudMap.Enabled;
            Texture2D sampler = ws.particleSampler;
            Vector3 origin = Datum.origin.position;
            // the sample point random-walks from the centre in steps of up to cloudDrawDist, as stock does
            // (CloudLayer.cs:225, 232), which spreads the puffs beyond one draw distance
            Vector3 walk = cam.position + cam.forward * draw * 0.2f + csm.cameraVelocity * 5f;
            Vector3 drift = new Vector3(li.windVelocity.x, 0f, li.windVelocity.z);
            Bands(cam.position.y - Datum.LocalSeaY, baseHere, t, out float pBase, out float pTop);
            // the weather clearing thins the puffs (down to 35 % at wetness 0, none at the end of the fade-out). The
            // stock loop runs ceil(count) times; the factor is applied to that with a random remainder, so it is 1 exactly
            // while the weather is fully wet
            float density = Mathf.Lerp(0.35f, 1f, Mathf.Clamp01(Plugin.WetnessOnly)) * Plugin.FadeFactor;
            int attempts = Mathf.CeilToInt(count);
            if (density < 0.999f)
                attempts = Mathf.FloorToInt(attempts * density + UnityEngine.Random.value);
            for (int i = 0; i < attempts; i++)
            {
                Vector2 u = UnityEngine.Random.insideUnitCircle;
                walk += new Vector3(u.x * draw, 0f, u.y * draw);
                GlobalPosition g = walk.ToGlobalPosition();
                Color pixel = sampler.GetPixel((int)((g.x - wind2.x) / scale % sampler.width), (int)((g.z - wind2.y) / scale % sampler.height));
                if (pixel.r <= 0.15f)
                    continue; // CloudLayer.cs:235
                float lb = b;
                if (regional)
                {
                    // cover: keep the puff where the coherent noise (drifting with the cloud pattern) is under the local cover
                    if (CloudMap.Gate(g.x - wind2.x, g.z - wind2.y) >= CloudMap.Cover(g.x, g.z))
                        continue;
                    lb = Plugin.LocalBaseAt(g.x, g.z);
                }
                float sizeCap = (t - lb) / MeshSpan;
                // stock size and lifetime (CloudLayer.cs:243-247): t = (|u.x| + |u.y|) / (cloudDrawDist * 1.2), which is
                // below 2.4e-4 with cloudDrawDist 7000, so in practice cloudSizeMin and cloudLifeMin; kept as written.
                // cloudSystem's startSize/startLifetime are in Constant mode, so these explicit values equal stock's
                // startSizeMultiplier/startLifetimeMultiplier.
                float st = (Mathf.Abs(u.x) + Mathf.Abs(u.y)) / (draw * 1.2f);
                float size = Mathf.Lerp(sizeMin, sizeMax, st) * Mathf.Lerp(0.1f, 1.1f, sizeFactor) *
                             UnityEngine.Random.Range(1f - variation, 1f + variation);
                size = Mathf.Min(size, sizeCap);
                _emit.position = new Vector3(walk.x - origin.x, PuffHeight(lb, t, size, pBase, pTop), walk.z - origin.z);
                _emit.velocity = drift;
                _emit.startSize = size;
                _emit.startLifetime = Mathf.Lerp(lifeMin, lifeMax, st);
                _emit.startColor = new Color(1f, 1f, 1f, pixel.r * 0.5f); // CloudLayer.cs:238
                system.Emit(_emit, 1);
            }
        }

        /// <summary>
        /// Which part of the deck new puffs go to, by the camera's side. The cloud planes are transparent (queue 2958,
        /// ZWrite off) and drawn before the puffs (MeshCloud, queue 2997), so a plane can never hide a puff behind it:
        /// puffs on the far side of the deck would just be blended over the plane. So under the base most puffs form
        /// the ragged base, over the top most form the bulging tops, and inside the deck the mix is even. Puffs live
        /// 15-20 s, so the mix follows the camera quickly.
        /// </summary>
        private static void Bands(float camAlt, float b, float t, out float pBase, out float pTop)
        {
            if (camAlt < b)
            {
                pBase = 0.85f;
                pTop = 0f;
            }
            else if (camAlt > t)
            {
                pBase = 0f;
                pTop = 0.85f;
            }
            else
            {
                pBase = 0.4f;
                pTop = 0.4f;
            }
        }

        /// <summary>Centre height (m MSL) of a puff of the given size inside the deck: with probability pBase its bottom
        /// at the base (ragged up to 40 m below), with pTop its top at the top (bulging up to 150 m above), else inside.</summary>
        private static float PuffHeight(float b, float t, float size, float pBase, float pTop)
        {
            float r = UnityEngine.Random.value;
            if (r < pBase)
                return b - UnityEngine.Random.Range(0f, BaseRag) + MeshBelow * size;
            if (r < pBase + pTop)
                return t + UnityEngine.Random.Range(0f, TopBulge) - MeshAbove * size;
            float lo = b + MeshBelow * size;
            float hi = t - MeshAbove * size;
            return hi > lo ? UnityEngine.Random.Range(lo, hi) : 0.5f * (b + t);
        }

        private static void FlyThrough(CloudLayer layer, WeatherSet ws, CameraStateManager csm, float b, float t, Vector2 wind2)
        {
            ParticleSystem fly = _flyThroughSystem(layer);
            if (fly == null)
                return;
            if (Bench.NoFlyThrough)
            {
                if (fly.isPlaying || fly.particleCount > 0)
                    fly.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                return; // the next frame without the override starts it again where it belongs
            }
            Transform cam = csm.transform;
            Vector3 velocity = csm.cameraVelocity;
            // probe 30 m ahead plus 0.8 s of camera motion (CloudLayer.cs:252-256)
            GlobalPosition probe = cam.GlobalPosition() + cam.forward * 30f + velocity * 0.8f;
            _camVelPrev(layer) = velocity;
            float alt = probe.y;
            b = Plugin.LocalBaseAt(probe.x, probe.z); // the local base (the eased mission base where the regional deck is off)
            float strength = alt < b ? 1f + (alt - b) / 40f : alt > t ? 1f - (alt - t) / 60f : 1f;
            strength = Mathf.Clamp01(strength);
            if (strength > 0f)
            {
                fly.transform.position = probe.ToLocalPosition(); // world position: the parent cloudPlane has scale (1,10,1)
                Texture2D sampler = ws.particleSampler;
                float scale = _densityMapScale(layer);
                Color pixel = sampler.GetPixel((int)((probe.x - wind2.x) / scale % sampler.width), (int)((probe.z - wind2.y) / scale % sampler.height));
                // the wisps follow the cover (none under 30 %, full from 90 %) and the weather's fade-out
                strength *= pixel.r * CloudMap.CoverFactor(CloudMap.Cover(probe.x, probe.z)) * Plugin.FadeFactor;
                if (pixel.r > 0f && strength > 0f)
                {
                    // CloudLayer.cs:274-280, with the rate and size reduced deep inside the deck. Stock runs this only
                    // within about -100..+133 m of its one layer; here the camera can be in cloud for 1.8 km of height,
                    // and full-screen wisps the whole time are a fill-rate cost (the in-cloud fog already hides the view).
                    // Full stock strength within 100 m of the base or the top, easing to FlyThroughInterior by 300 m.
                    float depth = Mathf.Min(alt - b, t - alt);
                    float deep = Smooth01((depth - 100f) / 200f);
                    float interior = Mathf.Lerp(1f, Mathf.Clamp01(Plugin.FlyThroughInterior.Value), deep);
                    float speed = velocity.magnitude;
                    var shape = fly.shape;
                    shape.radius = 50f * (1f + speed * 0.005f);
                    var main = fly.main;
                    main.startColor = new Color(1f, 1f, 1f, strength * strength);
                    var emission = fly.emission;
                    emission.rateOverTime = Mathf.Lerp(20f, 60f, speed * 0.003f * _cloudDetail(layer)) * interior;
                    fly.transform.LookAt(cam.position);
                    main.startLifetime = Mathf.Lerp(6f, 1.5f, speed * 0.005f);
                    float size = Mathf.Lerp(25f, 100f, speed * 0.003f);
                    main.startSize = Mathf.Lerp(size, Mathf.Min(size, 60f), deep);
                }
            }
            if (fly.isPlaying)
            {
                if (strength <= 0f)
                    fly.Stop();
            }
            else if (strength > 0f)
            {
                fly.Play();
            }
        }

        private static float Smooth01(float x)
        {
            x = Mathf.Clamp01(x);
            return x * x * (3f - 2f * x);
        }

        private static void UpdateTopPlane(CloudLayer layer, Material material, float t)
        {
            MeshRenderer baseRenderer = _cloudRenderer(layer);
            if (baseRenderer == null || material == null)
                return;
            if (_topPlane == null)
            {
                MeshFilter filter = baseRenderer.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null)
                    return;
                _topPlane = new GameObject("NOCloudDeck top");
                _topPlane.layer = baseRenderer.gameObject.layer;
                // a sibling of the cloud plane, so it goes away with the level's scene
                _topPlane.transform.SetParent(layer.transform.parent, false);
                _topPlane.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                MeshRenderer r = _topPlane.AddComponent<MeshRenderer>();
                _topRenderer = r;
                r.sharedMaterial = material; // the layer's own clone: same pattern, same drift
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = baseRenderer.receiveShadows;
                r.lightProbeUsage = baseRenderer.lightProbeUsage;
                r.reflectionProbeUsage = baseRenderer.reflectionProbeUsage;
            }
            Vector3 p = layer.transform.position;
            _topPlane.transform.position = new Vector3(p.x, Datum.LocalSeaY + t, p.z);
            _topPlane.transform.rotation = layer.transform.rotation;
            // the full lossy scale of the stock plane, (1,10,1) in level1: the CloudLayer shader graph has a
            // _displacement property, and a vertical object-space displacement would be scaled by y
            Vector3 s = layer.transform.lossyScale;
            Vector3 parentScale = _topPlane.transform.parent != null ? _topPlane.transform.parent.lossyScale : Vector3.one;
            _topPlane.transform.localScale = new Vector3(s.x / NonZero(parentScale.x), s.y / NonZero(parentScale.y), s.z / NonZero(parentScale.z));
        }

        private static float NonZero(float v)
        {
            return Mathf.Abs(v) < 1e-6f ? 1e-6f : v;
        }

        private static void DestroyTopPlane()
        {
            if (_topPlane != null)
                UnityEngine.Object.Destroy(_topPlane);
            _topPlane = null;
            _topRenderer = null;
            _topCulled = false;
            _topPatchHidden = false;
        }

        // ------------------------------------------------------------------------------ CloudLayer.UpdateWeatherSets

        private static void AfterUpdateWeatherSets(CloudLayer __instance)
        {
            try
            {
                if (!_fieldsOk || GameManager.IsHeadless)
                    return;
                if (Plugin.Active && Usable)
                    ShapeDistant(__instance);
                else if (_distantOwner == __instance)
                    RestoreDistant(__instance);
            }
            catch (Exception e)
            {
                Plugin.ErrorOnce("deck-distant", "the distant clouds", e);
            }
        }

        /// <summary>The distant ring (10-40 km, CloudLayer.cs:187-189) inside B..T: the flat puff meshes of the near
        /// system instead of the tall meshCloud3/4 (whose tops reach 1.6 x size above the centre), sizes capped to the
        /// deck, and the emitter raised so the largest puff's bottom sits at the base.</summary>
        private static void ShapeDistant(CloudLayer layer)
        {
            ParticleSystem distant = _distantCloudSystem(layer);
            ParticleSystem near = _cloudSystem(layer);
            LevelInfo li = NetworkSceneSingleton<LevelInfo>.i;
            if (distant == null || near == null || li == null)
                return;
            var distantRenderer = distant.GetComponent<ParticleSystemRenderer>();
            var nearRenderer = near.GetComponent<ParticleSystemRenderer>();
            if (_distantOwner != layer)
            {
                // a different layer (a new scene): put the previous one back first if it still exists
                if (!ReferenceEquals(_distantOwner, null))
                    RestoreDistant(_distantOwner);
                _distantOwner = layer;
                _distantMeshes = null;
                _distantRenderer = null;
            }
            // capture the originals only from a renderer this class has not swapped yet, so the saved "originals"
            // can never be the flat meshes and the raised position
            if (_distantMeshes == null && distantRenderer != null && distantRenderer != _distantRenderer &&
                nearRenderer != null && nearRenderer.meshCount > 0)
            {
                _distantMeshes = new Mesh[distantRenderer.meshCount];
                distantRenderer.GetMeshes(_distantMeshes);
                _distantLocalPos = distant.transform.localPosition;
                var flat = new Mesh[nearRenderer.meshCount];
                nearRenderer.GetMeshes(flat);
                distantRenderer.SetMeshes(flat);
                _distantRenderer = distantRenderer;
            }
            float t = Plugin.TopFor(Plugin.BaseNow);
            float b = Plugin.BaseNow;
            float ringCover = 1f;
            RingMean(ref b, ref ringCover);
            float sizeFactor = _sizeFactor(layer);
            float max = Mathf.Min(1800f * sizeFactor, (t - b) / MeshSpan);
            float min = Mathf.Min(1200f * sizeFactor, 0.75f * max);
            var main = distant.main;
            main.startSize = new ParticleSystem.MinMaxCurve(min, max);
            // thin the ring by the map's mean cover and the weather's wetness (once a second, with this method)
            var emission = distant.emission;
            if (float.IsNaN(_ringRate) && emission.rateOverTime.mode == ParticleSystemCurveMode.Constant)
                _ringRate = emission.rateOverTime.constant;
            if (!float.IsNaN(_ringRate))
                emission.rateOverTime = _ringRate * Mathf.Clamp01(ringCover) * Mathf.Lerp(0.35f, 1f, Mathf.Clamp01(Plugin.WetnessOnly)) * Plugin.FadeFactor;
            // world position: the parent cloudPlane has localScale (1,10,1), so localPosition would move 10x
            Vector3 p = distant.transform.position;
            distant.transform.position = new Vector3(p.x, Datum.LocalSeaY + b + MeshBelow * max, p.z);
        }

        /// <summary>The mean local base and cover over the distant ring: 8 bearings at 12, 25 and 38 km from the camera
        /// (the ring is 10-40 km out). Leaves both as given where the regional deck is off.</summary>
        private static void RingMean(ref float meanBase, ref float meanCover)
        {
            CameraStateManager csm = SceneSingleton<CameraStateManager>.i;
            if (!CloudMap.Enabled || csm == null)
                return;
            GlobalPosition c = csm.transform.GlobalPosition();
            float sb = 0f, sc = 0f;
            int n = 0;
            for (int r = 0; r < 3; r++)
            {
                float d = r == 0 ? 12000f : r == 1 ? 25000f : 38000f;
                for (int k = 0; k < 8; k++)
                {
                    float a = k * (Mathf.PI / 4f);
                    float x = c.x + Mathf.Cos(a) * d;
                    float z = c.z + Mathf.Sin(a) * d;
                    sb += Plugin.LocalBaseAt(x, z);
                    sc += CloudMap.Cover(x, z);
                    n++;
                }
            }
            meanBase = sb / n;
            meanCover = sc / n;
        }

        private static void RestoreDistant(CloudLayer layer)
        {
            try
            {
                ParticleSystem distant = layer != null ? _distantCloudSystem(layer) : null; // Unity null if destroyed
                if (distant != null && !float.IsNaN(_ringRate))
                {
                    var emission = distant.emission;
                    emission.rateOverTime = _ringRate;
                }
                if (distant != null && _distantMeshes != null)
                {
                    var r = distant.GetComponent<ParticleSystemRenderer>();
                    if (r != null)
                        r.SetMeshes(_distantMeshes);
                    distant.transform.localPosition = _distantLocalPos;
                    // the stock size comes back with the next UpdateWeatherSets (CloudLayer.cs:189)
                }
            }
            finally
            {
                _ringRate = float.NaN;
                _distantMeshes = null;
                _distantOwner = null;
                _distantRenderer = null;
            }
        }

        /// <summary>Everything back to stock. The distant ring is restored on the layer it was changed on
        /// (_distantOwner), which after a scene swap may differ from the layer passed in.</summary>
        internal static void StandDown(CloudLayer layer)
        {
            DestroyTopPlane();
            RestoreBasePlane();
            ClearLee();
            _baseCulled = false;
            _baseLeeHidden = false;
            if (!_fieldsOk)
                return;
            RestoreBudget();
            // a reference test: a destroyed owner compares equal to null with Unity's ==, but its state must still be
            // dropped (RestoreDistant does nothing to a destroyed system)
            if (!ReferenceEquals(_distantOwner, null))
                RestoreDistant(_distantOwner);
            else
            {
                _distantMeshes = null;
                _distantRenderer = null;
            }
            if (layer != null)
            {
                ParticleSystem fly = _flyThroughSystem(layer);
                if (fly != null && fly.isPlaying)
                    fly.Stop(); // the stock Update starts it again where it belongs
            }
        }
    }
}
