using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace CloudDeck
{
    /// <summary>
    /// NO Cloud Deck: a Pacific Northwest rain day for Nuclear Option's Overcast and Thunderstorm weather.
    ///
    /// The stock game (0.34.6) has one cloud sheet at LevelInfo.cloudHeight (the mission's cloudAltitude, metres above
    /// sea level), greys the whole scene and turns the sun off when the camera is below it, has lightning in the
    /// Thunderstorm set only, and has no rain at all. With this plugin, in the missions it is set up for:
    ///  - the cloudAltitude is the cloud BASE and CloudTop the top; the clouds fill base..top (Deck.cs),
    ///  - the deck is regional (Clouds.RegionalDeck, CloudMap.cs): the base is lower on the windward slopes and higher
    ///    in the lee, the cover thins east of the Cascade crest, the lee side sees further and is drier,
    ///  - as the weather clears (conditions 0.8 down to 0.4, "wetness") rain, haze and the deck thin out and the top
    ///    comes down onto the base, then everything fades out over a few seconds as the stock sets take over,
    ///  - under the base the air is clear apart from rain haze that follows the regional rain map; inside the deck the
    ///    fog is dense and the sun hidden; above the top the air is clear and the sun out (Atmosphere.cs),
    ///  - rain streaks and a rain sound follow the camera under the base (Rain.cs, RainMap.cs),
    ///  - lightning is occasional and strikes from the base to the ground (Storm.cs).
    ///
    /// Everything is visual and local. The plugin never writes a Mirage SyncVar (LevelInfo conditions, cloudHeight,
    /// wind*, timeOfDay, moonPhase), never patches LevelInfo.GetCloudOcclusion, never changes sun.color or the sun's
    /// transform (the server-side IR seeker reads them, IRSeeker.cs:207-209), and keeps the cloud layer transform at
    /// LocalSeaY + cloudHeight (LevelInfo.cs:454 and :541 read it). It does nothing on a headless server.
    /// </summary>
    [BepInPlugin(Id, "NO Cloud Deck", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Id = "com.jack.noclouddeck";
        public const string Version = "1.2.0";

        internal static Plugin Instance;
        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<string> Missions;
        internal static ConfigEntry<float> CloudTop;
        internal static ConfigEntry<bool> RegionalDeck;
        internal static ConfigEntry<bool> RainEnabled;
        internal static ConfigEntry<bool> RainPrewarm;
        internal static ConfigEntry<bool> LeeFade;
        internal static ConfigEntry<float> AmbientLift;
        internal static ConfigEntry<float> RainIntensityScale;
        internal static ConfigEntry<bool> UseRainMap;
        internal static ConfigEntry<float> BelowBaseVisibility;
        internal static ConfigEntry<float> InCloudVisibility;
        internal static ConfigEntry<float> FlyThroughInterior;
        internal static ConfigEntry<float> CloudParticleBudget;
        internal static ConfigEntry<bool> PlaneCulling;
        internal static ConfigEntry<float> PlaneCullMargin;
        internal static ConfigEntry<float> LightningMinInterval;
        internal static ConfigEntry<float> LightningMaxInterval;
        internal static ConfigEntry<float> RainSoundVolume;
        internal static ConfigEntry<int> MaxRainParticles;
        internal static ConfigEntry<bool> PerfLog;
        internal static ConfigEntry<bool> BenchmarkInMultiplayer;
        internal static ConfigEntry<int> BenchmarkAutoRuns;
        internal static ConfigEntry<float> BenchmarkMinutes;
        internal static ConfigEntry<string> RainMaterialChoice;
        private static ConfigEntry<string> ConfigVersion;

        /// <summary>Goes up whenever any setting of this plugin changes (the benchmark aborts on a change).</summary>
        internal static int SettingsVersion => Instance != null ? Instance._settingsVersion : 0;

        /// <summary>All features on: matched mission, Overcast or Thunderstorm, not headless, no conflicting mod.</summary>
        internal static bool Active { get; private set; }

        /// <summary>A mission matching the Missions filter is loaded on a machine that draws (not headless), whatever
        /// the weather or the Enabled switch. The performance log and the benchmark run then.</summary>
        internal static bool InMatchedMission { get; private set; }

        internal static string CurrentMission { get; private set; }
        internal static string CurrentWeather { get; private set; }

        /// <summary>Intensity used where the rain map is off (UseRainMap = false) or has no data.</summary>
        internal const float FlatIntensity = 0.5f;

        private static readonly string[] ConflictIds = { "neutral.rain", "WeatherController" };
        private static readonly HashSet<string> Warned = new HashSet<string>();
        private readonly List<string> _missionList = new List<string>();
        private float _nextCheck;
        private float _nextForeignCheck;
        private float _nextLogAllowed;
        private string _lastLog;
        private int _settingsVersion;
        private string _keyMission;
        private bool _keyWant;
        private string _keyReason;
        private string _keyWeather;
        private int _keyBase10;
        private bool _keyUsable;
        private bool _keyFading;
        private bool _lightWeather;
        private int _keySettings = -1;
        private Rain _rain;
        private CloudLayer _activeLayer;

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            Enabled = Config.Bind("General", "Enabled", true, "Turn the cloud deck, rain, haze and lightning on.");
            Missions = Config.Bind("General", "Missions", "Cascadia Falling",
                "Only in missions whose name contains one of these (comma-separated). Empty = every mission.");
            CloudTop = Config.Bind("Clouds", "CloudTop", 2400f, new ConfigDescription(
                "Top of the cloud deck, metres above sea level, while the weather is fully wet. The base is the mission's " +
                "cloud altitude; the top is kept at least 300 m above the base, and comes down onto it as the weather clears.",
                new AcceptableValueRange<float>(500f, 12000f)));
            RegionalDeck = Config.Bind("Clouds", "RegionalDeck", true,
                "Make the deck regional from the built-in cloud map (airport weather statistics): the cloud base is lower " +
                "than the mission's on the windward Olympic and Cascade slopes and higher in the Olympic rain shadow and east " +
                "of the Cascade crest, the cloud thins into patches east of the crest (and the stock cloud sheet at the base " +
                "is hidden where the local base is more than 250 m above the mission's), the air under the base is clearer " +
                "in the lee, and the rain map is halved 30 km east of the crest. Off = the same deck everywhere, as 1.0.2.");
            // The visibility ranges are kept narrow on purpose: this is a PvP mission and the fog is per client, so a
            // wide range would let a player tune the weather into an advantage (see README, Multiplayer).
            InCloudVisibility = Config.Bind("Clouds", "InCloudVisibility", 0.5f, new ConfigDescription(
                "Visibility inside the cloud deck, km.", new AcceptableValueRange<float>(0.3f, 1f)));
            FlyThroughInterior = Config.Bind("Clouds", "FlyThroughInterior", 0.3f, new ConfigDescription(
                "Strength of the close cloud wisps deep inside the deck (more than 100-300 m from the base or the top), " +
                "relative to the stock strength at the cloud surfaces. Lower = less GPU fill-rate cost in cloud.",
                new AcceptableValueRange<float>(0f, 1f)));
            CloudParticleBudget = Config.Bind("Clouds", "CloudParticleBudget", 0.5f, new ConfigDescription(
                "Cloud puffs in the deck as a fraction of the stock cap (3000 x sqrt(Cloud Detail)); the spawn rate stays " +
                "the stock one, so the cap is reached at a lower frame rate than in stock. The stock Overcast and " +
                "Thunderstorm sets run at the cap, which is the main cost of the clouds. 1 = stock count. Lower = faster, " +
                "thinner deck (like the game's Cloud Detail slider; for PvP, every player should keep the default).",
                new AcceptableValueRange<float>(0.1f, 1f)));
            PlaneCulling = Config.Bind("Clouds", "PlaneCulling", true,
                "Hide the top cloud plane while the camera is under the base, and the stock (base) plane while the " +
                "camera is over the top, each beyond PlaneCullMargin. Saves two full-screen transparent layers' worth " +
                "of overdraw where the hidden plane is behind the other one.");
            LeeFade = Config.Bind("Clouds", "LeeFade", false,
                "Experimental. Fade the stock cloud plane out through a guessed shader opacity property where the regional deck " +
                "lifts the local base well above the mission base. Off by default until the property is confirmed from the " +
                "'stock cloud plane shader ... properties' log line; when off, that plane is hidden outright instead.");
            PlaneCullMargin = Config.Bind("Clouds", "PlaneCullMargin", 100f, new ConfigDescription(
                "How far below the base (or above the top) the camera must be before the far plane is hidden, metres. " +
                "It comes back 50 m closer (hysteresis).", new AcceptableValueRange<float>(0f, 1000f)));
            BelowBaseVisibility = Config.Bind("Rain", "BelowBaseVisibility", 12f, new ConfigDescription(
                "Visibility under the cloud base in light rain (rain intensity 0.25), km. It shrinks where the rain " +
                "is heavier (about 5 km at intensity 1) and grows where it is drier.", new AcceptableValueRange<float>(6f, 20f)));
            RainEnabled = Config.Bind("Rain", "RainEnabled", true, "Show rain streaks and play the rain sound under the cloud base " +
                "(the rain haze stays when this is off).");
            RainPrewarm = Config.Bind("Rain", "RainPrewarm", true,
                "Fill the rain box with streaks at once when it is empty (rain starting, a teleport or respawn, or the game " +
                "paused) instead of letting it build up over a few seconds, so rain is visible immediately, also in a paused " +
                "photo shoot. Off = the 1.1.x behaviour.");
            AmbientLift = Config.Bind("Light", "AmbientLift", 1.5f, new ConfigDescription(
                "How much brighter the ambient light is under and in the cloud deck, as a multiple of the stock ambient " +
                "intensity (stock switches the sun off there, so the ground is lit by ambient only and looks near black). " +
                "1 = stock. The lift eases in over about 3 s, is strongest where the sun is hidden, fades out in the " +
                "clear air above the deck and at night, and scales with the wetness and the local cloud cover. It is a " +
                "per-client look only (the sun's colour and direction are never touched). The range is kept narrow so the " +
                "setting gives no PvP advantage.", new AcceptableValueRange<float>(1f, 1.8f)));
            RainIntensityScale = Config.Bind("Rain", "RainIntensityScale", 1f, new ConfigDescription(
                "Multiplies the local rain intensity (rain, rain sound and rain haze).", new AcceptableValueRange<float>(0.5f, 2f)));
            UseRainMap = Config.Bind("Rain", "UseRainMap", true,
                "Vary the rain by region from the built-in Cascadia rain map (annual precipitation): heavy on the coast " +
                "and the Olympics, moderate around Puget Sound, light in the rain shadow, almost none east of the " +
                "Cascades. Off = the same moderate rain (0.5) everywhere.");
            MaxRainParticles = Config.Bind("Rain", "MaxRainParticles", 2000, new ConfigDescription(
                "Most rain streaks alive at once (at full intensity).", new AcceptableValueRange<int>(0, 20000)));
            RainMaterialChoice = Config.Bind("Rain", "RainMaterial", "Auto", new ConfigDescription(
                "Which material the rain streaks use. Auto = the cheapest one whose shader variant is known to be in the " +
                "game build (see README). UnlitClone, LitClone = a copy of a game particle material on URP Particles/Unlit " +
                "or any URP particle shader (LitClone is what 1.0.0 used); Unlit = a new Particles/Unlit material; " +
                "Sprite = Sprites/Default. Try another one if the rain draws pink, opaque or not at all. A choice that " +
                "is not available falls back to Auto (the log says so).",
                new AcceptableValueList<string>("Auto", "UnlitClone", "LitClone", "Unlit", "Sprite")));
            RainSoundVolume = Config.Bind("Rain", "RainSoundVolume", 0.5f, new ConfigDescription(
                "Rain sound volume at full intensity (0 = silent). It goes through the game's effects volume.",
                new AcceptableValueRange<float>(0f, 1f)));
            LightningMinInterval = Config.Bind("Lightning", "LightningMinInterval", 90f, new ConfigDescription(
                "Shortest time between lightning strikes, seconds (the stock Thunderstorm strikes every 2-19 s).",
                new AcceptableValueRange<float>(2f, 3600f)));
            LightningMaxInterval = Config.Bind("Lightning", "LightningMaxInterval", 420f, new ConfigDescription(
                "Longest time between lightning strikes, seconds.", new AcceptableValueRange<float>(2f, 7200f)));
            PerfLog = Config.Bind("Performance", "PerfLog", true,
                "Write a performance log (one CSV row per second, a new file per game session, in the BepInEx folder) " +
                "while a mission matching Missions is loaded, whether the deck is on or not. A benchmark starts when " +
                "the file BepInEx/config/noclouddeck.bench appears, or by itself (BenchmarkAutoRuns; see README).");
            BenchmarkInMultiplayer = Config.Bind("Performance", "BenchmarkInMultiplayer", false,
                "Allow the benchmark in a multiplayer match. Several of its phases remove the fog and clouds for a while, " +
                "which shows you what other players cannot see, so leave this off except on a private server.");
            BenchmarkAutoRuns = Config.Bind("Performance", "BenchmarkAutoRuns", 0, new ConfigDescription(
                "Number of game sessions in which the benchmark starts by itself, in a matching mission with the deck " +
                "on: once you have flown level and steady (climb or descent within 10 m/s) at least 100 m above the " +
                "ground for 20 s, with the camera on your aircraft and the map closed (never in multiplayer unless " +
                "BenchmarkInMultiplayer). Each run that finishes counts this down by one and saves it; an aborted run " +
                "does not. Changing any setting of this plugin during a run aborts it. 0 = off.",
                new AcceptableValueRange<int>(0, 100)));
            BenchmarkMinutes = Config.Bind("Performance", "BenchmarkMinutes", 8f, new ConfigDescription(
                "How long a benchmark runs, minutes (automatic or started by the trigger file). The phases repeat in " +
                "whole rounds of about 4 min, and the run ends at the round boundary closest to this time (so 8 gives " +
                "2 rounds, about 8 min); at least one round is always run.", new AcceptableValueRange<float>(2f, 20f)));
            ConfigVersion = Config.Bind("Internal", "ConfigVersion", "",
                "Written by the plugin: the version whose defaults this file has. Do not edit.");
            MigrateConfig();
            ParseMissions();
            Missions.SettingChanged += (_, __) => ParseMissions();
            Config.SettingChanged += (_, __) => _settingsVersion++;

            RainMap.Load();
            CloudMap.Load();
            var harmony = new Harmony(Id);
            Deck.Apply(harmony);
            Atmosphere.Apply(harmony);
            Storm.Apply(harmony);
            try { Perf.Init(); } catch (Exception e) { ErrorOnce("perf-init", "starting the performance log", e); }
            Bench.ClearStaleTrigger();
            Logger.LogInfo("NO Cloud Deck " + Version + " loaded (rain map " + (RainMap.Loaded ? RainMap.Describe() : "missing") +
                           ", cloud map " + (CloudMap.Loaded ? CloudMap.Describe() : "missing") + ")");
        }

        /// <summary>
        /// A config file written by 1.0.0 (no ConfigVersion) holds that version's defaults, which BepInEx keeps over the
        /// new ones. Where a value still equals the old default it is moved to the new default, once, and logged. A value
        /// the player changed is left alone, and so is anything the player sets after this.
        /// </summary>
        private void MigrateConfig()
        {
            string from = ConfigVersion.Value;
            if (from == Version)
                return;
            var moved = new List<string>();
            if (string.IsNullOrEmpty(from) || from == "1.0.0")
            {
                if (Mathf.Approximately(FlyThroughInterior.Value, 0.5f))
                {
                    FlyThroughInterior.Value = (float)FlyThroughInterior.DefaultValue;
                    moved.Add("FlyThroughInterior 0.5 -> " + FlyThroughInterior.Value.ToString("0.##"));
                }
                if (MaxRainParticles.Value == 3000)
                {
                    MaxRainParticles.Value = (int)MaxRainParticles.DefaultValue;
                    moved.Add("MaxRainParticles 3000 -> " + MaxRainParticles.Value);
                }
            }
            ConfigVersion.Value = Version;
            Config.Save();
            if (moved.Count > 0)
                Logger.LogInfo("config from " + (string.IsNullOrEmpty(from) ? "1.0.0" : from) + " updated to the " + Version +
                               " defaults where it still had the old ones: " + string.Join(", ", moved));
        }

        private void ParseMissions()
        {
            _missionList.Clear();
            foreach (string m in Missions.Value.Split(','))
            {
                if (m.Trim().Length > 0)
                    _missionList.Add(m.Trim());
            }
        }

        // ------------------------------------------------------------------------------ wetness, fade, smoothed base

        /// <summary>Seconds the whole mod takes to fade out when the weather drops below the Moderate set.</summary>
        private const float FadeSeconds = 10f;
        /// <summary>Wetness change per second (a step in the mission's weather is eased over a few seconds).</summary>
        private const float WetRate = 0.15f;
        /// <summary>Metres per second the base used by the deck, fog and rain follows the mission's cloud height
        /// (the stock plane itself follows it at once: its transform must equal cloudHeight).</summary>
        private const float BaseRate = 30f;

        private static int _stepFrame = -1;
        private static bool _snap = true;
        private static float _wet;          // eased wetness from the conditions, 0..1
        private static float _fade = 1f;    // 1 normally, 0 when the fade-out is done
        private static float _baseNow = 900f;
        private static bool _fadingOut;

        /// <summary>The weather's wetness from LevelInfo.conditions: 1 at 0.8 and above (the Thunderstorm set), 0 at 0.4
        /// and below (the Scattered set), smooth between. Scales rain, rain sound, haze, in-cloud fog and the puff
        /// density, and brings the deck top down onto the base.</summary>
        internal static float WetnessOf(float conditions)
        {
            float x = Mathf.Clamp01((conditions - 0.4f) / 0.4f);
            return x * x * (3f - 2f * x);
        }

        /// <summary>Eased wetness times the fade-out factor: what the effects use.</summary>
        internal static float Wetness { get { Step(); return _wet * _fade; } }

        /// <summary>Eased wetness without the fade-out factor, for callers that multiply FadeFactor themselves.</summary>
        internal static float WetnessOnly { get { Step(); return _wet; } }

        /// <summary>1 while the mod is fully on, falling to 0 over FadeSeconds once the weather is lighter than Moderate.</summary>
        internal static float FadeFactor { get { Step(); return _fade; } }

        /// <summary>The mission's cloud height, eased (30 m/s) so a ModifyEnvironment step does not make the deck, the
        /// fog or the rain jump. The stock plane and the layer transform use the raw LevelInfo.cloudHeight.</summary>
        internal static float BaseNow { get { Step(); return _baseNow; } }

        /// <summary>Once per frame, from whichever part of the mod needs the values first.</summary>
        internal static void Step()
        {
            int frame = Time.frameCount;
            if (frame == _stepFrame)
                return;
            _stepFrame = frame;
            LevelInfo li = NetworkSceneSingleton<LevelInfo>.i;
            if (li == null)
                return;
            float target = WetnessOf(li.conditions);
            float cloud = li.cloudHeight;
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.25f);
            if (_snap || !Active || Mathf.Abs(cloud - _baseNow) > 400f)
            {
                // not running, just started, or a jump too big to be a step (a new mission, a teleport): no easing
                _wet = target;
                _baseNow = cloud;
                if (!Active || _snap)
                    _fade = 1f;
                _snap = false;
                return;
            }
            _wet = Mathf.MoveTowards(_wet, target, WetRate * dt);
            _baseNow = Mathf.MoveTowards(_baseNow, cloud, BaseRate * dt);
            _fade = Mathf.MoveTowards(_fade, _fadingOut ? 0f : 1f, dt / FadeSeconds);
        }

        /// <summary>Cloud top for a given base (both metres above sea level): CloudTop (at least base + 300) while fully
        /// wet, descending to base + 300 as the wetness falls to 0, and onto the base (40 m) in the last fade-out.</summary>
        internal static float TopFor(float baseHeight)
        {
            Step();
            float full = Mathf.Max(CloudTop.Value, baseHeight + 300f);
            float thick = Mathf.Lerp(300f, full - baseHeight, Mathf.Clamp01(_wet));
            thick = Mathf.Lerp(40f, thick, Mathf.Clamp01(_fade));
            return baseHeight + thick;
        }

        /// <summary>The cloud base at a global x/z for the eased mission base, the current top and the wetness.</summary>
        internal static float LocalBaseAt(float globalX, float globalZ)
        {
            float b = BaseNow;
            return CloudMap.LocalBase(globalX, globalZ, b, TopFor(b), _wet * _fade);
        }

        /// <summary>The rain map at a global x/z (0..2): the Cascadia rain map times the lee scaling (CloudMap.RainScale)
        /// times RainIntensityScale, before the wetness. Where the rain map is off it is the flat 0.5.</summary>
        internal static float RainMapAt(float globalX, float globalZ)
        {
            float i = UseRainMap.Value && RainMap.Loaded ? RainMap.Sample(globalX, globalZ) * CloudMap.RainScale(globalX, globalZ) : FlatIntensity;
            return Mathf.Clamp(i * RainIntensityScale.Value, 0f, 2f);
        }

        /// <summary>Local rain intensity (0..2, about 1 on the wet coast) at a global x/z (metres from the map centre),
        /// times the wetness: it eases to 0 as the weather clears.</summary>
        internal static float RainIntensityAt(float globalX, float globalZ)
        {
            return RainMapAt(globalX, globalZ) * Wetness;
        }

        /// <summary>Write the config file now (after the benchmark counts BenchmarkAutoRuns down).</summary>
        internal static void SaveConfig()
        {
            try
            {
                Instance?.Config.Save();
            }
            catch (Exception e)
            {
                ErrorOnce("config-save", "saving the config file", e);
            }
        }

        /// <summary>Log a warning once per key.</summary>
        internal static void WarnOnce(string key, string message)
        {
            if (Warned.Add(key))
                Log?.LogWarning(message);
        }

        internal static void ErrorOnce(string key, string what, Exception e)
        {
            if (Warned.Add(key))
                Log?.LogError(what + " failed, that part falls back to the stock game: " + e);
        }

        private void Update()
        {
            try
            {
                if (Time.unscaledTime >= _nextCheck)
                {
                    _nextCheck = Time.unscaledTime + 0.5f;
                    Evaluate();
                }
                Step();
                if (Active && _fadingOut && _fade <= 0f)
                {
                    // the fade-out after the weather dropped below the Moderate set is done: the stock clouds take over
                    Logger.LogInfo("cloud deck faded out (weather lighter than Moderate)");
                    StandDown();
                }
            }
            catch (Exception e)
            {
                ErrorOnce("evaluate", "checking the mission", e);
                if (Active)
                    StandDown();
            }
            try
            {
                Bench.Tick();
            }
            catch (Exception e)
            {
                ErrorOnce("bench", "the benchmark", e);
                try { Bench.Abort("error"); } catch (Exception) { }
            }
            try
            {
                Perf.Tick();
            }
            catch (Exception e)
            {
                Perf.Fail(e);
            }
        }

        /// <summary>Re-check the mission now (the benchmark switching the whole mod off or on).</summary>
        internal static void EvaluateNow()
        {
            Plugin p = Instance;
            if (p == null)
                return;
            try
            {
                p._nextCheck = Time.unscaledTime + 0.5f;
                p.Evaluate();
            }
            catch (Exception e)
            {
                ErrorOnce("evaluate", "checking the mission", e);
                if (Active)
                    p.StandDown();
            }
        }

        private void Evaluate()
        {
            string mission = null;
            string weather = null;
            string reason = WhyNot(ref mission, ref weather);
            InMatchedMission = mission != null && !GameManager.IsHeadless && MissionMatches(mission);
            CurrentMission = mission;
            CurrentWeather = weather;
            bool want = reason == null;
            CloudLayer layer = want ? Deck.LayerOf(NetworkSceneSingleton<LevelInfo>.i) : null;
            if (want && (!Active || layer != _activeLayer))
                Activate(layer);
            else if (!want && Active && _lightWeather)
                _fadingOut = true; // lighter than Moderate: fade everything out over FadeSeconds (Update ends it)
            else if (!want && Active)
                StandDown();
            if (want && Active)
                _fadingOut = false;
            if (Active && Rain.Needed && _rain == null)
                _rain = Rain.Create();
            if (Active && !Rain.Needed && _rain != null)
                DestroyRain();
            if (Active && Time.unscaledTime >= _nextForeignCheck)
            {
                // a mod that patches CloudLayer.Update later than our activation is noticed within 30 s
                _nextForeignCheck = Time.unscaledTime + 30f;
                Deck.CheckForeignPatches();
            }
            if (mission != null)
                LogState(mission, want, reason, weather);
        }

        /// <summary>One log line when the state changes. The string is built only when one of the cheap keys changed
        /// (base and top rounded to 10 m), and in the mission editor, where every slider step changes the cloud height,
        /// at most once every 5 s.</summary>
        private void LogState(string mission, bool want, string reason, string weather)
        {
            LevelInfo li = NetworkSceneSingleton<LevelInfo>.i;
            bool fading = !want && Active;
            int base10 = li != null && (want || fading) ? Mathf.RoundToInt(li.cloudHeight / 10f) : int.MinValue;
            bool usable = Deck.Usable;
            if (mission == _keyMission && want == _keyWant && fading == _keyFading && reason == _keyReason && weather == _keyWeather &&
                base10 == _keyBase10 && usable == _keyUsable && _settingsVersion == _keySettings)
                return;
            if (GameManager.gameState == GameState.Editor && Time.unscaledTime < _nextLogAllowed)
                return; // the keys stay stale, so the change is logged when the 5 s are up
            _keyMission = mission;
            _keyWant = want;
            _keyFading = fading;
            _keyReason = reason;
            _keyWeather = weather;
            _keyBase10 = base10;
            _keyUsable = usable;
            _keySettings = _settingsVersion;
            string line = want
                ? "mission \"" + mission + "\": cloud deck on (" + weather + ", " + Settings() + ")"
                : fading
                    ? "mission \"" + mission + "\": cloud deck fading out (" + reason + ")"
                    : "mission \"" + mission + "\": cloud deck off (" + reason + ")";
            if (line == _lastLog)
                return;
            _lastLog = line;
            _nextLogAllowed = Time.unscaledTime + 5f;
            Logger.LogInfo(line);
        }

        private static string Settings()
        {
            LevelInfo li = NetworkSceneSingleton<LevelInfo>.i;
            float b = li != null ? Mathf.Round(li.cloudHeight / 10f) * 10f : float.NaN;
            float t = Deck.Usable ? Mathf.Round(TopFor(b) / 10f) * 10f : b;
            return "base " + b.ToString("0") + " m, top " + t.ToString("0") + " m MSL, wetness " + Wetness.ToString("0.00") +
                   ", regional deck " + (CloudMap.Enabled ? "on" : CloudMap.Loaded ? "off" : "unavailable") + "; ambient lift " + AmbientLift.Value.ToString("0.##") + "x, fog " + Atmosphere.FogModeText() + "; rain " +
                   (RainEnabled.Value ? "on" : "off") + ", map " + (UseRainMap.Value && RainMap.Loaded ? "on" : "off") +
                   ", scale " + RainIntensityScale.Value.ToString("0.00") + ", max " + MaxRainParticles.Value +
                   " streaks, sound " + RainSoundVolume.Value.ToString("0.00") + "; visibility " +
                   BelowBaseVisibility.Value.ToString("0.#") + " km under the base in light rain, " +
                   InCloudVisibility.Value.ToString("0.##") + " km in cloud, wisps " +
                   FlyThroughInterior.Value.ToString("0.##") + " deep in cloud; puffs " +
                   CloudParticleBudget.Value.ToString("0.##") + " x stock; far plane hidden " +
                   (PlaneCulling.Value ? "beyond " + PlaneCullMargin.Value.ToString("0") + " m" : "never") + "; lightning every " +
                   LightningMinInterval.Value.ToString("0") + "-" + Mathf.Max(LightningMinInterval.Value, LightningMaxInterval.Value).ToString("0") + " s" +
                   (Deck.Usable ? "" : "; stock clouds (cloud patch unavailable)");
        }

        /// <summary>Null when the plugin should be active, else the reason it is not.</summary>
        private string WhyNot(ref string mission, ref string weather)
        {
            if (GameManager.IsHeadless)
            {
                if (Warned.Add("headless"))
                    Log.LogInfo("headless server: NO Cloud Deck does nothing here (it is purely visual)");
                return "headless server";
            }
            GameState state = GameManager.gameState;
            if (state != GameState.SinglePlayer && state != GameState.Multiplayer && state != GameState.Editor)
                return "not in a mission";
            mission = MissionManager.CurrentMission?.Name;
            if (string.IsNullOrEmpty(mission))
            {
                mission = null;
                return "no mission";
            }
            if (!Enabled.Value)
                return "disabled in the config";
            foreach (string id in ConflictIds)
            {
                if (Chainloader.PluginInfos.ContainsKey(id))
                {
                    WarnOnce("conflict:" + id, "another weather mod is loaded (" + id + "); NO Cloud Deck stands down to leave the weather to it");
                    return "another weather mod is loaded: " + id;
                }
            }
            if (!MissionMatches(mission))
                return "not one of the configured missions";
            LevelInfo li = NetworkSceneSingleton<LevelInfo>.i;
            if (li == null)
                return "no level loaded";
            CloudLayer layer = Deck.LayerOf(li);
            if (layer == null)
                return "no cloud layer in this scene";
            bool deckWeather = Deck.IsDeckWeather(layer, li, out weather);
            _lightWeather = false;
            if (Bench.ModOff)
                return "benchmark phase with the mod off";
            if (!deckWeather)
            {
                _lightWeather = true; // the one reason that fades out instead of standing down at once
                return "weather is " + weather + ", lighter than Moderate";
            }
            return null;
        }

        private bool MissionMatches(string name)
        {
            if (_missionList.Count == 0)
                return true;
            foreach (string m in _missionList)
            {
                if (name.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private void Activate(CloudLayer layer)
        {
            if (Active)
                StandDown();
            _activeLayer = layer;
            // a failure in an earlier mission (logged once) does not keep the feature off for the rest of the session
            Deck.ResetFailure();
            Atmosphere.ResetFailure();
            Storm.ResetFailure();
            Deck.CheckForeignPatches();
            _nextForeignCheck = Time.unscaledTime + 30f;
            _fadingOut = false;
            _snap = true;
            Active = true;
        }

        private void StandDown()
        {
            Active = false;
            _fadingOut = false;
            DestroyRain();
            try { Deck.StandDown(_activeLayer); } catch (Exception e) { ErrorOnce("deck-standdown", "restoring the stock clouds", e); }
            try { Atmosphere.StandDown(); } catch (Exception e) { ErrorOnce("atmo-standdown", "restoring the stock fog", e); }
            try { Storm.StandDown(); } catch (Exception e) { ErrorOnce("storm-standdown", "restoring the stock lightning", e); }
            _activeLayer = null;
        }

        private void DestroyRain()
        {
            if (_rain != null)
            {
                try { Destroy(_rain.gameObject); } catch (Exception) { }
            }
            _rain = null;
        }

        private void OnDestroy()
        {
            // no re-evaluation during teardown: it could switch the deck back on and create objects while unloading
            try { Bench.Abort("plugin unloading", false); } catch (Exception) { }
            if (Active)
                StandDown();
            try { Perf.Close(); } catch (Exception) { }
        }

        private void OnApplicationFocus(bool focused)
        {
            if (!focused)
            {
                try { Bench.NoteFocusLost(); } catch (Exception) { }
            }
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused)
            {
                try { Bench.NoteFocusLost(); } catch (Exception) { }
            }
        }

        private void OnApplicationQuit()
        {
            try { Bench.Abort("game closing", false); } catch (Exception) { }
            try { Perf.Close(); } catch (Exception) { }
        }
    }
}
