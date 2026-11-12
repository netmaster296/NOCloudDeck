using System;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;

namespace CloudDeck
{
    /// <summary>
    /// Performance log. While a mission that matches the Missions filter is loaded (whether or not the deck is on, so
    /// stock and modded runs can be compared), one CSV row per second goes to BepInEx/NOCloudDeck-perf-yyyyMMdd-HHmmss.csv,
    /// one file per game session. The file starts with a comment block (lines starting with '#') describing the machine,
    /// the game's graphics settings, the mod's config and which engine counters this build can read.
    ///
    /// Cost: frame deltas go into a preallocated buffer, rows are formatted into a preallocated char buffer (no strings
    /// are built for sample rows), and the StreamWriter is flushed every 5 s, at mission end and at the end of a
    /// benchmark. Event rows (mission start/end, benchmark start/end) build a few strings; they are rare.
    /// </summary>
    internal static class Perf
    {
        // ------------------------------------------------------------------------------------------------- columns

        internal const int RowType = 0, T = 1, Frames = 2, Fps = 3, FtAvg = 4, FtMed = 5, FtP95 = 6, FtMax = 7,
            FtmCpu = 8, FtmMain = 9, FtmRender = 10, FtmGpu = 11,
            PrMainThread = 12, PrCpuMain = 13, PrCpuRender = 14, PrGpu = 15,
            Batches = 16, SetPass = 17, DrawCalls = 18, Tris = 19, Verts = 20,
            Alt = 21, Zone = 22, Base = 23, Top = 24, WSet = 25, RainI = 26,
            Puffs = 27, PuffCap = 28, Distant = 29, Fly = 30, RainP = 31,
            BasePlane = 32, TopPlane = 33, Fog = 34,
            Active = 35, DeckRunning = 36, CfgEnabled = 37, CfgRain = 38, CfgMap = 39, CfgCull = 40,
            Budget = 41, FlyInt = 42, MaxRain = 43, Phase = 44, Round = 45, Gc = 46, MemMb = 47, Note = 48,
            CamX = 49, CamZ = 50, LocalCover = 51, LocalBase = 52, RainLocal = 53, Count = 54;

        internal static readonly string[] Names =
        {
            "row_type", "t_s", "frames", "fps_avg", "ft_avg_ms", "ft_median_ms", "ft_p95_ms", "ft_max_ms",
            "ftm_cpu_ms", "ftm_cpu_main_ms", "ftm_cpu_render_ms", "ftm_gpu_ms",
            "pr_main_thread_ms", "pr_cpu_main_ms", "pr_cpu_render_ms", "pr_gpu_ms",
            "batches", "setpass_calls", "draw_calls", "triangles", "vertices",
            "cam_alt_m", "zone", "base_m", "top_m", "weather_set", "rain_intensity",
            "cloud_puffs", "cloud_puff_cap", "distant_puffs", "flythrough_particles", "rain_particles",
            "base_plane_on", "top_plane_on", "fog_density",
            "mod_active", "deck_running", "cfg_enabled", "cfg_rain", "cfg_rain_map", "cfg_plane_cull",
            "puff_budget", "flythrough_interior", "max_rain", "bench_phase", "bench_round", "gc_gen0", "managed_mb", "note",
            "cam_x_m", "cam_z_m", "local_cover", "local_base_m", "rain_local",
        };

        private static readonly int[] Dec =
        {
            0, 2, 0, 1, 2, 2, 2, 2,
            2, 2, 2, 2,
            2, 2, 2, 2,
            0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 3,
            0, 0, 0, 0, 0,
            0, 0, 7,
            0, 0, 0, 0, 0, 0,
            2, 2, 0, 0, 0, 0, 1, 0,
            0, 0, 2, 0, 3,
        };

        internal static readonly string[] ZoneNames = { "below", "in", "above" };

        /// <summary>Zone column labels: the three camera zones, then two used only by benchmark summary rows (a window
        /// with no camera zone, and the line pooled over every zone).</summary>
        internal const int ZoneUnknown = 3, ZoneAll = 4;
        private static readonly string[] ZoneLabels = { "below", "in", "above", "unknown", "all" };

        // ProfilerRecorder counters, columns PrMainThread.. Verts. Times are nanoseconds.
        private static readonly string[] RecNames =
        {
            "Main Thread", "CPU Main Thread Frame Time", "CPU Render Thread Frame Time", "GPU Frame Time",
            "Batches Count", "SetPass Calls Count", "Draw Calls Count", "Triangles Count", "Vertices Count",
        };
        private const int RecTimes = 4; // the first 4 are times (ns -> ms)

        // ------------------------------------------------------------------------------------------------- state

        private const int SecondCapacity = 4096;
        private static readonly float[] _dts = new float[SecondCapacity];
        private static int _frames;
        private static int _stored;
        private static double _sumDt;
        private static float _maxDt;

        private static ProfilerRecorder[] _rec;
        private static bool _recStarted;
        private static readonly double[] _recSum = new double[RecNames.Length];
        private static readonly int[] _recN = new int[RecNames.Length];
        private static readonly bool[] _recSeen = new bool[RecNames.Length];
        private static readonly double[] _recScale = new double[RecNames.Length];
        private static readonly string[] _recInfo = new string[RecNames.Length];
        private static int _rowsThisMission;
        private static bool _statusLogged;

        private static bool _ftm;
        private static readonly FrameTiming[] _timing = new FrameTiming[1];
        // per field: a field is added only when it is above 0 (the GPU time of the latest frame can be missing), and a
        // frame GetLatestTimings returns again (same frameStartTimestamp) is not counted twice
        private static readonly double[] _ftmSum = new double[4];
        private static readonly int[] _ftmN = new int[4];
        private static ulong _ftmLastStart;
        private static bool _ftmSeen;
        private static float _secondStart;

        private static readonly double[] _v = new double[Count];

        private static StreamWriter _w;
        private static string _path;
        private static bool _fileFailed;
        private static bool _failed;
        private static float _nextRow;
        private static float _nextFlush;
        private static bool _inMission;
        private static LevelInfo _li;

        private static readonly char[] _line = new char[4096];
        private static int _len;
        private static readonly char[] _digits = new char[24];
        private static readonly double[] Pow10 = { 1, 10, 100, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9 };

        internal static bool Failed => _failed;
        internal static string FilePath => _path;

        // ------------------------------------------------------------------------------------------------- lifecycle

        internal static void Init()
        {
            try
            {
                _ftm = FrameTimingManager.IsFeatureEnabled();
            }
            catch (Exception)
            {
                _ftm = false;
            }
            if (Plugin.PerfLog.Value)
                StartRecorders();
        }

        /// <summary>
        /// Attach a ProfilerRecorder to each counter. Each is looked up by name among the stats this build has registered
        /// (whatever its category), which also gives its unit; a name not registered yet is attached by the usual
        /// category, and Unity connects it if the stat appears later. A counter that never reports stays empty.
        /// </summary>
        private static void StartRecorders()
        {
            if (_recStarted)
                return;
            _recStarted = true;
            _rec = new ProfilerRecorder[RecNames.Length];
            var found = new ProfilerRecorderHandle[RecNames.Length];
            var have = new bool[RecNames.Length];
            try
            {
                var all = new System.Collections.Generic.List<ProfilerRecorderHandle>(4096);
                ProfilerRecorderHandle.GetAvailable(all);
                foreach (ProfilerRecorderHandle h in all)
                {
                    ProfilerRecorderDescription d = ProfilerRecorderHandle.GetDescription(h);
                    int i = Array.IndexOf(RecNames, d.Name);
                    if (i < 0 || have[i])
                        continue;
                    found[i] = h;
                    have[i] = true;
                    _recScale[i] = d.UnitType == ProfilerMarkerDataUnit.TimeNanoseconds ? 1e-6 : 1.0;
                    _recInfo[i] = d.Category.Name + ", " + d.UnitType;
                }
            }
            catch (Exception e)
            {
                Plugin.ErrorOnce("perf-rec-list", "listing the profiler counters", e);
            }
            for (int i = 0; i < RecNames.Length; i++)
            {
                try
                {
                    if (have[i])
                    {
                        _rec[i] = new ProfilerRecorder(found[i], 1);
                        _rec[i].Start();
                    }
                    else
                    {
                        ProfilerCategory cat = i < RecTimes ? ProfilerCategory.Internal : ProfilerCategory.Render;
                        _rec[i] = ProfilerRecorder.StartNew(cat, RecNames[i], 1);
                        _recScale[i] = i < RecTimes ? 1e-6 : 1.0;
                        _recInfo[i] = "not registered at start";
                    }
                }
                catch (Exception e)
                {
                    Plugin.ErrorOnce("perf-rec:" + RecNames[i], "starting the profiler counter '" + RecNames[i] + "'", e);
                }
            }
        }

        /// <summary>Called every frame from Plugin.Update.</summary>
        internal static void Tick()
        {
            if (_failed)
                return;
            bool log = Plugin.PerfLog.Value;
            if (!log && _w != null)
            {
                // PerfLog switched off mid-session: end this file (switching it on again starts a new one)
                Event("PerfLog switched off; this file ends here");
                CloseWriter();
            }
            LevelInfo li = Plugin.InMatchedMission ? NetworkSceneSingleton<LevelInfo>.i : null;
            if (li == null)
            {
                if (_inMission)
                    EndMission();
                return;
            }
            if (_inMission && !ReferenceEquals(li, _li))
                EndMission();
            // the benchmark needs the counters even when the CSV is off (its summary goes to the BepInEx log)
            bool sample = log || Bench.Running;
            if (sample && !_recStarted)
                StartRecorders();
            if (log && _w == null && !_fileFailed)
                Open();
            if (!_inMission)
                BeginMission(li);

            float dt = Time.unscaledDeltaTime;
            _frames++;
            _sumDt += dt;
            if (dt > _maxDt)
                _maxDt = dt;
            if (_stored < SecondCapacity)
                _dts[_stored++] = dt;
            if (sample)
                SampleCounters();
            Bench.Frame(dt);

            float now = Time.realtimeSinceStartup;
            if (now >= _nextRow)
            {
                _nextRow += 1f;
                if (_nextRow <= now)
                    _nextRow = now + 1f;
                if (_w != null || Bench.Running)
                {
                    BuildRow(li, now);
                    if (_w != null && log)
                        WriteRow("sample", _v, Bench.PhaseLabel, null);
                    Bench.OnRow(_v, _secondStart);
                }
                ResetSecond();
                // once per session, after 10 s in a mission: which counters really report in this build
                if (++_rowsThisMission == 10 && !_statusLogged && log)
                {
                    _statusLogged = true;
                    string status = CounterStatus();
                    Plugin.Log?.LogInfo("performance log counters: " + status);
                    Event("counters after 10 s in the mission: " + status);
                }
            }
            // a blocking disk write must not land in a benchmark window: while a run goes, the file is written at the
            // start of each phase's settle time instead (Bench.BeginPhase); 10-30 s of rows stay far below the buffer
            if (_w != null && now >= _nextFlush && !Bench.Running)
                Flush();
        }

        private static void SampleCounters()
        {
            if (_rec != null)
            {
                for (int i = 0; i < _rec.Length; i++)
                {
                    if (!_rec[i].Valid)
                        continue;
                    long value = _rec[i].LastValue;
                    if (value > 0)
                    {
                        _recSum[i] += value;
                        _recN[i]++;
                        _recSeen[i] = true;
                    }
                }
            }
            if (_ftm)
            {
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, _timing) > 0)
                {
                    FrameTiming f = _timing[0];
                    if (f.frameStartTimestamp == 0 || f.frameStartTimestamp != _ftmLastStart)
                    {
                        _ftmLastStart = f.frameStartTimestamp;
                        AddFtm(0, f.cpuFrameTime);
                        AddFtm(1, f.cpuMainThreadFrameTime);
                        AddFtm(2, f.cpuRenderThreadFrameTime);
                        AddFtm(3, f.gpuFrameTime);
                    }
                }
            }
        }

        private static void AddFtm(int i, double ms)
        {
            if (ms <= 0 || double.IsNaN(ms))
                return;
            _ftmSum[i] += ms;
            _ftmN[i]++;
            _ftmSeen = true;
        }

        private static void ResetSecond()
        {
            _secondStart = Time.realtimeSinceStartup;
            _frames = 0;
            _stored = 0;
            _sumDt = 0;
            _maxDt = 0f;
            for (int i = 0; i < _recSum.Length; i++)
            {
                _recSum[i] = 0;
                _recN[i] = 0;
            }
            for (int i = 0; i < _ftmSum.Length; i++)
            {
                _ftmSum[i] = 0;
                _ftmN[i] = 0;
            }
        }

        private static void BeginMission(LevelInfo li)
        {
            _inMission = true;
            _li = li;
            _rowsThisMission = 0;
            ResetSecond();
            _nextRow = Time.realtimeSinceStartup + 1f;
            if (_w != null)
            {
                float detail = float.NaN;
                try { detail = PlayerSettings.graphics.CloudDetail; } catch (Exception) { }
                Event("mission start: \"" + Plugin.CurrentMission + "\", weather " + (Plugin.CurrentWeather ?? "?") +
                      ", cloud base " + li.cloudHeight.ToString("0") + " m, conditions " + li.conditions.ToString("0.00") +
                      ", CloudDetail " + detail.ToString("0.00") + ", screen " + Screen.width + "x" + Screen.height);
            }
        }

        private static void EndMission()
        {
            _inMission = false;
            _li = null;
            if (_w != null)
            {
                Event("mission end");
                Flush();
            }
        }

        internal static void Fail(Exception e)
        {
            _failed = true;
            Plugin.ErrorOnce("perf", "the performance log", e);
            Close();
        }

        internal static void Flush()
        {
            _nextFlush = Time.realtimeSinceStartup + 5f;
            if (_w == null)
                return;
            try
            {
                _w.Flush();
            }
            catch (Exception e)
            {
                Plugin.ErrorOnce("perf-flush", "writing the performance log", e);
                _fileFailed = true;
                CloseWriter();
            }
        }

        internal static void Close()
        {
            try
            {
                if (_w != null && _inMission)
                    Event("game closing");
            }
            catch (Exception) { }
            CloseWriter();
            if (_rec != null)
            {
                for (int i = 0; i < _rec.Length; i++)
                {
                    try { _rec[i].Dispose(); } catch (Exception) { }
                }
                _rec = null;
                _recStarted = false;
            }
        }

        private static void CloseWriter()
        {
            if (_w == null)
                return;
            try { _w.Flush(); } catch (Exception) { }
            try { _w.Dispose(); } catch (Exception) { }
            _w = null;
        }

        // ------------------------------------------------------------------------------------------------- the row

        private static void BuildRow(LevelInfo li, float now)
        {
            double[] v = _v;
            for (int i = 0; i < Count; i++)
                v[i] = double.NaN;
            v[T] = now;
            v[Frames] = _frames;
            if (_frames > 0 && _sumDt > 0)
            {
                v[Fps] = _frames / _sumDt;
                v[FtAvg] = _sumDt / _frames * 1000.0;
                HeapSort(_dts, _stored);
                v[FtMed] = Median(_dts, _stored) * 1000.0;
                v[FtP95] = Percentile(_dts, _stored, 0.95) * 1000.0;
                v[FtMax] = _maxDt * 1000.0;
            }
            for (int i = 0; i < _ftmSum.Length; i++)
            {
                if (_ftmN[i] > 0)
                    v[FtmCpu + i] = _ftmSum[i] / _ftmN[i];
            }
            for (int i = 0; i < RecNames.Length; i++)
            {
                if (_recN[i] > 0)
                    v[PrMainThread + i] = _recSum[i] / _recN[i] * _recScale[i];
            }

            float b = li.cloudHeight;
            bool deck = Plugin.Active && Deck.Usable;
            float t = deck ? Plugin.TopFor(Plugin.BaseNow) : b;
            v[Base] = b;
            v[Top] = t;
            CameraStateManager csm = SceneSingleton<CameraStateManager>.i;
            Camera cam = csm != null ? csm.mainCamera : null;
            if (cam != null)
            {
                Vector3 p = cam.transform.position;
                GlobalPosition g = p.ToGlobalPosition();
                v[Alt] = g.y;
                v[Zone] = g.y < b ? 0 : g.y > t ? 2 : 1;
                v[RainI] = Plugin.RainIntensityAt(g.x, g.z);
                v[CamX] = g.x;
                v[CamZ] = g.z;
                v[LocalCover] = CloudMap.Cover(g.x, g.z);
                v[LocalBase] = Plugin.Active ? Plugin.LocalBaseAt(g.x, g.z) : b;
                v[RainLocal] = Plugin.RainMapAt(g.x, g.z);
            }
            CloudLayer layer = Deck.LayerOf(li);
            if (layer != null)
            {
                v[WSet] = Deck.WeatherIndex(layer, li);
                ParticleSystem s = Deck.CloudSystemOf(layer);
                if (s != null)
                {
                    v[Puffs] = s.particleCount;
                    v[PuffCap] = s.main.maxParticles;
                }
                s = Deck.DistantOf(layer);
                if (s != null)
                    v[Distant] = s.particleCount;
                s = Deck.FlyThroughOf(layer);
                if (s != null)
                    v[Fly] = s.particleCount;
                v[BasePlane] = Deck.BasePlaneOn(layer) ? 1 : 0;
            }
            Rain rain = Rain.Current;
            v[RainP] = rain != null ? rain.ParticleCount : 0;
            v[TopPlane] = Deck.TopPlaneOn ? 1 : 0;
            v[Fog] = RenderSettings.fog ? RenderSettings.fogDensity : 0f;
            v[Active] = Plugin.Active ? 1 : 0;
            v[DeckRunning] = deck ? 1 : 0;
            v[CfgEnabled] = Plugin.Enabled.Value ? 1 : 0;
            v[CfgRain] = Plugin.RainEnabled.Value ? 1 : 0;
            v[CfgMap] = Plugin.UseRainMap.Value ? 1 : 0;
            v[CfgCull] = Plugin.PlaneCulling.Value ? 1 : 0;
            if (deck)
                v[Budget] = Deck.EffectiveBudget;
            v[FlyInt] = Plugin.FlyThroughInterior.Value;
            v[MaxRain] = Plugin.MaxRainParticles.Value;
            if (Bench.Running)
                v[Round] = Bench.RoundNumber;
            v[Gc] = GC.CollectionCount(0);
            v[MemMb] = GC.GetTotalMemory(false) / 1048576.0;
        }

        /// <summary>A benchmark summary row (row_type "bench"): the given values, phase in bench_phase.</summary>
        internal static void WriteBench(double[] v, string phase, string note)
        {
            if (_w == null || _failed)
                return;
            WriteRow("bench", v, phase, note);
        }

        /// <summary>A benchmark summary row (row_type "summary"): one phase pooled over its measured windows, in one
        /// camera zone or (zone "all") over every zone. Written once at the end of a run, outside any window.</summary>
        internal static void WriteSummary(double[] v, string phase, string note)
        {
            if (_w == null || _failed)
                return;
            WriteRow("summary", v, phase, note);
        }

        /// <summary>An event row (row_type "event"): time and a note. Rare; may allocate.</summary>
        internal static void Event(string note)
        {
            if (_w == null || _failed)
                return;
            double[] v = _v;
            for (int i = 0; i < Count; i++)
                v[i] = double.NaN;
            v[T] = Time.realtimeSinceStartup;
            WriteRow("event", v, Bench.PhaseLabel, note);
        }

        private static void WriteRow(string type, double[] v, string phase, string note)
        {
            _len = 0;
            for (int c = 0; c < Count; c++)
            {
                if (c > 0)
                    Put(',');
                switch (c)
                {
                    case RowType:
                        Str(type);
                        break;
                    case Zone:
                        if (!double.IsNaN(v[Zone]))
                            Str(ZoneLabels[Mathf.Clamp((int)v[Zone], 0, ZoneLabels.Length - 1)]);
                        break;
                    case Phase:
                        Str(phase);
                        break;
                    case Note:
                        Quoted(note);
                        break;
                    default:
                        Num(v[c], Dec[c]);
                        break;
                }
            }
            PutReserved('\n');
            try
            {
                _w.Write(_line, 0, _len);
            }
            catch (Exception e)
            {
                Plugin.ErrorOnce("perf-write", "writing the performance log", e);
                _fileFailed = true;
                CloseWriter();
            }
        }

        // ------------------------------------------------------------------------------------------------- the file

        private static void Open()
        {
            try
            {
                string dir = Paths.BepInExRootPath;
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                string path = Path.Combine(dir, "NOCloudDeck-perf-" + stamp + ".csv");
                for (int i = 2; File.Exists(path) && i < 100; i++)
                    path = Path.Combine(dir, "NOCloudDeck-perf-" + stamp + "-" + i + ".csv");
                var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                _w = new StreamWriter(stream, new UTF8Encoding(false), 65536);
                _path = path;
                _w.Write(Header());
                _w.Flush();
                _nextFlush = Time.realtimeSinceStartup + 5f;
                Plugin.Log?.LogInfo("performance log: " + path);
            }
            catch (Exception e)
            {
                _fileFailed = true;
                CloseWriter();
                Plugin.ErrorOnce("perf-open", "opening the performance log", e);
            }
        }

        private static string Header()
        {
            var sb = new StringBuilder(4096);
            void L(string s) => sb.Append("# ").Append(s).Append('\n');
            DateTime now = DateTime.Now;
            L("NO Cloud Deck " + Plugin.Version + " performance log, started " + now.ToString("yyyy-MM-dd HH:mm:ss") +
              " local (" + now.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss") + " UTC), t_s = Time.realtimeSinceStartup " +
              Time.realtimeSinceStartup.ToString("0.0"));
            L("game: Nuclear Option " + Safe(() => Application.version) + ", Unity " + Safe(() => Application.unityVersion) +
              ", " + Safe(() => Application.platform.ToString()));
            L("os: " + Safe(() => SystemInfo.operatingSystem));
            L("gpu: " + Safe(() => SystemInfo.graphicsDeviceName) + " (" + Safe(() => SystemInfo.graphicsDeviceVendor) + "), " +
              Safe(() => SystemInfo.graphicsDeviceType.ToString()) + " " + Safe(() => SystemInfo.graphicsDeviceVersion) +
              ", VRAM " + Safe(() => SystemInfo.graphicsMemorySize.ToString()) + " MB, shader level " +
              Safe(() => SystemInfo.graphicsShaderLevel.ToString()));
            L("cpu: " + Safe(() => SystemInfo.processorType) + ", " + Safe(() => SystemInfo.processorCount.ToString()) +
              " logical cores, " + Safe(() => SystemInfo.processorFrequency.ToString()) + " MHz; RAM " +
              Safe(() => SystemInfo.systemMemorySize.ToString()) + " MB");
            L("screen: render " + Safe(() => Screen.width + "x" + Screen.height) + ", display " +
              Safe(() => Screen.currentResolution.width + "x" + Screen.currentResolution.height + " @ " +
                         Screen.currentResolution.refreshRateRatio.value.ToString("0.##") + " Hz") +
              ", mode " + Safe(() => Screen.fullScreenMode.ToString()) + ", dpi " + Safe(() => Screen.dpi.ToString("0")));
            L("quality: level " + Safe(() => QualitySettings.GetQualityLevel() + " \"" +
                                            QualitySettings.names[QualitySettings.GetQualityLevel()] + "\"") +
              ", vSyncCount " + Safe(() => QualitySettings.vSyncCount.ToString()) +
              ", Application.targetFrameRate " + Safe(() => Application.targetFrameRate.ToString()) +
              ", GameManager.TargetFrameRate " + Safe(() => GameManager.TargetFrameRate.ToString()) +
              ", lodBias " + Safe(() => QualitySettings.lodBias.ToString("0.##")) +
              ", texture mip limit " + Safe(() => QualitySettings.globalTextureMipmapLimit.ToString()));
            L("game graphics settings (PlayerSettings.graphics): " + Safe(GameGraphics));
            L("render pipeline: " + Safe(RenderPipeline));
            L("mod config: " + Safe(ModConfig));
            L("counters at the start (an event row about 10 s into the first mission repeats this with in-mission data): " +
              Safe(CounterStatus));
            L("columns: times in ms; fps_avg = frames / sum of frame deltas; ft_* from Time.unscaledDeltaTime of every frame " +
              "in the second; pr_* and batches..vertices are per-frame averages of ProfilerRecorder counters; cam_x_m / cam_z_m = camera position in metres from the map centre (east, north), local_cover and local_base_m = the regional cloud map at the camera (cover 0..1; base for the eased mission base, equal to base_m when the regional deck is off or the mod is not active), rain_local = the rain map times the lee scaling at the camera before the weather's wetness (rain_intensity includes it); zone = camera " +
              "below the base, in the deck, or above the top (top = base when the deck is not running); fog_density is " +
              "RenderSettings.fogDensity (0 when fog is off); gc_gen0 is cumulative in sample rows and the collections " +
              "during the window in bench rows; ftm_* average only the frames that reported that field; bench_round = the " +
              "benchmark round (1, 2, ...) while a run goes; row_type sample = one second, bench = one benchmark phase " +
              "(measured window only, per-second means over the whole seconds inside it), summary = one phase pooled " +
              "over all its windows in one zone (zone all = every zone; frames, fps_avg, ft_avg_ms, ft_max_ms exact, " +
              "ft_median_ms and ft_p95_ms nearest-rank from 0.1 ms bins of every frame, the other columns empty; the " +
              "note gives the window count and the change against all-on), event = a note");
            for (int c = 0; c < Count; c++)
            {
                if (c > 0)
                    sb.Append(',');
                sb.Append(Names[c]);
            }
            sb.Append('\n');
            return sb.ToString();
        }

        private static string Safe(Func<string> f)
        {
            try
            {
                return f() ?? "null";
            }
            catch (Exception e)
            {
                return "(unavailable: " + e.GetType().Name + ")";
            }
        }

        private static string GameGraphics()
        {
            GraphicsHelper g = PlayerSettings.graphics;
            if (g == null)
                return "not loaded";
            return "CloudDetail " + g.CloudDetail.ToString("0.00") + ", Vsync " + g.Vsync + ", FpsLimit " + g.FpsLimit +
                   ", ShadowQuality " + Pick(GraphicsHelper.ShadowQualityOptions, g.ShadowQuality) +
                   ", ShadowDistance " + g.ShadowDistance + ", SoftShadows " + g.SoftShadows +
                   ", AntiAliasing " + Pick(GraphicsHelper.AAOptions, g.AntiAliasing) +
                   ", LodBias " + g.LodBias.ToString("0.##") +
                   ", MipmapLevel " + Pick(GraphicsHelper.MipmapLimitOptions, g.MipmapLevel) +
                   ", AnisotropicFiltering " + Pick(GraphicsHelper.AnisotropicOptions, g.AnisotropicFiltering) +
                   ", MaxLights " + Pick(GraphicsHelper.MaxLightsOptions, g.MaxLights);
        }

        private static string Pick(System.Collections.Generic.List<string> options, int index)
        {
            return index + (options != null && index >= 0 && index < options.Count ? " (" + options[index] + ")" : "");
        }

        private static string RenderPipeline()
        {
            RenderPipelineAsset rp = GraphicsSettings.currentRenderPipeline;
            if (rp == null)
                return "built-in";
            var sb = new StringBuilder(rp.GetType().Name + " '" + rp.name + "'");
            foreach (string name in new[] { "renderScale", "upscalingFilter", "msaaSampleCount", "supportsHDR",
                         "shadowDistance", "shadowCascadeCount", "mainLightShadowmapResolution",
                         "maxAdditionalLightsCount", "supportsCameraDepthTexture", "supportsCameraOpaqueTexture" })
            {
                try
                {
                    PropertyInfo p = rp.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (p != null)
                        sb.Append(", ").Append(name).Append(' ').Append(p.GetValue(rp, null));
                }
                catch (Exception) { }
            }
            return sb.ToString();
        }

        private static string ModConfig()
        {
            var sb = new StringBuilder();
            foreach (var kv in Plugin.Instance.Config)
            {
                if (sb.Length > 0)
                    sb.Append("; ");
                sb.Append(kv.Key.Section).Append('.').Append(kv.Key.Key).Append(" = ").Append(kv.Value.BoxedValue);
            }
            return sb.ToString();
        }

        /// <summary>Which counters report. A counter counts as available once it has given a value above 0, in a
        /// mission sample or in its last frame now.</summary>
        private static string CounterStatus()
        {
            var sb = new StringBuilder("FrameTimingManager ");
            if (!_ftm)
            {
                sb.Append("not enabled in this build (ftm_* columns stay empty)");
            }
            else
            {
                bool now = false;
                try
                {
                    FrameTimingManager.CaptureFrameTimings();
                    now = FrameTimingManager.GetLatestTimings(1, _timing) > 0 &&
                          (_timing[0].cpuFrameTime > 0 || _timing[0].gpuFrameTime > 0);
                }
                catch (Exception) { }
                sb.Append(_ftmSeen || now ? "enabled and reporting" : "enabled but no timings yet");
                if (now)
                    sb.Append(" (gpu ").Append(_timing[0].gpuFrameTime > 0 ? "yes" : "no").Append(')');
            }
            sb.Append("; ProfilerRecorder: ");
            if (_rec == null)
                return sb.Append("not started (PerfLog off at start)").ToString();
            for (int i = 0; i < RecNames.Length; i++)
            {
                if (i > 0)
                    sb.Append(", ");
                bool valid = false;
                long last = 0;
                try
                {
                    valid = _rec[i].Valid;
                    if (valid)
                        last = _rec[i].LastValue;
                }
                catch (Exception) { }
                sb.Append('\'').Append(RecNames[i]).Append("' ")
                  .Append(_recSeen[i] || last > 0 ? "available" : valid ? "attached, no data" : "unavailable");
                if (_recInfo[i] != null)
                    sb.Append(" [").Append(_recInfo[i]).Append(']');
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------------------------------------- statistics

        /// <summary>In-place ascending heap sort of a[0..n). No allocation.</summary>
        internal static void HeapSort(float[] a, int n)
        {
            for (int i = n / 2 - 1; i >= 0; i--)
                SiftDown(a, i, n);
            for (int end = n - 1; end > 0; end--)
            {
                float tmp = a[0];
                a[0] = a[end];
                a[end] = tmp;
                SiftDown(a, 0, end);
            }
        }

        private static void SiftDown(float[] a, int i, int n)
        {
            while (true)
            {
                int l = 2 * i + 1;
                if (l >= n)
                    return;
                int m = l + 1 < n && a[l + 1] > a[l] ? l + 1 : l;
                if (a[m] <= a[i])
                    return;
                float tmp = a[i];
                a[i] = a[m];
                a[m] = tmp;
                i = m;
            }
        }

        /// <summary>Median of a sorted a[0..n).</summary>
        internal static double Median(float[] a, int n)
        {
            if (n <= 0)
                return double.NaN;
            return (n & 1) == 1 ? a[n / 2] : 0.5 * (a[n / 2 - 1] + a[n / 2]);
        }

        /// <summary>Nearest-rank percentile of a sorted a[0..n).</summary>
        internal static double Percentile(float[] a, int n, double p)
        {
            if (n <= 0)
                return double.NaN;
            int k = (int)Math.Ceiling(p * n) - 1;
            return a[Mathf.Clamp(k, 0, n - 1)];
        }

        // ------------------------------------------------------------------------------------------------- formatting

        // the last chars of the line buffer are kept for a note's closing quote and the row's '\n', so an over-long
        // row is cut short but never runs into the next one
        private const int Reserve = 2;

        private static void Put(char c)
        {
            if (_len < _line.Length - Reserve)
                _line[_len++] = c;
        }

        private static void PutReserved(char c)
        {
            if (_len < _line.Length)
                _line[_len++] = c;
        }

        private static void Str(string s)
        {
            if (s == null)
                return;
            for (int i = 0; i < s.Length; i++)
                Put(s[i]);
        }

        private static void Quoted(string s)
        {
            if (string.IsNullOrEmpty(s))
                return;
            Put('"');
            int limit = _line.Length - Reserve;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (_len + (c == '"' ? 2 : 1) > limit)
                    break; // too long: cut, never in the middle of a doubled quote
                if (c == '"')
                    Put('"');
                Put(c == '\n' || c == '\r' ? ' ' : c);
            }
            PutReserved('"');
        }

        /// <summary>A fixed-point number with the given decimals, written into the line buffer without allocating.
        /// NaN and infinities are written as an empty field.</summary>
        private static void Num(double v, int dec)
        {
            if (double.IsNaN(v) || double.IsInfinity(v))
                return;
            dec = Mathf.Clamp(dec, 0, Pow10.Length - 1);
            double scaled = Math.Round(Math.Abs(v) * Pow10[dec]);
            if (scaled > 9e15)
                scaled = 9e15;
            long whole = (long)scaled;
            if (v < 0 && whole != 0)
                Put('-');
            long unit = (long)Pow10[dec];
            PutLong(whole / unit);
            if (dec > 0)
            {
                Put('.');
                long frac = whole % unit;
                for (long d = unit / 10; d >= 1; d /= 10)
                {
                    Put((char)('0' + frac / d));
                    frac %= d;
                }
            }
        }

        private static void PutLong(long x)
        {
            int n = 0;
            do
            {
                _digits[n++] = (char)('0' + x % 10);
                x /= 10;
            } while (x > 0 && n < _digits.Length);
            while (n > 0)
                Put(_digits[--n]);
        }
    }
}
