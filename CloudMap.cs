using System;
using System.IO;
using UnityEngine;

namespace CloudDeck
{
    /// <summary>
    /// The regional cloud map embedded in the DLL (Resources/cloudmap.bin, written by Cascadia's tools/cloudmap.py from
    /// airport weather statistics, see that script). Layout (little-endian): "CLDM", int32 version 1, int32 width,
    /// int32 height, float32 half extent (m), float32 ref_base, float32 base_min, float32 base_max, int32 plane count (4),
    /// then four planes of width*height bytes, row 0 = south, column 0 = west, the same frame as rainmap.bin:
    ///   cover  (byte / 255)                      cloud cover on a wet day, 0..1
    ///   base   (base_min + byte / 255 * range)   cloud base m MSL for a MISSION BASE of ref_base (900 m)
    ///   haze   (byte / 255)                      clear-air bonus (lee side, Olympic rain shadow), 0..1
    ///   lee    (byte / 255)                      0 at the Cascade crest, 1 thirty km east of it
    /// Everything is bilinear between cell centres; outside the grid the nearest edge value is used.
    ///
    /// The map is a description of the weather; the mission still sets one cloud base (LevelInfo.cloudHeight, B) and the
    /// two flat planes stay where they are. The local base is B shifted by how far the map's base is from ref_base,
    /// scaled by the wetness (so the regional differences die out as the weather clears and the stock layer takes
    /// over), kept 600 m under the deck top and at least 200 m up. Nothing here reads or writes a SyncVar.
    /// With Clouds.RegionalDeck off (or no map) every query answers "the same everywhere": cover 1, base B, no bonus,
    /// no lee scaling, which is what 1.0.2 did.
    /// </summary>
    internal static class CloudMap
    {
        private const string ResourceName = "NOCloudDeck.cloudmap.bin";
        private const float MinThickness = 600f;   // local base is kept this far under the top (never under B itself)
        private const float LowestBase = 200f;
        private const float LeeRainFloor = 0.5f;    // the rain map is halved 30 km east of the crest (weather-regional F8)

        private static byte[] _cover, _base, _haze, _lee;
        private static int _width, _height;
        private static float _half, _ref, _baseMin, _baseMax;

        internal static bool Loaded { get; private set; }

        /// <summary>The map is loaded and the RegionalDeck setting is on.</summary>
        internal static bool Enabled => Loaded && Plugin.RegionalDeck != null && Plugin.RegionalDeck.Value;

        internal static void Load()
        {
            try
            {
                using (Stream s = typeof(CloudMap).Assembly.GetManifestResourceStream(ResourceName))
                {
                    if (s == null)
                    {
                        Plugin.WarnOnce("cloudmap-missing", "cloud map resource missing; the clouds are the same everywhere");
                        return;
                    }
                    using (var r = new BinaryReader(s))
                    {
                        if (new string(r.ReadChars(4)) != "CLDM")
                            throw new InvalidDataException("bad magic");
                        int version = r.ReadInt32();
                        if (version != 1)
                            throw new InvalidDataException("version " + version);
                        _width = r.ReadInt32();
                        _height = r.ReadInt32();
                        _half = r.ReadSingle();
                        _ref = r.ReadSingle();
                        _baseMin = r.ReadSingle();
                        _baseMax = r.ReadSingle();
                        int planes = r.ReadInt32();
                        if (_width < 2 || _height < 2 || _width > 4096 || _height > 4096 || _half <= 0f || planes != 4 ||
                            !(_baseMax > _baseMin))
                            throw new InvalidDataException("bad header");
                        int n = _width * _height;
                        _cover = r.ReadBytes(n);
                        _base = r.ReadBytes(n);
                        _haze = r.ReadBytes(n);
                        _lee = r.ReadBytes(n);
                        if (_cover.Length != n || _base.Length != n || _haze.Length != n || _lee.Length != n)
                            throw new InvalidDataException("truncated");
                    }
                }
                Loaded = true;
            }
            catch (Exception e)
            {
                Plugin.ErrorOnce("cloudmap-load", "loading the cloud map", e);
            }
        }

        internal static string Describe()
        {
            return _width + "x" + _height + ", " + (2f * _half / 1000f).ToString("0.#") + " km square, base for a " +
                   _ref.ToString("0") + " m mission base";
        }

        // ---------------------------------------------------------------------------------------------- the planes

        /// <summary>Cloud cover 0..1 at a global x/z (1 when the regional deck is off).</summary>
        internal static float Cover(float x, float z)
        {
            return Enabled ? Sample(_cover, x, z) / 255f : 1f;
        }

        /// <summary>The map's cloud base (m MSL, for a mission base of RefBase) at a global x/z.</summary>
        internal static float MapBase(float x, float z)
        {
            return _baseMin + Sample(_base, x, z) / 255f * (_baseMax - _baseMin);
        }

        /// <summary>Clear-air bonus 0..1 (0 when the regional deck is off).</summary>
        internal static float HazeBonus(float x, float z)
        {
            return Enabled ? Sample(_haze, x, z) / 255f : 0f;
        }

        /// <summary>The multiplier for the rain map: 1 west of the Cascade crest, 0.5 thirty km east of it.</summary>
        internal static float RainScale(float x, float z)
        {
            return Enabled ? Mathf.Lerp(1f, LeeRainFloor, Sample(_lee, x, z) / 255f) : 1f;
        }

        /// <summary>
        /// The cloud base here, m MSL, for the mission base b and the deck top t: b plus the map's offset from its
        /// reference base times the wetness w, at least 200 m (or b if lower) and at most max(b, t - 600). Equal to b
        /// when the regional deck is off.
        /// </summary>
        internal static float LocalBase(float x, float z, float b, float t, float w)
        {
            if (!Enabled)
                return b;
            float lb = b + (MapBase(x, z) - _ref) * Mathf.Clamp01(w);
            float cap = Mathf.Max(b, t - MinThickness);
            return Mathf.Clamp(lb, Mathf.Min(LowestBase, b), cap);
        }

        /// <summary>How much of the in-cloud fog and wisps cover keeps: 0 at cover 0.3 and below, 1 at 0.9 and above.</summary>
        internal static float CoverFactor(float cover)
        {
            float x = Mathf.Clamp01((cover - 0.3f) / 0.6f);
            return x * x * (3f - 2f * x);
        }

        // --------------------------------------------------------------------------------------- coherent gate noise

        /// <summary>
        /// A spatially coherent value in 0..1, roughly uniform (the weighted sum of two octaves of value noise, 4.5 km
        /// and 1.8 km cells, through a logistic fitted to its spread: tools/cloudmap.py --check reports the fraction
        /// that falls under each cover). A puff or ring particle at x/z is kept if Gate(x, z) &lt; cover, so a cover
        /// of 0.65 leaves about 65 % of the sky in connected patches instead of thinning every place equally. Callers
        /// pass the position minus the wind displacement, so the patches drift with the cloud pattern.
        /// </summary>
        internal static float Gate(float x, float z)
        {
            float a = ValueNoise(x / 4500f, z / 4500f, 11u);
            float b = ValueNoise(x / 1800f + 31.7f, z / 1800f - 17.3f, 29u);
            float n = 0.65f * a + 0.35f * b;
            const float sd = 0.158f; // measured spread of n (about 0.5 +- 0.158), see tools/cloudmap.py --check
            float zz = (n - 0.5f) / sd;
            return 1f / (1f + Mathf.Exp(-1.702f * zz));
        }

        private static float ValueNoise(float fx, float fz, uint seed)
        {
            int ix = Mathf.FloorToInt(fx);
            int iz = Mathf.FloorToInt(fz);
            float tx = fx - ix;
            float tz = fz - iz;
            tx = tx * tx * (3f - 2f * tx);
            tz = tz * tz * (3f - 2f * tz);
            float a = Hash(ix, iz, seed), b = Hash(ix + 1, iz, seed), c = Hash(ix, iz + 1, seed), d = Hash(ix + 1, iz + 1, seed);
            return (a * (1f - tx) + b * tx) * (1f - tz) + (c * (1f - tx) + d * tx) * tz;
        }

        private static float Hash(int ix, int iz, uint seed)
        {
            unchecked
            {
                uint h = (uint)ix * 374761393u + (uint)iz * 668265263u + seed * 2246822519u + 1442695041u;
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFFu) / 16777216f;
            }
        }

        // ----------------------------------------------------------------------------------------------- sampling

        /// <summary>Bilinear between cell centres, 0..255 (as tools/cloudmap.py samples).</summary>
        private static float Sample(byte[] plane, float x, float z)
        {
            if (!Loaded || plane == null)
                return 0f;
            float cellX = 2f * _half / _width;
            float cellZ = 2f * _half / _height;
            float fx = (x + _half) / cellX - 0.5f;
            float fz = (z + _half) / cellZ - 0.5f;
            if (float.IsNaN(fx) || float.IsNaN(fz))
                return 0f;
            int i0 = Mathf.FloorToInt(fx);
            int j0 = Mathf.FloorToInt(fz);
            float tx = fx - i0;
            float tz = fz - j0;
            float a = Cell(plane, i0, j0), b = Cell(plane, i0 + 1, j0), c = Cell(plane, i0, j0 + 1), d = Cell(plane, i0 + 1, j0 + 1);
            return (a * (1f - tx) + b * tx) * (1f - tz) + (c * (1f - tx) + d * tx) * tz;
        }

        private static float Cell(byte[] plane, int i, int j)
        {
            i = Mathf.Clamp(i, 0, _width - 1);
            j = Mathf.Clamp(j, 0, _height - 1);
            return plane[j * _width + i];
        }
    }
}
