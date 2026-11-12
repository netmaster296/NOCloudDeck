using System;
using UnityEngine;
using UnityEngine.Audio;

namespace CloudDeck
{
    /// <summary>
    /// Rain under the cloud base: stretched-billboard streaks emitted around the camera and a looping rain sound,
    /// both scaled by the local rain intensity (RainMap x RainIntensityScale) and faded out over the last 80 m below
    /// the base. The particles are simulated in Datum.origin space, as the stock clouds are (CloudLayer.cs:103-112),
    /// so a floating-origin shift does not jerk them; they fall at 8-9 m/s and drift with LevelInfo.windVelocity
    /// (read only). The streak texture and the sound are generated here: no outside asset is used.
    /// </summary>
    internal sealed class Rain : MonoBehaviour
    {
        private const float Radius = 40f;      // half width of the box the streaks are born in, m
        private const float TopAbove = 28f;    // born up to this far above the camera, m
        private const float BottomBelow = 8f;  // and this far below it
        private const float Life = 4f;         // s: 4 s x 8.5 m/s = 34 m of fall
        private const float FadeBelowBase = 80f;

        // generated once per session; DontUnloadUnusedAsset keeps Resources.UnloadUnusedAssets (scene loads) from
        // destroying them while only these statics hold them
        private static Material _material;
        private static string _materialFor;   // the RainMaterial setting _material was made for
        private static string _failedFor;     // the setting a search found nothing for (not repeated)
        private static Texture2D _texture;
        private static AudioClip _clip;

        private ParticleSystem _system;
        private ParticleSystemRenderer _renderer;
        private AudioSource _audio;
        private AudioLowPassFilter _lowPass;
        private ParticleSystem.EmitParams _emit;
        private float _carry;
        private float _nextPrime;
        private Vector3 _lastView;
        private bool _lastViewKnown;
        private float _volume;
        private bool _mixerSet;
        private bool _failed;

        internal static bool Needed => Plugin.RainEnabled.Value && (Plugin.MaxRainParticles.Value > 0 || Plugin.RainSoundVolume.Value > 0f);

        /// <summary>The live rain object, if any (for the performance log).</summary>
        internal static Rain Current { get; private set; }

        internal int ParticleCount => _system != null ? _system.particleCount : 0;

        private void OnDestroy()
        {
            if (Current == this)
                Current = null;
        }

        internal static Rain Create()
        {
            try
            {
                var go = new GameObject("NOCloudDeck rain"); // in the active scene: unloaded with the mission
                return go.AddComponent<Rain>();
            }
            catch (Exception e)
            {
                Plugin.ErrorOnce("rain-create", "creating the rain", e);
                return null;
            }
        }

        private void Awake()
        {
            Current = this;
            try
            {
                Build();
            }
            catch (Exception e)
            {
                Fail("building the rain", e);
            }
        }

        private void Build()
        {
            _system = gameObject.AddComponent<ParticleSystem>();
            _system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = _system.main;
            main.loop = true;
            main.playOnAwake = false;
            main.startSpeed = 0f;
            main.startLifetime = Life;
            main.startSize = 0.03f;
            main.gravityModifier = 0f;
            main.scalingMode = ParticleSystemScalingMode.Local;
            main.maxParticles = Mathf.Max(1, Plugin.MaxRainParticles.Value);
            if (Datum.origin != null)
            {
                main.simulationSpace = ParticleSystemSimulationSpace.Custom;
                main.customSimulationSpace = Datum.origin;
            }
            var emission = _system.emission;
            emission.enabled = false; // emitted from code, like the stock clouds
            var shape = _system.shape;
            shape.enabled = false;

            var renderer = GetComponent<ParticleSystemRenderer>();
            _renderer = renderer;
            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.lengthScale = 2f;        // length = size * 2 + speed * 0.12, about 1.1 m at 8.5 m/s
            // (the particles' own fall speed: cameraVelocityScale stays 0, so streaks do not lengthen with flight speed)
            renderer.velocityScale = 0.12f;
            renderer.cameraVelocityScale = 0f; // a floating-origin shift would otherwise draw one huge streak
            renderer.sortMode = ParticleSystemSortMode.None;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            ShowMaterial();

            _audio = gameObject.AddComponent<AudioSource>();
            _audio.clip = RainClip();
            _audio.loop = true;
            _audio.playOnAwake = false;
            _audio.spatialBlend = 0f;
            _audio.dopplerLevel = 0f;
            _audio.priority = 64;
            _audio.volume = 0f;
            _lowPass = gameObject.AddComponent<AudioLowPassFilter>();
            _lowPass.cutoffFrequency = 12000f;
        }

        private void Fail(string what, Exception e)
        {
            _failed = true;
            Plugin.ErrorOnce("rain:" + what, what, e);
            try
            {
                if (_system != null)
                    _system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                if (_audio != null)
                    _audio.Stop();
            }
            catch (Exception) { }
        }

        private void LateUpdate()
        {
            if (_failed)
                return;
            try
            {
                Tick();
            }
            catch (Exception e)
            {
                Fail("the rain", e);
            }
        }

        private void Tick()
        {
            CameraStateManager csm = SceneSingleton<CameraStateManager>.i;
            LevelInfo li = NetworkSceneSingleton<LevelInfo>.i;
            if (!Plugin.Active || csm == null || li == null || Datum.origin == null || _system == null)
            {
                Fade(0f);
                return;
            }
            if (Bench.NoRain)
            {
                // the benchmark's rain-off phase: no streaks and no sound; the next frame without it starts again
                if (_system.isPlaying || _system.particleCount > 0)
                    _system.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                _carry = 0f;
                Fade(0f);
                return;
            }
            if (!ReferenceEquals(Plugin.RainMaterialChoice.Value, _materialFor))
                ShowMaterial(); // the RainMaterial setting changed (a string compare only when it did)
            Camera cam = csm.mainCamera;
            Transform view = cam != null ? cam.transform : csm.transform;
            transform.position = view.position;
            bool editor = GameManager.gameState == GameState.Editor;
            float dt = editor ? Time.unscaledDeltaTime : Time.deltaTime;

            GlobalPosition g = view.position.ToGlobalPosition();
            float intensity = Plugin.RainIntensityAt(g.x, g.z);
            float b = Plugin.LocalBaseAt(g.x, g.z); // the local cloud base (the mission's where the regional deck is off)
            float under = 1f - Mathf.Clamp01((g.y - (b - FadeBelowBase)) / FadeBelowBase);
            bool wet = Plugin.RainEnabled.Value && !Atmosphere.Underwater(csm) && g.y > -1f;
            float k = wet ? Mathf.Clamp01(intensity * under) : 0f;

            var main = _system.main;
            if (main.customSimulationSpace != Datum.origin)
            {
                main.simulationSpace = ParticleSystemSimulationSpace.Custom;
                main.customSimulationSpace = Datum.origin;
            }
            if (main.useUnscaledTime != editor)
                main.useUnscaledTime = editor;
            int cap = Mathf.Max(1, Plugin.MaxRainParticles.Value);
            if (main.maxParticles != cap)
                main.maxParticles = cap;
            if (!_system.isPlaying)
                _system.Play();

            // Density, not count, is what the eye sees. At rest a full cap of streaks fills the 80 x 36 x 80 m box at
            // intensity 1. A moving camera would leave a fixed-rate box behind (at 250 m/s it crosses it in 0.3 s), so
            // the streaks are spread along the path the camera will fly during their (shorter) life, and the rate is the
            // rest density times the box volume plus the volume swept per second, capped at MaxRainParticles alive.
            Vector3 velocity = csm.cameraVelocity;
            float speed = velocity.magnitude;
            float life = Mathf.Clamp(60f / Mathf.Max(speed, 1f), 0.6f, Life);
            float radius = Mathf.Lerp(Radius, 20f, Mathf.Clamp01(speed / 250f)); // the sides matter less at speed
            float height = TopAbove + BottomBelow;
            float box = 4f * radius * radius * height;
            float hSpeed = new Vector2(velocity.x, velocity.z).magnitude;
            float swept = (2f * radius * height * hSpeed + 4f * radius * radius * Mathf.Abs(velocity.y)) * life;
            float most = Mathf.Max(0, Plugin.MaxRainParticles.Value);
            float restDensity = most / (4f * Radius * Radius * height);
            float alive = Mathf.Min(most, restDensity * (box + swept)) * k;
            _carry += alive / life * dt;
            int n = Mathf.Min((int)_carry, 1000);
            _carry -= (int)_carry;
            if (n > 0)
                Emit(n, view, li, velocity, life, radius, false);
            if (Plugin.RainPrewarm.Value)
                Prewarm(view, li, velocity, life, radius, alive);

            bool paused = !editor && Time.timeScale <= 0f;
            bool cockpit = CameraStateManager.cameraMode == CameraMode.cockpit;
            _lowPass.cutoffFrequency = cockpit ? 1800f : 12000f; // drumming on the canopy, muffled
            float target = paused ? 0f : Plugin.RainSoundVolume.Value * k * (cockpit ? 0.8f : 1f);
            EnsureMixer(csm);
            Fade(target);
        }

        /// <summary>
        /// Rain that is visible at once. Steady emission needs about one streak life (up to 4 s) to fill the box, and
        /// none happens while the game is paused (dt = 0, e.g. a photo shoot at timeScale 0). So when the streaks alive
        /// are fewer than half of what the intensity calls for, or the camera has jumped (teleport, respawn, a mission
        /// start), the box is filled in one go with streaks of mixed ages, at most once a second. Purely a visual
        /// effect on this client: particles only, nothing synced.
        /// </summary>
        private void Prewarm(Transform view, LevelInfo li, Vector3 velocity, float life, float radius, float alive)
        {
            bool jumped = _lastViewKnown && Time.unscaledDeltaTime < 0.25f && (view.position - _lastView).sqrMagnitude > 200f * 200f;
            _lastView = view.position;
            _lastViewKnown = true;
            if (alive < 20f)
                return;
            float now = Time.unscaledTime;
            int have = _system.particleCount;
            int want = Mathf.RoundToInt(alive * 0.9f);
            if (!jumped && (now < _nextPrime || have >= alive * 0.5f))
                return;
            _nextPrime = now + 1f;
            int n = Mathf.Min(Mathf.Min(jumped ? want : want - have, 3000), Mathf.Max(0, _system.main.maxParticles - have));
            if (n > 0)
            {
                Emit(n, view, li, velocity, life, radius, true);
                if (!_primedLogged)
                {
                    _primedLogged = true;
                    Plugin.Log?.LogInfo("rain prewarm: filled the box with " + n + " streaks at once (" + have + " alive, " +
                                        (int)alive + " wanted" + (Time.timeScale <= 0f ? ", game paused" : "") + (jumped ? ", camera jumped" : "") + ")");
                }
            }
        }

        private bool _primedLogged;

        private void Emit(int n, Transform view, LevelInfo li, Vector3 velocity, float life, float radius, bool spreadAges)
        {
            Vector3 origin = Datum.origin.position;
            Vector3 wind = li.windVelocity;
            Vector3 centre = view.position;
            for (int i = 0; i < n; i++)
            {
                // a box around the camera, shifted to a random point on the path it flies during the streaks' life
                Vector3 p = centre + velocity * UnityEngine.Random.Range(0f, life) +
                            new Vector3(UnityEngine.Random.Range(-radius, radius),
                                UnityEngine.Random.Range(-BottomBelow, TopAbove), UnityEngine.Random.Range(-radius, radius));
                _emit.position = p - origin; // Datum.origin space, as CloudLayer.cs:239 does
                _emit.velocity = new Vector3(wind.x, -UnityEngine.Random.Range(8f, 9f), wind.z);
                _emit.startLifetime = spreadAges ? life * UnityEngine.Random.Range(0.25f, 1f) : life; // mixed ages when filling at once
                // 4.5-6.5 cm wide and faint: thinner streaks fall under one pixel beyond about 30 m and sparkle
                _emit.startSize = UnityEngine.Random.Range(0.045f, 0.065f);
                _emit.startColor = new Color32(205, 210, 220, 85);
                _system.Emit(_emit, 1);
            }
        }

        private void Fade(float target)
        {
            if (_audio == null)
                return;
            _volume = Mathf.MoveTowards(_volume, target, Time.unscaledDeltaTime * 0.5f);
            _audio.volume = _volume;
            if (_volume > 0.001f)
            {
                if (!_audio.isPlaying && _audio.clip != null)
                    _audio.Play();
            }
            else if (_audio.isPlaying)
            {
                _audio.Stop();
            }
        }

        /// <summary>The game's own mixer group so the player's volume settings apply: the one the external wind noise
        /// uses (CameraStateManager.windNoiseExternal), else SoundManager's effects group (used by most effect sounds,
        /// e.g. AeroPart.cs:249).</summary>
        private void EnsureMixer(CameraStateManager csm)
        {
            if (_mixerSet || _audio == null)
                return;
            _mixerSet = true;
            AudioMixerGroup group = null;
            string from = null;
            try
            {
                AudioSource wind = csm.windNoiseExternal;
                if (wind != null && wind.outputAudioMixerGroup != null)
                {
                    group = wind.outputAudioMixerGroup;
                    from = "wind noise";
                }
            }
            catch (Exception) { }
            if (group == null)
            {
                try
                {
                    SoundManager sm = SoundManager.i;
                    if (sm != null && sm.EffectsMixer != null)
                    {
                        group = sm.EffectsMixer;
                        from = "effects";
                    }
                }
                catch (Exception) { }
            }
            _audio.outputAudioMixerGroup = group;
            Plugin.Log?.LogInfo("rain sound goes to the " + (group != null ? from + " mixer group (" + group.name + ")" : "master output (no game mixer group found)"));
        }

        // ------------------------------------------------------------------------------------- generated assets

        /// <summary>Put the material for the current RainMaterial setting on this rain's renderer (off if there is none).</summary>
        private void ShowMaterial()
        {
            Material m = RainMaterial();
            if (_renderer == null)
                return;
            if (m != null)
            {
                if (_renderer.sharedMaterial != m)
                    _renderer.sharedMaterial = m;
                _renderer.enabled = true;
            }
            else
            {
                _renderer.enabled = false;
            }
        }

        private const string UnlitName = "Universal Render Pipeline/Particles/Unlit";
        private const string ParticlesPrefix = "Universal Render Pipeline/Particles/";

        // Auto: the cheapest material whose exact shader variant is known to be in the game build first. A clone of a
        // game material that is already transparent with no feature keyword keeps its keyword set through Sanitize, so
        // it draws with a variant the game itself uses. 1.0.0 drew with such a clone of 'WaterShockwave' (Particles/Lit,
        // keywords _SURFACE_TYPE_TRANSPARENT _FLIPBOOKBLENDING_OFF) without a reported problem.
        //  1. UnlitClone (strict): a game material on URP Particles/Unlit, transparent, no feature keyword,
        //  2. LitClone (strict): the same on any URP particle shader (in practice Lit, the 1.0.0 choice),
        //  3. Sprite: Sprites/Default (a built-in shader with few variants; drawn by URP as an unlit pass),
        //  4. Unlit: a new Particles/Unlit material (its sanitised keyword set may have been stripped from the build),
        //  5. a clone of the best-ranked URP particle material whatever its keywords, sanitised.
        private static readonly string[] AutoOrder = { "UnlitClone", "LitClone", "Sprite", "Unlit", "AnyClone" };

        private static Material RainMaterial()
        {
            string choice = Plugin.RainMaterialChoice.Value ?? "Auto";
            if (_material != null && _materialFor == choice)
            {
                _materialFor = choice;
                return _material;
            }
            _materialFor = choice;
            if (_failedFor == choice)
                return null; // looked once and found nothing: do not scan every material again on each activation
            // the material made for the previous setting: destroyed at the end of this frame, after ShowMaterial (the
            // only caller) has put the new one on the renderer
            Material previous = _material;
            _material = null;
            if (previous != null)
                UnityEngine.Object.Destroy(previous);
            try
            {
                Texture2D texture = StreakTexture();
                string how = null;
                Material m = null;
                if (choice != "Auto")
                {
                    m = Make(choice, false, out how);
                    if (m == null)
                        Plugin.Log?.LogWarning("rain material '" + choice + "' is not available in this game; using Auto");
                }
                for (int i = 0; m == null && i < AutoOrder.Length; i++)
                    m = Make(AutoOrder[i], true, out how);
                if (m == null)
                {
                    _failedFor = choice;
                    Plugin.WarnOnce("rain-material", "no particle shader found; rain streaks are off (the rain sound still plays)");
                    return null;
                }
                m.name = "NOCloudDeck rain";
                m.hideFlags = HideFlags.DontUnloadUnusedAsset;
                foreach (string tex in new[] { "_BaseMap", "_MainTex" })
                {
                    if (m.HasProperty(tex))
                        m.SetTexture(tex, texture);
                }
                foreach (string col in new[] { "_BaseColor", "_Color", "_TintColor" })
                {
                    if (m.HasProperty(col))
                        m.SetColor(col, new Color(0.8f, 0.82f, 0.86f, 0.5f));
                }
                if (m.HasProperty("_EmissionColor"))
                    m.SetColor("_EmissionColor", Color.black); // the lightning material glows; rain must not
                _material = m;
                Plugin.Log?.LogInfo("rain material (setting " + choice + "): " + how + "; now blend " + Float(m, "_SrcBlend") + "/" +
                                    Float(m, "_DstBlend") + ", surface " + Float(m, "_Surface") + ", queue " + m.renderQueue +
                                    ", keywords [" + string.Join(" ", m.shaderKeywords) + "]. If the rain draws pink, " +
                                    "opaque or not at all, try another Rain.RainMaterial setting.");
                return m;
            }
            catch (Exception e)
            {
                _failedFor = choice;
                Plugin.ErrorOnce("rain-material-ex", "making the rain material", e);
                return null;
            }
        }

        /// <summary>One candidate material, or null if this game has none of that kind. strict (Auto) = a clone only of a
        /// material whose keyword set Sanitize leaves unchanged.</summary>
        private static Material Make(string kind, bool strict, out string how)
        {
            how = null;
            switch (kind)
            {
                case "UnlitClone":
                    return Clone(FindMaterial(UnlitName, strict), out how);
                case "LitClone":
                    return Clone(FindMaterial(ParticlesPrefix, strict), out how);
                case "AnyClone":
                    return Clone(FindMaterial(ParticlesPrefix, false), out how);
                case "Unlit":
                {
                    Shader unlit = Shader.Find(UnlitName);
                    if (unlit == null || !unlit.isSupported)
                        return null;
                    var m = new Material(unlit);
                    Sanitize(m);
                    how = "new material on " + unlit.name;
                    return m;
                }
                case "Sprite":
                {
                    Shader sprite = Shader.Find("Sprites/Default");
                    if (sprite == null || !sprite.isSupported)
                        return null;
                    var m = new Material(sprite);
                    m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                    how = "new material on " + sprite.name;
                    return m;
                }
            }
            return null;
        }

        private static Material Clone(Material source, out string how)
        {
            how = null;
            if (source == null)
                return null;
            how = "clone of the game's '" + source.name + "' (" + source.shader.name + "), whose own keywords were [" +
                  string.Join(" ", source.shaderKeywords) + "]";
            var m = new Material(source);
            Sanitize(m);
            return m;
        }

        private static string Float(Material m, string name)
        {
            return m.HasProperty(name) ? m.GetFloat(name).ToString("0.##") : "-";
        }

        // URP particle keywords that change the look and that a plain alpha-blended streak must not have
        private static readonly string[] FeatureKeywords =
        {
            "_SOFTPARTICLES_ON", "_FADING_ON", "_DISTORTION_ON", "_FLIPBOOKBLENDING_ON", "_ALPHATEST_ON",
            "_ALPHAPREMULTIPLY_ON", "_ALPHAMODULATE_ON", "_COLOROVERLAY_ON", "_COLORCOLOR_ON", "_COLORADDSUBDIFF_ON",
            "_EMISSION", "_NORMALMAP", "_RECEIVE_SHADOWS_OFF",
        };

        /// <summary>
        /// The best loaded material on a shader whose name starts with the given text, for cloning. Which game material
        /// is found first is arbitrary (Resources.FindObjectsOfTypeAll order), so candidates are ranked: transparent
        /// (queue >= 2500) is required; already plain alpha-blended (_Surface 1, _Blend 0 = Alpha, _ColorMode 0 =
        /// Multiply) with no feature keyword scores highest, because that exact shader variant is then known to be in the
        /// build; Unlit beats Lit. Ties keep the first found. strict: only a material that already has
        /// _SURFACE_TYPE_TRANSPARENT and no feature keyword, so that Sanitize does not change its keyword set and the
        /// clone draws with a shader variant the game itself uses.
        /// </summary>
        private static Material FindMaterial(string shaderPrefix, bool strict)
        {
            Material best = null;
            int bestScore = int.MinValue;
            foreach (Material m in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (m == null || m.shader == null || !m.shader.name.StartsWith(shaderPrefix, StringComparison.Ordinal))
                    continue;
                if (m.renderQueue < 2500)
                    continue;
                int score = 0;
                bool alpha = Is(m, "_Surface", 1f) && Is(m, "_Blend", 0f) && Is(m, "_ColorMode", 0f) &&
                             Is(m, "_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                if (alpha)
                    score += 4;
                int features = 0;
                foreach (string k in FeatureKeywords)
                {
                    if (k != "_RECEIVE_SHADOWS_OFF" && m.IsKeywordEnabled(k))
                        features++;
                }
                if (strict && (features > 0 || !m.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT")))
                    continue;
                if (features == 0)
                    score += 8;
                else
                    score -= features;
                if (m.shader.name.Contains("Unlit"))
                    score += 2;
                if (score > bestScore)
                {
                    best = m;
                    bestScore = score;
                }
            }
            return best;
        }

        private static bool Is(Material m, string name, float value)
        {
            return !m.HasProperty(name) || Mathf.Approximately(m.GetFloat(name), value);
        }

        /// <summary>A URP particle material set to plain alpha blending with every optional feature off (what the URP
        /// material editor would set for Surface Transparent, Blend Alpha, Color Mode Multiply, no soft particles, no
        /// camera fading, no distortion, no flipbook blending, no emission).</summary>
        private static void Sanitize(Material m)
        {
            void F(string name, float v)
            {
                if (m.HasProperty(name))
                    m.SetFloat(name, v);
            }
            F("_Surface", 1f);
            F("_Blend", 0f);
            F("_BlendOp", 0f);
            F("_ColorMode", 0f);
            F("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            F("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            F("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
            F("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            F("_ZWrite", 0f);
            F("_AlphaClip", 0f);
            F("_SoftParticlesEnabled", 0f);
            F("_CameraFadingEnabled", 0f);
            F("_DistortionEnabled", 0f);
            F("_FlipbookBlending", 0f);
            m.SetOverrideTag("RenderType", "Transparent");
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            foreach (string k in FeatureKeywords)
            {
                if (k != "_RECEIVE_SHADOWS_OFF") // harmless, and keeping it keeps the variant the source used
                    m.DisableKeyword(k);
            }
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }

        /// <summary>A soft round spot; the stretched billboard draws it out into a streak along the fall.</summary>
        private static Texture2D StreakTexture()
        {
            if (_texture != null)
                return _texture;
            const int size = 32;
            var t = new Texture2D(size, size, TextureFormat.RGBA32, true);
            t.name = "NOCloudDeck rain streak";
            t.wrapMode = TextureWrapMode.Clamp;
            t.filterMode = FilterMode.Bilinear;
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size * 2f - 1f;
                    float v = (y + 0.5f) / size * 2f - 1f;
                    float a = Mathf.Exp(-(u * u + v * v) / (2f * 0.38f * 0.38f));
                    a *= Mathf.Clamp01((1f - Mathf.Sqrt(u * u + v * v)) * 4f);
                    px[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(255f * a));
                }
            }
            t.SetPixels32(px);
            t.Apply(true, true);
            t.hideFlags = HideFlags.DontUnloadUnusedAsset;
            _texture = t;
            return t;
        }

        /// <summary>A 6 s seamless loop of rain: mostly white noise with some pink noise, band-limited to about
        /// 350 Hz - 6 kHz, plus sparse short drop transients. Deterministic (fixed seed).</summary>
        private static AudioClip RainClip()
        {
            if (_clip != null)
                return _clip;
            try
            {
                const int rate = 44100;
                const int length = rate * 6;
                const int fade = rate / 2;
                var raw = new float[length + fade];
                var rng = new System.Random(20261106);
                float p0 = 0f, p1 = 0f, p2 = 0f;
                float hpIn = 0f, hpOut = 0f, lp1 = 0f, lp2 = 0f, drop = 0f;
                float dt = 1f / rate;
                float rcHp = 1f / (2f * Mathf.PI * 350f);
                float aHp = rcHp / (rcHp + dt);
                float rcLp = 1f / (2f * Mathf.PI * 6000f);
                float aLp = dt / (rcLp + dt);
                float peak = 1e-6f;
                for (int i = 0; i < raw.Length; i++)
                {
                    float white = (float)(rng.NextDouble() * 2.0 - 1.0);
                    p0 = 0.99765f * p0 + white * 0.0990460f;
                    p1 = 0.96300f * p1 + white * 0.2965164f;
                    p2 = 0.57000f * p2 + white * 1.0526913f;
                    float pink = (p0 + p1 + p2 + white * 0.1848f) * 0.2f;
                    if (rng.NextDouble() < 90.0 / rate)
                        drop = (float)(0.6 + rng.NextDouble() * 1.2);
                    float x = 0.55f * white + 0.45f * pink + drop * (float)(rng.NextDouble() * 2.0 - 1.0);
                    drop *= 0.9972f; // about an 8 ms decay
                    hpOut = aHp * (hpOut + x - hpIn);
                    hpIn = x;
                    lp1 += aLp * (hpOut - lp1);
                    lp2 += aLp * (lp1 - lp2);
                    raw[i] = lp2;
                    peak = Mathf.Max(peak, Mathf.Abs(lp2));
                }
                var data = new float[length];
                float gain = 0.6f / peak;
                for (int i = 0; i < length; i++)
                {
                    float s = raw[i];
                    if (i < fade)
                    {
                        // equal-power crossfade with the tail, so the end runs straight into the start
                        float w = (float)i / fade;
                        s = raw[i] * Mathf.Sqrt(w) + raw[length + i] * Mathf.Sqrt(1f - w);
                    }
                    data[i] = Mathf.Clamp(s * gain, -1f, 1f);
                }
                AudioClip clip = AudioClip.Create("NOCloudDeck rain", length, 1, rate, false);
                clip.SetData(data, 0);
                clip.hideFlags = HideFlags.DontUnloadUnusedAsset;
                _clip = clip;
                return clip;
            }
            catch (Exception e)
            {
                Plugin.ErrorOnce("rain-clip", "making the rain sound", e);
                return null;
            }
        }
    }
}
