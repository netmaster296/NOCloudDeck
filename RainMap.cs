using System;
using System.IO;
using UnityEngine;

namespace CloudDeck
{
    /// <summary>
    /// The regional rain-intensity map embedded in the DLL (Resources/rainmap.bin, written by Cascadia's
    /// tools/rainmap.py from CHELSA V2.1 annual precipitation corrected to NOAA and ECCC station normals).
    /// Layout (little-endian): "NORM", int32 version 1, int32 width, int32 height, float32 half extent (m),
    /// float32 P0, float32 P1, then width*height bytes (intensity * 255), row 0 = south, column 0 = west.
    /// The grid covers the map frame: global x (east) and z (north) from -half to +half, metres from the map centre,
    /// the same metres as the game's GlobalPosition (Cascadia's mission positions are written that way).
    /// Outside the grid the nearest edge value is used.
    /// </summary>
    internal static class RainMap
    {
        private const string ResourceName = "NOCloudDeck.rainmap.bin";
        private static byte[] _cells;
        private static int _width;
        private static int _height;
        private static float _half;
        private static float _p0;
        private static float _p1;

        internal static bool Loaded { get; private set; }

        internal static void Load()
        {
            try
            {
                using (Stream s = typeof(RainMap).Assembly.GetManifestResourceStream(ResourceName))
                {
                    if (s == null)
                    {
                        Plugin.WarnOnce("rainmap-missing", "rain map resource missing; the rain is the same everywhere");
                        return;
                    }
                    using (var r = new BinaryReader(s))
                    {
                        if (new string(r.ReadChars(4)) != "NORM")
                            throw new InvalidDataException("bad magic");
                        int version = r.ReadInt32();
                        if (version != 1)
                            throw new InvalidDataException("version " + version);
                        _width = r.ReadInt32();
                        _height = r.ReadInt32();
                        _half = r.ReadSingle();
                        _p0 = r.ReadSingle();
                        _p1 = r.ReadSingle();
                        if (_width < 2 || _height < 2 || _width > 4096 || _height > 4096 || _half <= 0f)
                            throw new InvalidDataException("bad size");
                        _cells = r.ReadBytes(_width * _height);
                        if (_cells.Length != _width * _height)
                            throw new InvalidDataException("truncated");
                    }
                }
                Loaded = true;
            }
            catch (Exception e)
            {
                Plugin.ErrorOnce("rainmap-load", "loading the rain map", e);
            }
        }

        internal static string Describe()
        {
            return _width + "x" + _height + ", " + (2f * _half / 1000f).ToString("0.#") + " km square, " +
                   _p0.ToString("0") + "-" + _p1.ToString("0") + " mm/yr -> 0-1";
        }

        /// <summary>Rain intensity 0..1 at a global x/z, bilinear between cell centres (as tools/rainmap.py samples).</summary>
        internal static float Sample(float x, float z)
        {
            if (!Loaded)
                return Plugin.FlatIntensity;
            float cellX = 2f * _half / _width;
            float cellZ = 2f * _half / _height;
            float fx = (x + _half) / cellX - 0.5f;
            float fz = (z + _half) / cellZ - 0.5f;
            if (float.IsNaN(fx) || float.IsNaN(fz))
                return Plugin.FlatIntensity;
            int i0 = Mathf.FloorToInt(fx);
            int j0 = Mathf.FloorToInt(fz);
            float tx = fx - i0;
            float tz = fz - j0;
            float a = Cell(i0, j0), b = Cell(i0 + 1, j0), c = Cell(i0, j0 + 1), d = Cell(i0 + 1, j0 + 1);
            return (a * (1f - tx) + b * tx) * (1f - tz) + (c * (1f - tx) + d * tx) * tz;
        }

        private static float Cell(int i, int j)
        {
            i = Mathf.Clamp(i, 0, _width - 1);
            j = Mathf.Clamp(j, 0, _height - 1);
            return _cells[j * _width + i] / 255f;
        }
    }
}
