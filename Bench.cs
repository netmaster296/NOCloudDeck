using System;
using System.IO;
using BepInEx;
using UnityEngine;

namespace CloudDeck
{
    /// <summary>
    /// Automatic A/B benchmark. It starts in one of two ways, in a matched mission with the deck on that has been loaded
    /// for at least 10 s:
    ///  - by itself, once per game session while Performance.BenchmarkAutoRuns is above 0: once the local player has
    ///    flown level and steady (climb or descent within 10 m/s) at least 100 m above the ground for 20 s, in their own
    ///    aircraft, with the camera on it and the map closed. The countdown starts over whenever that stops holding;
    ///  - when the file BepInEx/config/noclouddeck.bench appears (polled once a second; deleted when it is found).
    /// It steps through the phases below in whole rounds, and ends at the round boundary closest to
    /// Performance.BenchmarkMinutes (at least one round). Each phase is a client-visual change only:
    /// renderer switches, the mod's own puff budget, the mod's fly-through, rain and fog overrides, or the whole mod
    /// standing down to the stock thunderstorm. The config file is not changed, except that a completed automatic run
    /// counts BenchmarkAutoRuns down by one. Each phase settles (2 s, longer where the cloud system has to refill or turn
    /// over), then measures 15 s; the measured window gives one "bench" row in the CSV and one line in the BepInEx log.
    /// At the end, "summary" rows and log lines pool every window of a phase, per camera zone and over all zones.
    /// Afterwards, or when the run is aborted, every override is cleared and every renderer is put back as it was.
    ///
    /// Several phases remove the mod's haze or occluders, so the benchmark refuses to start in a multiplayer match
    /// unless Performance.BenchmarkInMultiplayer is set (for a private server).
    ///
    /// A window is only kept when nothing disturbed it: if the game loses focus or is paused, the map is opened or
    /// closed, or the camera stops following the unit it followed at the start, the phase settles and measures again.
    /// If the deck stands down, the weather or the cloud base changes, a setting changes, or the player leaves, loses
    /// or changes the aircraft the run started in, the run is aborted, because every later phase would measure
    /// something else.
    ///
    /// The flags below are read by Deck, Rain, Atmosphere and Plugin. Nothing here writes a SyncVar or touches the
    /// cloud layer transform, the sun, or GetCloudOcclusion.
    /// </summary>
    internal static class Bench
    {
        internal const int WindowCapacity = 16384;
        private const float MeasureSeconds = 15f;
        private const float SettleSeconds = 2f;
        private const float FillMaxSeconds = 12f;   // a refilling phase settles until the puffs are at 95 % of the cap, or this
        private const float StockTurnover = 16f;    // puffs live 15 s: let the ones placed by the other cloud mode die out
        private const float GraceAfterLoad = 10f;   // a start waits this long after the level appeared
        private const float AutoDelay = 20f;        // an automatic run starts after the player has flown steady this long
        private const float AutoMinRadarAlt = 100f; // m above the ground or the sea, for the automatic start
        private const float AutoMaxClimb = 10f;     // m/s of climb or descent, over the last second, for the automatic start
        private const float AutoFirstWaitLog = 60f; // "still waiting" is logged this long after the level appeared
        private const float AutoWaitLogEvery = 120f; // and again this often
        private const float AutoResetLogEvery = 30f; // "countdown stopped" at most this often
        private const float AutoRefuseRetry = 10f;  // the deck being off at the end of the countdown is re-checked this long
        private const float MaxDisturbedSeconds = 120f;
        private const int MaxRestarts = 5;

        private static readonly string[] PhaseNames =
        {
            "all-on", "top-plane-hidden", "puffs-hidden", "flythrough-off", "distant-hidden", "rain-off", "fog-stock",
            "budget-50", "budget-25", "budget-100", "mod-off", "stock-no-puffs-no-distant",
        };
        private const int PhaseModOff = 10; // the first stock phase: its puffs were placed by the deck and must turn over
        private static readonly string[] SettleLabels = new string[PhaseNames.Length];

        // overrides read by the other classes
        internal static bool Running;
        internal static string PhaseLabel = "";
        internal static bool HideTop;
        internal static bool NoFlyThrough;
        internal static bool NoRain;
        internal static bool StockFog;
        internal static bool ModOff;
        internal static bool Filling;
        internal static float Budget = -1f;

        private static int _phase;
        private static int _round;          // completed rounds; the phase going on is in round _round + 1
        private static float _roundStart;
        private static float _runStart;
        private static float _runSeconds;
        private static float _phaseStart;
        private static bool _measuring;
        private static float _measureStart;
        private static int _measureFrame;
        private static LevelInfo _li;
        private static CloudLayer _layer;
        private static float _nextPoll;
        private static double _baseFps = double.NaN;
        private static LevelInfo _levelSeen;
        private static float _levelSeenAt;

        // the automatic start
        private static bool _autoUsed;          // the automatic run has started in this game session
        private static bool _autoThisRun;       // the run going on was started automatically
        private static bool _autoLevelOff;      // no automatic start in this level (refused, or a run was started by hand)
        private static bool _autoAnnounced;     // the mission-start line was logged for this level
        private static float _autoAt = float.NaN; // when the automatic run starts; NaN while the player is not flying steady
        private static Aircraft _autoSampled;   // the aircraft the climb-rate sample belongs to
        private static float _autoLastY = float.NaN;
        private static float _autoLastYAt;
        private static float _autoNextSample;
        private static bool _autoLevel;         // the last 1 s climb-rate sample was within AutoMaxClimb
        private static float _autoNextWaitLog;
        private static float _autoNextResetLog;
        private static float _autoRefusedSince = float.NaN;

        // what the run started with; a change aborts it
        private static bool _startUsable;
        private static int _startWeather;
        private static float _startBase;
        private static int _startSettings;
        private static bool _startHadAircraft;
        private static Aircraft _startAircraft;

        // the view the run started with; a change discards the window (it measures again)
        private static Unit _startFollowing;
        private static bool _startMap;

        // interruptions of the current phase
        private static int _restarts;
        private static string _restartWhy;
        private static float _disturbedSince = float.NaN;

        // renderers this class switched off, and their state before
        private static ParticleSystemRenderer _puffRenderer;
        private static bool _puffWas;
        private static ParticleSystemRenderer _distantRenderer;
        private static bool _distantWas;

        // the measured window
        private static readonly float[] _buf = new float[WindowCapacity];
        private static int _stored;
        private static int _frames;
        private static double _sum;
        private static float _max;
        private static readonly double[] _sums = new double[Perf.Count];
        private static readonly int[] _counts = new int[Perf.Count];
        private static readonly int[] _zones = new int[3];
        private static int _gcStart;
        private static readonly double[] _out = new double[Perf.Count];

        // pooled over the run, per (phase, zone bucket): every window's frame count and time, and a histogram of every
        // frame delta stored in a window (0.1 ms bins up to 250 ms, then one bin for anything longer). Preallocated
        // once (12 x 4 x 2501 ints, about 480 KB); filled at the end of each window, outside it.
        private const int ZoneBuckets = 4;          // below, in, above, unknown (no camera in the window)
        private static readonly string[] BucketNames = { "below", "in", "above", "unknown" };
        private const int Bins = 2500;              // 0.1 ms each
        private const int BinSlots = Bins + 1;      // the last one holds deltas of 250 ms and more
        private const double BinMs = 0.1;
        private static readonly int[] _hist = new int[PhaseNames.Length * ZoneBuckets * BinSlots];
        private static readonly int[] _poolWindows = new int[PhaseNames.Length * ZoneBuckets];
        private static readonly long[] _poolFrames = new long[PhaseNames.Length * ZoneBuckets];
        private static readonly double[] _poolSum = new double[PhaseNames.Length * ZoneBuckets];
        private static readonly long[] _poolBinned = new long[PhaseNames.Length * ZoneBuckets];
        private static readonly float[] _poolMax = new float[PhaseNames.Length * ZoneBuckets];
        private static readonly int[] _allHist = new int[BinSlots]; // scratch: one phase over every zone
        private static int _windowsTotal;

        static Bench()
        {
            for (int i = 0; i < PhaseNames.Length; i++)
                SettleLabels[i] = PhaseNames[i] + " (settle)";
        }

        internal static string TriggerPath => Path.Combine(Paths.ConfigPath, "noclouddeck.bench");

        /// <summary>The round the phase going on belongs to, from 1.</summary>
        internal static int RoundNumber => _round + 1;

        /// <summary>One round of phases with the shortest settles, s (the fill phases can add up to 2 x 12 s).</summary>
        internal static float RoundSecondsEstimate =>
            PhaseNames.Length * (SettleSeconds + MeasureSeconds) + (StockTurnover - SettleSeconds);

        private static float RunMinutes => Mathf.Clamp(Plugin.BenchmarkMinutes.Value, 2f, 20f);

        /// <summary>A trigger file left from an earlier session (or created while no run could start) is deleted
        /// without starting a run. Called at plugin load.</summary>
        internal static void ClearStaleTrigger()
        {
            try
            {
                string path = TriggerPath;
                if (!File.Exists(path))
                    return;
                File.Delete(path);
                Plugin.Log?.LogInfo("a benchmark trigger file left from an earlier session was deleted, not acted on; " +
                                    "create it again during a mission to run the benchmark");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning("a benchmark trigger file left from an earlier session could not be deleted (" + e.Message +
                                       "); it will start a run in the next matching mission unless you delete it");
            }
        }

        /// <summary>Called every frame from Plugin.Update, before Perf.Tick.</summary>
        internal static void Tick()
        {
            float now = Time.realtimeSinceStartup;
            if (!Running)
            {
                LevelInfo level = Plugin.InMatchedMission ? NetworkSceneSingleton<LevelInfo>.i : null;
                if (level == null)
                {
                    _levelSeen = null;
                    return;
                }
                if (!ReferenceEquals(level, _levelSeen))
                {
                    _levelSeen = level;
                    _levelSeenAt = now;
                    _autoLevelOff = false;
                    _autoAnnounced = false;
                    _autoAt = float.NaN;
                    _autoSampled = null;
                    _autoLastY = float.NaN;
                    _autoLevel = false;
                    _autoRefusedSince = float.NaN;
                    _autoNextResetLog = 0f;
                }
                // the automatic start has its own guard: an error in it switches it off for this level and leaves the
                // trigger file working, instead of throwing every frame
                bool autoStart;
                try
                {
                    autoStart = AutoTick(now);
                }
                catch (Exception e)
                {
                    autoStart = false;
                    _autoLevelOff = true;
                    _autoAt = float.NaN;
                    Plugin.ErrorOnce("bench-auto", "the automatic benchmark start (off for this mission; the trigger file still works)", e);
                }
                if (autoStart)
                {
                    _autoUsed = true;
                    Start(now, true);
                    return;
                }
                if (now < _nextPoll || now - _levelSeenAt < GraceAfterLoad)
                    return; // in the first seconds the clouds are still filling in; a trigger waits for them
                _nextPoll = now + 1f;
                string path = TriggerPath;
                if (!File.Exists(path))
                    return;
                string refuse = WhyNotStart();
                if (!TryDeleteTrigger(path, now))
                    return;
                if (refuse != null)
                {
                    Plugin.Log?.LogWarning("benchmark not started: " + refuse);
                    Perf.Event("benchmark not started: " + refuse);
                    return;
                }
                if (!_autoUsed && !_autoLevelOff && Plugin.BenchmarkAutoRuns.Value > 0)
                    Plugin.Log?.LogInfo("benchmark started by the trigger file, so the automatic start is skipped in this " +
                                        "mission (BenchmarkAutoRuns stays " + Plugin.BenchmarkAutoRuns.Value + ")");
                _autoLevelOff = true;
                Start(now, false);
                return;
            }

            string why = AbortReason();
            if (why != null)
            {
                Finish("aborted (" + why + ")", true, false);
                return;
            }

            // a window the game was not drawing normally in is thrown away and measured again
            string bad = Disturbance();
            if (bad != null)
            {
                if (float.IsNaN(_disturbedSince))
                    _disturbedSince = now;
                else if (now - _disturbedSince > MaxDisturbedSeconds)
                {
                    Finish("aborted (" + bad + " for over " + MaxDisturbedSeconds.ToString("0") + " s)", true, false);
                    return;
                }
                if (_measuring)
                {
                    _measuring = false;
                    _restarts++;
                    _restartWhy = bad;
                    PhaseLabel = SettleLabels[_phase];
                    if (_restarts > MaxRestarts)
                    {
                        Finish("aborted (phase " + PhaseNames[_phase] + " was interrupted " + _restarts + " times: " + bad + ")", true, false);
                        return;
                    }
                    Plugin.Log?.LogInfo("bench " + PhaseNames[_phase] + ": " + bad + "; this phase measures again once the game is back");
                }
                _phaseStart = now; // settle again from the moment it is back
                return;
            }
            _disturbedSince = float.NaN;

            float elapsed = now - _phaseStart;
            if (!_measuring)
            {
                float settle = SettleFor(_phase, _round);
                bool settled = elapsed >= settle;
                if (settled && Filling && elapsed < settle + FillMaxSeconds && !PuffsFull())
                    settled = false;
                if (settled)
                {
                    Filling = false;
                    _measuring = true;
                    _measureStart = now;
                    _measureFrame = Time.frameCount;
                    PhaseLabel = PhaseNames[_phase];
                    ResetWindow();
                }
            }
            else if (now - _measureStart >= MeasureSeconds)
            {
                EndPhase(now);
                _phase++;
                if (_phase >= PhaseNames.Length)
                {
                    _phase = 0;
                    _round++;
                    // whole rounds only, so every phase has the same number of windows: stop at this round boundary
                    // unless the next one is closer to BenchmarkMinutes. The next round is estimated from this one (the
                    // first round has no long all-on settle; the later ones do).
                    float next = now - _roundStart + (_round == 1 ? StockTurnover - SettleSeconds : 0f);
                    _roundStart = now;
                    if (now - _runStart + 0.5f * next >= _runSeconds)
                    {
                        Finish("finished", true, true);
                        return;
                    }
                }
                BeginPhase(now);
            }
        }

        /// <summary>Settle time before a phase measures. mod-off, and all-on after the first round (it follows the stock
        /// phases), wait for the puffs placed by the other cloud mode to die out.</summary>
        private static float SettleFor(int phase, int round)
        {
            return phase == PhaseModOff || (phase == 0 && round > 0) ? StockTurnover : SettleSeconds;
        }

        // ------------------------------------------------------------------------------------------------- auto start

        /// <summary>
        /// The automatic start: true when a run should start now (the caller starts it). Runs every frame until then;
        /// the per-frame path reads a few fields and returns constant strings, so it allocates nothing. Log lines are
        /// written only on a change, and the "still waiting" line at most once every 2 min.
        /// </summary>
        private static bool AutoTick(float now)
        {
            if (_autoUsed || _autoLevelOff)
                return false;
            int left = Plugin.BenchmarkAutoRuns.Value;
            if (left <= 0)
                return false;
            if (!_autoAnnounced)
            {
                if (now - _levelSeenAt < 0.5f)
                    return false; // Perf opens the CSV after this in the first frame; the event row should land in it
                _autoAnnounced = true;
                _autoNextWaitLog = _levelSeenAt + AutoFirstWaitLog;
                if (GameManager.gameState == GameState.Multiplayer && !Plugin.BenchmarkInMultiplayer.Value)
                {
                    _autoLevelOff = true;
                    string mp = "automatic benchmark not started: this is a multiplayer match (it removes the fog and " +
                                "clouds for a while). BenchmarkAutoRuns stays " + left;
                    Plugin.Log?.LogInfo(mp);
                    Perf.Event(mp);
                    return false;
                }
                string msg = "benchmark starts automatically once you have flown level and steady (climb or descent within " +
                             AutoMaxClimb.ToString("0") + " m/s) at least " + AutoMinRadarAlt.ToString("0") + " m above the " +
                             "ground for " + AutoDelay.ToString("0") + " s in this mission, with the camera on your aircraft " +
                             "and the map closed, and runs about " + RunMinutes.ToString("0.#") + " min (BenchmarkAutoRuns = " +
                             left + ")";
                string refuse = WhyNotStart();
                if (refuse != null)
                    msg += ". It cannot start now: " + refuse;
                Plugin.Log?.LogInfo(msg);
                Perf.Event(msg);
            }

            string status = FlightStatus(now);
            if (status != null)
            {
                _autoRefusedSince = float.NaN;
                if (!float.IsNaN(_autoAt))
                {
                    _autoAt = float.NaN;
                    _autoNextWaitLog = Mathf.Max(_autoNextWaitLog, now + AutoWaitLogEvery);
                    if (now >= _autoNextResetLog)
                    {
                        _autoNextResetLog = now + AutoResetLogEvery;
                        string msg = "automatic benchmark countdown stopped: " + status + ". It counts " +
                                     AutoDelay.ToString("0") + " s again once you are flying level and steady";
                        Plugin.Log?.LogInfo(msg);
                        Perf.Event(msg);
                    }
                }
                else if (now >= _autoNextWaitLog)
                {
                    _autoNextWaitLog = now + AutoWaitLogEvery;
                    string msg = "automatic benchmark still waiting: " + status + ". It starts " + AutoDelay.ToString("0") +
                                 " s after you fly level and steady (climb or descent within " + AutoMaxClimb.ToString("0") +
                                 " m/s) at least " + AutoMinRadarAlt.ToString("0") + " m above the ground, with the camera " +
                                 "on your aircraft and the map closed";
                    Plugin.Log?.LogInfo(msg);
                    Perf.Event(msg);
                }
                return false;
            }

            if (float.IsNaN(_autoAt))
            {
                _autoAt = Mathf.Max(now + AutoDelay, _levelSeenAt + GraceAfterLoad);
                string msg = "benchmark starts automatically in about " + Mathf.RoundToInt(_autoAt - now) + " s and runs about " +
                             RunMinutes.ToString("0.#") + " min: keep flying level and steady (autopilot works well), keep " +
                             "the camera on your aircraft and the game window in front. The countdown starts over if you " +
                             "climb or descend, go below " + AutoMinRadarAlt.ToString("0") + " m above the ground, open the " +
                             "map or leave the aircraft";
                Plugin.Log?.LogInfo(msg);
                Perf.Event(msg);
                return false;
            }
            if (now < _autoAt)
                return false;

            string why = WhyNotStart();
            if (why != null)
            {
                // the deck being off can be a moment in a re-evaluation: re-checked for a few seconds. A multiplayer
                // refusal or a failed log does not go away.
                bool lasting = Perf.Failed ||
                               (GameManager.gameState == GameState.Multiplayer && !Plugin.BenchmarkInMultiplayer.Value);
                if (!lasting)
                {
                    if (float.IsNaN(_autoRefusedSince))
                        _autoRefusedSince = now;
                    if (now - _autoRefusedSince < AutoRefuseRetry)
                        return false;
                }
                _autoLevelOff = true;
                _autoAt = float.NaN;
                Plugin.Log?.LogWarning("automatic benchmark not started in this mission: " + why + ". BenchmarkAutoRuns stays " + left);
                Perf.Event("automatic benchmark not started: " + why);
                return false;
            }
            _autoRefusedSince = float.NaN;
            return true;
        }

        /// <summary>
        /// Null when the local player is flying steady enough for the automatic start, else why not (a constant string,
        /// no allocation).
        ///  - GameManager.GetLocalAircraft returns the local Player's Aircraft (GameManager.cs:118-127; set by
        ///    Player.SetAircraft from Aircraft.OnStartServer/OnStartClient, Aircraft.cs:1143 and :1191, and cleared by
        ///    Player.RemoveAircraft when the pilot dies, Aircraft.cs:268).
        ///  - The main camera follows that aircraft (CameraStateManager.followingUnit, CameraStateManager.cs:129, set by
        ///    Aircraft.SetupLocalPlayerAndUI together with the cockpit view, Aircraft.cs:1287-1288). That happens at
        ///    spawn, on the ground too, so it is not enough on its own.
        ///  - Airborne: Unit.radarAlt (Unit.cs:241; the height above the ground, ships or sea, refreshed by
        ///    Aircraft.CheckRadarAlt, Aircraft.cs:1805-1818) at least AutoMinRadarAlt, and not Aircraft.IsLanded().
        ///  - Steady: the global height (GlobalY, so a floating-origin shift does not count) changed by at most
        ///    AutoMaxClimb m/s over the last second. This keeps the countdown from running through a take-off and climb.
        ///  - Not disabled or ejected, the map not open, the game not paused.
        /// </summary>
        private static string FlightStatus(float now)
        {
            if (!GameManager.GetLocalAircraft(out Aircraft aircraft) || aircraft == null)
            {
                _autoSampled = null;
                return "you are not in an aircraft";
            }
            if (!ReferenceEquals(aircraft, _autoSampled))
            {
                _autoSampled = aircraft;
                _autoLastY = float.NaN;
                _autoLevel = false;
                _autoNextSample = now;
            }
            if (now >= _autoNextSample)
            {
                float y = aircraft.transform.position.GlobalY();
                float dt = now - _autoLastYAt;
                _autoLevel = !float.IsNaN(_autoLastY) && dt > 0.5f && Mathf.Abs(y - _autoLastY) / dt <= AutoMaxClimb;
                _autoLastY = y;
                _autoLastYAt = now;
                _autoNextSample = now + 1f;
            }
            if (aircraft.disabled || aircraft.HasEjected())
                return "your aircraft is disabled or you ejected";
            CameraStateManager csm = SceneSingleton<CameraStateManager>.i;
            if (csm == null || csm.followingUnit == null || !ReferenceEquals(csm.followingUnit, aircraft))
                return "the camera is not on your aircraft";
            if (DynamicMap.mapMaximized)
                return "the map is open";
            if (GameManager.gameState != GameState.Editor && Time.timeScale <= 0f)
                return "the game is paused";
            if (aircraft.radarAlt < AutoMinRadarAlt || aircraft.IsLanded())
                return "you are on the ground or less than 100 m above it";
            if (!_autoLevel)
                return "you are climbing or descending (more than 10 m/s)";
            return null;
        }

        /// <summary>Null when a run can start now, else why not.</summary>
        private static string WhyNotStart()
        {
            if (GameManager.gameState == GameState.Multiplayer && !Plugin.BenchmarkInMultiplayer.Value)
                return "this is a multiplayer match. The benchmark removes the fog and clouds for a while, which would show " +
                       "you what other players cannot see. On a private server, set Performance.BenchmarkInMultiplayer = true";
            if (Perf.Failed)
                return "the performance log failed earlier in this session (see the error above)";
            if (!Plugin.Active)
                return "the cloud deck is not on in this mission (see the 'cloud deck off' line), so every phase would " +
                       "measure the same stock scene";
            return null;
        }

        private static bool TryDeleteTrigger(string path, float now)
        {
            try
            {
                File.Delete(path);
                return true;
            }
            catch (Exception e)
            {
                // acting on it without deleting it would start again after every run; retried every 30 s
                _nextPoll = now + 30f;
                Plugin.Log?.LogWarning("benchmark trigger found but it could not be deleted (" + e.Message + "); not starting, " +
                                       "trying again in 30 s");
                return false;
            }
        }

        /// <summary>Why the run cannot go on, or null.</summary>
        private static string AbortReason()
        {
            LevelInfo li = NetworkSceneSingleton<LevelInfo>.i;
            if (!Plugin.InMatchedMission)
                return "the mission ended";
            if (li == null || !ReferenceEquals(li, _li))
                return "the level changed";
            if (Perf.Failed)
                return "the performance log failed";
            if (Plugin.SettingsVersion != _startSettings)
                return "a setting changed during the run";
            if (Deck.WeatherIndex(_layer, li) != _startWeather)
                return "the weather changed";
            if (Mathf.Abs(li.cloudHeight - _startBase) > 1f)
                return "the cloud base changed";
            // the aircraft the run started in: losing or changing it does not come back, so it ends the run
            bool has = GameManager.GetLocalAircraft(out Aircraft aircraft) && aircraft != null;
            if (_startHadAircraft)
            {
                if (!has)
                    return "you left the aircraft the run started in (the pilot died or it was removed)";
                if (!ReferenceEquals(aircraft, _startAircraft))
                    return "you changed aircraft";
                if (aircraft.disabled || aircraft.HasEjected())
                    return "your aircraft was disabled or you ejected";
            }
            else if (has)
            {
                return "you took control of an aircraft (the run started without one, so the view changed)";
            }
            if (_phase < PhaseModOff)
            {
                if (!Plugin.Active)
                    return "the cloud deck stood down (see the log)";
                if (Deck.Usable != _startUsable)
                    return "the cloud replacement stopped (see the log)";
            }
            else if (Plugin.Active)
            {
                return "the mod came back on during a mod-off phase";
            }
            return null;
        }

        private static bool _focusLost;

        /// <summary>From Plugin.OnApplicationFocus(false) / OnApplicationPause(true). When the game does not run in the
        /// background, no Update runs while it is unfocused, so isFocused is never seen false; the event still marks it,
        /// and the first frame back (whose delta spans the whole gap) is not measured.</summary>
        internal static void NoteFocusLost()
        {
            if (Running)
                _focusLost = true;
        }

        /// <summary>A reason the frames now do not show the normal cost, or null.</summary>
        private static string Disturbance()
        {
            if (_focusLost)
            {
                _focusLost = false;
                return "the game window lost focus";
            }
            if (!Application.isFocused)
                return "the game window lost focus";
            if (GameManager.gameState != GameState.Editor && Time.timeScale <= 0f)
                return "the game is paused";
            if (DynamicMap.mapMaximized != _startMap)
                return _startMap ? "the map was closed (it was open at the start)" : "the map is open";
            CameraStateManager csm = SceneSingleton<CameraStateManager>.i;
            Unit following = csm != null ? csm.followingUnit : null;
            bool followingNull = following == null;
            bool startNull = _startFollowing == null;
            if (followingNull != startNull || (!followingNull && !ReferenceEquals(following, _startFollowing)))
                return "the camera is not on the unit it followed at the start";
            return null;
        }

        /// <summary>One frame's delta, from Perf.Tick.</summary>
        internal static void Frame(float dt)
        {
            if (!Running || !_measuring)
                return;
            if (Time.frameCount == _measureFrame)
                return; // this delta is the time before the window opened
            _frames++;
            _sum += dt;
            if (dt > _max)
                _max = dt;
            if (_stored < WindowCapacity)
                _buf[_stored++] = dt;
        }

        /// <summary>One per-second row's values, from Perf.Tick. secondStart is when that row's second began; a row that
        /// began before the window opened (partly settle time) is left out.</summary>
        internal static void OnRow(double[] v, float secondStart)
        {
            if (!Running || !_measuring || secondStart < _measureStart)
                return;
            for (int c = Perf.FtmCpu; c < Perf.Count; c++)
            {
                if (c == Perf.Gc || c == Perf.Round)
                    continue; // gc counted exactly over the window (ResetWindow, EndPhase); the round is set in EndPhase
                double x = v[c];
                if (double.IsNaN(x))
                    continue;
                if (c == Perf.Zone)
                {
                    _zones[Mathf.Clamp((int)x, 0, 2)]++;
                    continue;
                }
                _sums[c] += x;
                _counts[c]++;
            }
        }

        /// <summary>Stop a running benchmark and restore everything. reevaluate = false during teardown (plugin unload,
        /// game quit), where the mod must not be switched back on.</summary>
        internal static void Abort(string reason, bool reevaluate = true)
        {
            if (Running)
                Finish("aborted (" + reason + ")", reevaluate, false);
        }

        // ------------------------------------------------------------------------------------------------- phases

        private static void Start(float now, bool auto)
        {
            _li = NetworkSceneSingleton<LevelInfo>.i;
            _layer = Deck.LayerOf(_li);
            _startUsable = Deck.Usable;
            _startWeather = Deck.WeatherIndex(_layer, _li);
            _startBase = _li.cloudHeight;
            _startSettings = Plugin.SettingsVersion;
            _startHadAircraft = GameManager.GetLocalAircraft(out Aircraft aircraft) && aircraft != null;
            _startAircraft = _startHadAircraft ? aircraft : null;
            CameraStateManager csm = SceneSingleton<CameraStateManager>.i;
            _startFollowing = csm != null && csm.followingUnit != null ? csm.followingUnit : null;
            _startMap = DynamicMap.mapMaximized;
            _disturbedSince = float.NaN;
            _focusLost = false;
            _autoThisRun = auto;
            _runStart = now;
            _roundStart = now;
            _runSeconds = RunMinutes * 60f;
            _round = 0;
            ResetPool();
            Running = true;
            _phase = 0;
            _baseFps = double.NaN;
            Plugin.Log?.LogInfo("benchmark started " + (auto ? "automatically" : "by the trigger file") + ": " +
                                PhaseNames.Length + " phases a round (a round takes about " +
                                (RoundSecondsEstimate / 60f).ToString("0.0") + " min), repeated in whole rounds for about " +
                                RunMinutes.ToString("0.#") + " min (it ends at the round boundary closest to that, after at " +
                                "least one round). The clouds, rain and fog change every 17-31 s or so. Keep the view steady " +
                                "(same heading, height and camera), the map closed and the game window in front until it " +
                                "says it has finished; leaving or changing your aircraft ends the run. Rows go to " +
                                (Perf.FilePath != null && Plugin.PerfLog.Value ? Perf.FilePath : "the BepInEx log only (PerfLog is off)"));
            if (!_startUsable)
                Plugin.Log?.LogWarning("benchmark: the cloud replacement is not running (see the log), so the top-plane and " +
                                       "budget phases measure the stock clouds");
            Perf.Event("benchmark start (" + (auto ? "automatic" : "trigger file") + ", " + RunMinutes.ToString("0.#") +
                       " min; " + string.Join(" ", PhaseNames) + ")");
            BeginPhase(now);
        }

        private static void BeginPhase(float now)
        {
            ClearOverrides();
            switch (_phase)
            {
                case 1:
                    HideTop = true;
                    break;
                case 2:
                    HidePuffs();
                    break;
                case 3:
                    NoFlyThrough = true;
                    break;
                case 4:
                    HideDistant();
                    break;
                case 5:
                    NoRain = true;
                    break;
                case 6:
                    StockFog = true;
                    break;
                case 7:
                    Budget = 0.5f;
                    Filling = true;
                    break;
                case 8:
                    Budget = 0.25f;
                    break;
                case 9:
                    Budget = 1f;
                    Filling = true;
                    break;
                case 10:
                    ModOff = true;
                    break;
                case 11:
                    ModOff = true;
                    HidePuffs();
                    HideDistant();
                    break;
            }
            _phaseStart = now;
            _measuring = false;
            _restarts = 0;
            _restartWhy = null;
            PhaseLabel = SettleLabels[_phase];
            Plugin.EvaluateNow(); // the whole-mod phases take effect this frame, not at the next 0.5 s check
            // the file is written now, in the settle time, so the periodic flush (paused while a run goes) never lands
            // in a measured window
            Perf.Flush();
        }

        private static void EndPhase(float now)
        {
            double[] v = _out;
            for (int i = 0; i < v.Length; i++)
                v[i] = double.NaN;
            v[Perf.T] = now;
            v[Perf.Frames] = _frames;
            if (_frames > 0 && _sum > 0)
            {
                v[Perf.Fps] = _frames / _sum;
                v[Perf.FtAvg] = _sum / _frames * 1000.0;
                Perf.HeapSort(_buf, _stored);
                v[Perf.FtMed] = Perf.Median(_buf, _stored) * 1000.0;
                v[Perf.FtP95] = Perf.Percentile(_buf, _stored, 0.95) * 1000.0;
                v[Perf.FtMax] = _max * 1000.0;
            }
            for (int c = Perf.FtmCpu; c < Perf.Count; c++)
            {
                if (_counts[c] > 0)
                    v[c] = _sums[c] / _counts[c];
            }
            v[Perf.Gc] = GC.CollectionCount(0) - _gcStart; // collections during the window, not the cumulative count
            v[Perf.Round] = RoundNumber;
            int zone = -1;
            for (int z = 0; z < 3; z++)
            {
                if (_zones[z] > 0 && (zone < 0 || _zones[z] > _zones[zone]))
                    zone = z;
            }
            if (zone >= 0)
                v[Perf.Zone] = zone;
            Pool(zone);

            string name = PhaseNames[_phase];
            if (_phase == 0)
                _baseFps = v[Perf.Fps];
            string restarted = _restarts > 0 ? "; measured again " + _restarts + " x (" + _restartWhy + ")" : "";
            Perf.WriteBench(v, name, "round " + RoundNumber + "; measured " + MeasureSeconds.ToString("0") +
                                     " s; per-second means over " + _counts[Perf.Alt] +
                                     " whole seconds; gc_gen0 = collections in the window" + restarted);

            double fps = v[Perf.Fps];
            string rel = !double.IsNaN(_baseFps) && _baseFps > 0 && !double.IsNaN(fps) && _phase > 0
                ? " (" + ((fps / _baseFps - 1.0) * 100.0).ToString("+0;-0;0") + " % vs all-on)"
                : "";
            Plugin.Log?.LogInfo("bench round " + RoundNumber + " " + name + ": " + F(fps, "0.0") + " fps" + rel + ", frame median " +
                                F(v[Perf.FtMed], "0.0") + " ms, p95 " + F(v[Perf.FtP95], "0.0") + " ms, max " +
                                F(v[Perf.FtMax], "0.0") + " ms; puffs " + F(v[Perf.Puffs], "0") + " (cap " +
                                F(v[Perf.PuffCap], "0") + "), distant " + F(v[Perf.Distant], "0") + ", fly-through " +
                                F(v[Perf.Fly], "0") + ", rain " + F(v[Perf.RainP], "0") + "; " +
                                (zone >= 0 ? Perf.ZoneNames[zone] : "?") + ", " + F(v[Perf.Alt], "0") + " m; batches " +
                                F(v[Perf.Batches], "0") + ", setpass " + F(v[Perf.SetPass], "0") + ", gpu " + F(v[Perf.FtmGpu], "0.0") +
                                " ms" + restarted);
        }

        private static string F(double x, string format)
        {
            return double.IsNaN(x) || double.IsInfinity(x) ? "-" : x.ToString(format);
        }

        private static void Finish(string how, bool reevaluate, bool completed)
        {
            float now = Time.realtimeSinceStartup;
            bool wasAuto = _autoThisRun;
            int rounds = _round;
            int phaseAt = _phase;
            try
            {
                ClearOverrides();
            }
            finally
            {
                Running = false;
                _measuring = false;
                PhaseLabel = "";
                _li = null;
                _layer = null;
                _nextPoll = now + 3f;
                _disturbedSince = float.NaN;
                _autoThisRun = false;
                _startAircraft = null;
                _startFollowing = null;
            }
            // a trigger created during the run must not start a second run straight away
            try
            {
                string path = TriggerPath;
                if (File.Exists(path))
                {
                    File.Delete(path);
                    Plugin.Log?.LogInfo("a benchmark trigger file created during the run was deleted; create it again to start another run");
                }
            }
            catch (Exception e)
            {
                _nextPoll = now + 30f;
                Plugin.Log?.LogWarning("a benchmark trigger file created during the run could not be deleted (" + e.Message + ")");
            }
            if (reevaluate)
                Plugin.EvaluateNow();
            try
            {
                WriteSummary(now, completed, rounds, phaseAt);
            }
            catch (Exception e)
            {
                Plugin.ErrorOnce("bench-summary", "writing the benchmark summary", e);
            }
            Plugin.Log?.LogInfo("benchmark " + how + " after " + ((now - _runStart) / 60f).ToString("0.0") +
                                " min; every override is cleared and the clouds, rain and fog are back as configured");
            Perf.Event("benchmark " + how);
            if (completed && wasAuto)
                CountDownAutoRuns();
            else if (wasAuto)
            {
                try
                {
                    Plugin.Log?.LogInfo("the automatic benchmark did not finish, so it does not count: BenchmarkAutoRuns stays " +
                                        Plugin.BenchmarkAutoRuns.Value + ", and it starts by itself again in the next game " +
                                        "session (at most one automatic run per session; the trigger file still works now)");
                }
                catch (Exception e)
                {
                    Plugin.ErrorOnce("bench-autoruns", "reporting BenchmarkAutoRuns", e);
                }
            }
            Perf.Flush();
        }

        /// <summary>A completed automatic run uses up one of BenchmarkAutoRuns, and the config file is saved.</summary>
        private static void CountDownAutoRuns()
        {
            try
            {
                int left = Plugin.BenchmarkAutoRuns.Value;
                if (left <= 0)
                    return;
                Plugin.BenchmarkAutoRuns.Value = left - 1;
                Plugin.SaveConfig();
                Plugin.Log?.LogInfo("Performance.BenchmarkAutoRuns is now " + Plugin.BenchmarkAutoRuns.Value + " (saved)" +
                                    (Plugin.BenchmarkAutoRuns.Value > 0 ? "; the benchmark starts by itself again next game session" : ""));
            }
            catch (Exception e)
            {
                Plugin.ErrorOnce("bench-autoruns", "counting down BenchmarkAutoRuns", e);
            }
        }

        private static void ResetWindow()
        {
            _stored = 0;
            _frames = 0;
            _sum = 0;
            _max = 0f;
            for (int i = 0; i < _sums.Length; i++)
            {
                _sums[i] = 0;
                _counts[i] = 0;
            }
            _zones[0] = _zones[1] = _zones[2] = 0;
            _gcStart = GC.CollectionCount(0);
        }

        // ------------------------------------------------------------------------------------------------- pooling

        private static void ResetPool()
        {
            Array.Clear(_hist, 0, _hist.Length);
            Array.Clear(_poolWindows, 0, _poolWindows.Length);
            Array.Clear(_poolFrames, 0, _poolFrames.Length);
            Array.Clear(_poolSum, 0, _poolSum.Length);
            Array.Clear(_poolBinned, 0, _poolBinned.Length);
            Array.Clear(_poolMax, 0, _poolMax.Length);
            _windowsTotal = 0;
        }

        /// <summary>Add the window just measured to its (phase, zone) pool: frame count and time, maximum, and every
        /// stored frame delta into the histogram. Runs at the end of the window, outside it; no allocation.</summary>
        private static void Pool(int zone)
        {
            if (_frames <= 0 || _sum <= 0)
                return;
            int slot = _phase * ZoneBuckets + (zone >= 0 && zone < 3 ? zone : 3);
            _poolWindows[slot]++;
            _poolFrames[slot] += _frames;
            _poolSum[slot] += _sum;
            if (_max > _poolMax[slot])
                _poolMax[slot] = _max;
            int off = slot * BinSlots;
            for (int i = 0; i < _stored; i++)
                _hist[off + Bin(_buf[i])]++;
            _poolBinned[slot] += _stored;
            _windowsTotal++;
        }

        private static int Bin(float dtSeconds)
        {
            double b = dtSeconds * 1000.0 / BinMs;
            if (double.IsNaN(b) || b < 0)
                return 0;
            return b >= Bins ? Bins : (int)b;
        }

        /// <summary>Nearest-rank percentile (0..1) of n deltas in hist[off..off+BinSlots), ms: the centre of the bin that
        /// holds that rank, or 250 ms for the overflow bin.</summary>
        private static double HistPercentile(int[] hist, int off, long n, double p)
        {
            if (n <= 0)
                return double.NaN;
            long rank = (long)Math.Ceiling(p * n);
            if (rank < 1)
                rank = 1;
            long cum = 0;
            for (int b = 0; b < BinSlots; b++)
            {
                cum += hist[off + b];
                if (cum >= rank)
                    return b >= Bins ? Bins * BinMs : (b + 0.5) * BinMs;
            }
            return Bins * BinMs;
        }

        /// <summary>
        /// Summary of the whole run: for each phase, one line over every zone and one per camera zone it was measured
        /// in, each pooled over its windows. fps = all frames / their total time; median and p95 from the histogram of
        /// every frame delta (not averages of the per-window values); change against all-on in the same zone (the "all"
        /// line against all-on over every zone). A "summary" CSV row and a log line each. After an abort it covers the
        /// windows measured so far.
        /// </summary>
        private static void WriteSummary(float now, bool completed, int rounds, int phaseAt)
        {
            if (_windowsTotal == 0)
                return;
            string scope = completed
                ? rounds + " full round" + (rounds == 1 ? "" : "s") + (phaseAt > 0 ? " and " + phaseAt + " phase" + (phaseAt == 1 ? "" : "s") + " of the next" : "")
                : "the run was aborted; only the " + _windowsTotal + " windows measured so far";
            Plugin.Log?.LogInfo("benchmark summary (" + scope + "; per phase, then per camera zone: windows, fps = all frames / " +
                                "their total time, frame median and p95 over every frame in 0.1 ms bins, change in fps " +
                                "against all-on in the same zone; compare phases on the per-zone lines, the [all] lines " +
                                "mix the zones each phase happened to be measured in):");

            // all-on over every zone, the reference of the "all" lines
            long baseFrames = 0;
            double baseSum = 0;
            for (int z = 0; z < ZoneBuckets; z++)
            {
                baseFrames += _poolFrames[z];
                baseSum += _poolSum[z];
            }
            double baseAll = baseSum > 0 ? baseFrames / baseSum : double.NaN;

            for (int p = 0; p < PhaseNames.Length; p++)
            {
                int windows = 0;
                long frames = 0, binned = 0;
                double sum = 0;
                float max = 0f;
                Array.Clear(_allHist, 0, _allHist.Length);
                for (int z = 0; z < ZoneBuckets; z++)
                {
                    int slot = p * ZoneBuckets + z;
                    if (_poolWindows[slot] == 0)
                        continue;
                    windows += _poolWindows[slot];
                    frames += _poolFrames[slot];
                    sum += _poolSum[slot];
                    binned += _poolBinned[slot];
                    if (_poolMax[slot] > max)
                        max = _poolMax[slot];
                    int off = slot * BinSlots;
                    for (int b = 0; b < BinSlots; b++)
                        _allHist[b] += _hist[off + b];
                }
                if (windows == 0)
                {
                    Plugin.Log?.LogInfo("bench summary " + PhaseNames[p] + " [all]: not measured");
                    continue;
                }
                SummaryLine(now, p, Perf.ZoneAll, "all", windows, frames, sum, max, _allHist, 0, binned, baseAll);

                for (int z = 0; z < ZoneBuckets; z++)
                {
                    int slot = p * ZoneBuckets + z;
                    if (_poolWindows[slot] == 0)
                        continue;
                    double zoneBase = _poolSum[z] > 0 ? _poolFrames[z] / _poolSum[z] : double.NaN; // all-on, same zone
                    SummaryLine(now, p, z < 3 ? z : Perf.ZoneUnknown, BucketNames[z], _poolWindows[slot], _poolFrames[slot],
                                _poolSum[slot], _poolMax[slot], _hist, slot * BinSlots, _poolBinned[slot], zoneBase);
                }
            }
            Perf.Event("benchmark summary above: " + _windowsTotal + " windows; " + scope);
        }

        private static void SummaryLine(float now, int phase, int zoneColumn, string zoneName, int windows, long frames,
                                        double sum, float max, int[] hist, int off, long binned, double baseFps)
        {
            double[] v = _out;
            for (int i = 0; i < v.Length; i++)
                v[i] = double.NaN;
            v[Perf.T] = now;
            v[Perf.Frames] = frames;
            v[Perf.Zone] = zoneColumn;
            double fps = sum > 0 ? frames / sum : double.NaN;
            v[Perf.Fps] = fps;
            v[Perf.FtAvg] = frames > 0 ? sum / frames * 1000.0 : double.NaN;
            // a bin centre can lie just above the longest frame; the exact maximum bounds both
            v[Perf.FtMax] = max * 1000.0;
            v[Perf.FtMed] = Math.Min(HistPercentile(hist, off, binned, 0.5), v[Perf.FtMax]);
            v[Perf.FtP95] = Math.Min(HistPercentile(hist, off, binned, 0.95), v[Perf.FtMax]);
            string change;
            if (phase == 0)
                change = "reference";
            else if (!double.IsNaN(baseFps) && baseFps > 0 && !double.IsNaN(fps))
                change = ((fps / baseFps - 1.0) * 100.0).ToString("+0.0;-0.0;0.0") + " % vs all-on";
            else
                change = zoneColumn == Perf.ZoneAll ? "no all-on window" : "no all-on window in this zone";
            string name = PhaseNames[phase];
            Perf.WriteSummary(v, name, "windows " + windows + "; " + change + "; median and p95 nearest-rank in 0.1 ms bins" +
                                       (binned < frames ? " (" + binned + " of " + frames + " frames binned)" : ""));
            Plugin.Log?.LogInfo("bench summary " + name + " [" + zoneName + "]: " + windows + " window" + (windows == 1 ? "" : "s") +
                                ", " + F(fps, "0.0") + " fps (" + change + "), frame median " + F(v[Perf.FtMed], "0.0") +
                                " ms, p95 " + F(v[Perf.FtP95], "0.0") + " ms, max " + F(v[Perf.FtMax], "0.0") + " ms");
        }

        // ------------------------------------------------------------------------------------------------- renderers

        private static bool PuffsFull()
        {
            ParticleSystem s = _layer != null ? Deck.CloudSystemOf(_layer) : null;
            if (s == null)
                return true;
            return s.particleCount >= 0.95f * s.main.maxParticles;
        }

        private static void HidePuffs()
        {
            ParticleSystem s = _layer != null ? Deck.CloudSystemOf(_layer) : null;
            ParticleSystemRenderer r = s != null ? s.GetComponent<ParticleSystemRenderer>() : null;
            if (r == null)
                return;
            _puffRenderer = r;
            _puffWas = r.enabled;
            r.enabled = false;
        }

        private static void HideDistant()
        {
            ParticleSystem s = _layer != null ? Deck.DistantOf(_layer) : null;
            ParticleSystemRenderer r = s != null ? s.GetComponent<ParticleSystemRenderer>() : null;
            if (r == null)
                return;
            _distantRenderer = r;
            _distantWas = r.enabled;
            r.enabled = false;
        }

        /// <summary>Every override off and every renderer this class switched back to its earlier state.</summary>
        private static void ClearOverrides()
        {
            HideTop = false;
            NoFlyThrough = false;
            NoRain = false;
            StockFog = false;
            ModOff = false;
            Filling = false;
            Budget = -1f;
            if (!ReferenceEquals(_puffRenderer, null))
            {
                if (_puffRenderer != null) // Unity null when the scene is gone
                    _puffRenderer.enabled = _puffWas;
                _puffRenderer = null;
            }
            if (!ReferenceEquals(_distantRenderer, null))
            {
                if (_distantRenderer != null)
                    _distantRenderer.enabled = _distantWas;
                _distantRenderer = null;
            }
        }
    }
}
