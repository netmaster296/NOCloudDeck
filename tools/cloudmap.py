"""Regional cloud map for the NOCloudDeck plugin (Cascadia Falling): cloud cover, cloud base, clear-air bonus, lee ramp.

Writes plugin/NOCloudDeck/Resources/cloudmap.bin (embedded in the DLL, read by CloudMap.cs) and, with --preview,
build/cloudmap/ previews. Same frame as rainmap.bin (tools/rainmap.py): a 399,360 m box centred on lat0/lon0 from
design/mission.json, x = (lon - lon0) * 111320 * cos(lat0) east, z = (lat - lat0) * 111320 north, row 0 south,
column 0 west. 128 x 128 cells of 3,120 m.

Source of the numbers: the "steady rain day" composite of 2015-2025 hourly airport reports (Iowa Environmental
Mesonet ASOS archive, 16 stations, 4,670 Oct-Mar hours when Sea-Tac reported steady rain with wind from 150-290
degrees; the per-station medians are in composite_msl.csv of the weather-regional study) for the real stations
(source "S" below), and judgement values (source "V") for places with no airport: the windward Olympic and Cascade
slopes and passes, the Cascade lee slopes (Leavenworth, Cle Elum, Chelan, Methow), the ocean and the BC interior.
The weather-regional study explains each value (its findings F1-F8). Nothing here is measured at the passes.

What the map holds, per cell:
  cover  0..1   fraction of the sky under cloud on a wet day (0.95-1.0 west of the Cascades, about 0.65 east of them)
  base   m MSL  cloud base for a MISSION BASE OF ref_base (900 m); the plugin shifts it with the mission's base
                (LocalBase in CloudMap.cs). Windward slopes hug the terrain (500 m).
  haze   0..1   clear-air bonus: the lee side and the Olympic rain shadow see further under the base
  lee    0..1   smooth((x - crest_x(z)) / 30 km): 0 at and west of the Cascade crest, 1 thirty km east of it.
                CloudMap.RainScale = lerp(1, 0.5, lee) multiplies the rain map (Leavenworth ~0.15, Wenatchee and
                Ellensburg stay near 0): the rain map is annual precipitation, and on rain days the east side is much
                drier than that (weather-regional F8).

The crest line is a polyline through the passes (CREST below). The terrain-hugging windward term is the upslope of
the smoothed heightmap (build/height.f32, row 0 south) in the onshore flow direction (from the south-west) times the
height above 150 m.

cloudmap.bin layout (little-endian): "CLDM", int32 version (1), int32 width, int32 height, float32 half extent (m),
float32 ref_base, float32 base_min, float32 base_max, int32 plane count (4), then the planes, each width*height
bytes, row-major, row 0 = south: cover*255, (base - base_min)/(base_max - base_min)*255, haze*255, lee*255.

Run: python -I tools/cloudmap.py [--preview]     (needs numpy, scipy; Pillow for --preview; the project checkout)
Check: python -I tools/cloudmap.py --check       (samples the written file at named places, like CloudMap.cs does)
"""
from __future__ import annotations

import json
import math
import struct
import sys
from pathlib import Path

import numpy as np
from scipy import ndimage

ROOT = Path(__file__).resolve().parent.parent
RESOURCE = ROOT / "plugin" / "NOCloudDeck" / "Resources" / "cloudmap.bin"
RAINMAP = ROOT / "plugin" / "NOCloudDeck" / "Resources" / "rainmap.bin"
HEIGHT = ROOT / "build" / "height.f32"
OUT = ROOT / "build" / "cloudmap"

M = json.loads((ROOT / "design" / "mission.json").read_text(encoding="utf-8"))
LAT0, LON0, HALF = M["map"]["lat0"], M["map"]["lon0"], M["map"]["half"]
GRID = M["map"]["grid"]  # heightmap samples per side (7801)
LAT_M = 111_320.0
LON_M = 111_320.0 * math.cos(math.radians(LAT0))

N = 128                 # cells per side (3,120 m)
REF_BASE = 900.0        # the mission cloud base the base plane is written for
BASE_MIN, BASE_MAX = 200.0, 2800.0
LEE_KM = 30.0           # width of the lee ramp
IDW_POWER, IDW_SOFT = 2.2, 3000.0


def xz(lat: float, lon: float) -> tuple[float, float]:
    return (lon - LON0) * LON_M, (lat - LAT0) * LAT_M


# name, lat, lon, cover, base m MSL (typical wet day, for a 900 m mission base), source S = station composite, V = judgement
CONTROLS = [
    ("UIL", 47.937, -124.563, 1.00, 520, "S"), ("HQM", 46.971, -123.937, 1.00, 650, "S"),
    ("OLM", 46.973, -122.903, 0.97, 900, "S"), ("SEA", 47.445, -122.314, 0.99, 950, "S"),
    ("BFI", 47.546, -122.315, 0.99, 950, "S"), ("TIW", 47.268, -122.576, 0.99, 900, "S"),
    ("PAE", 47.907, -122.282, 0.98, 750, "S"), ("NUW", 48.352, -122.656, 0.99, 900, "S"),
    ("BLI", 48.793, -122.538, 0.95, 900, "S"), ("CYVR", 49.195, -123.184, 0.97, 1000, "S"),
    ("CYYJ", 48.647, -123.426, 0.95, 1200, "S"), ("FHR", 48.522, -123.024, 0.90, 1050, "S"),
    ("CLM", 48.122, -123.507, 0.87, 1450, "S"), ("EAT", 47.399, -120.207, 0.64, 1500, "S"),
    ("ELN", 47.033, -120.531, 0.66, 1900, "S"), ("YKM", 46.568, -120.544, 0.56, 2100, "S"),
    ("Olympic windward", 47.75, -123.80, 1.0, 500, "V"), ("Stevens crest", 47.745, -121.089, 1.0, 500, "V"),
    ("Snoqualmie crest", 47.425, -121.413, 1.0, 500, "V"), ("N Cascades crest", 48.75, -121.2, 1.0, 550, "V"),
    ("Rainier flank", 46.85, -121.9, 1.0, 500, "V"), ("Hope BC", 49.38, -121.44, 1.0, 700, "V"),
    ("Leavenworth FOB", 47.569, -120.604, 0.80, 1250, "V"), ("Chelan", 48.2, -120.5, 0.78, 1300, "V"),
    ("Methow", 48.6, -120.2, 0.68, 1500, "V"), ("Ocean W", 48.0, -126.0, 1.0, 700, "V"),
    ("Ocean NW", 49.5, -126.0, 1.0, 700, "V"), ("Ocean SW", 46.3, -125.8, 1.0, 700, "V"),
    ("Cle Elum", 47.195, -120.937, 0.85, 1100, "V"), ("Okanogan N", 49.2, -120.3, 0.65, 1600, "V"),
    ("Yakima S", 46.0, -120.8, 0.55, 2100, "V"),
]

PLACES = [  # name, lat, lon (the validation list; Leavenworth is the FOB position in design/mission.json)
    ("Forks", 47.950222, -124.386165), ("Seattle", 47.6038, -122.3301), ("Everett", 47.9793, -122.2127),
    ("Sequim", 48.0849, -123.1097), ("Victoria", 48.4283, -123.3650), ("Stevens Pass", 47.745, -121.089),
    ("Leavenworth", 47.56874, -120.6037), ("Wenatchee", 47.4235, -120.3103), ("Ellensburg", 46.9971, -120.5451),
]


def cell_centres() -> tuple[np.ndarray, np.ndarray]:
    c = -HALF + (np.arange(N) + 0.5) * (2 * HALF / N)
    return np.meshgrid(c, c)  # X[j, i], Z[j, i]; j = row (south first)


def idw(px, pz, values, X, Z):
    num = np.zeros_like(X)
    den = np.zeros_like(X)
    for k in range(len(values)):
        w = (np.hypot(X - px[k], Z - pz[k]) + IDW_SOFT) ** -IDW_POWER
        num += w * values[k]
        den += w
    return num / den


def smooth01(x):
    x = np.clip(x, 0.0, 1.0)
    return x * x * (3 - 2 * x)


def terrain():
    """Smoothed terrain (m, sea = 0) on a 1301 x 1301 grid, and its pixel size."""
    h = np.memmap(HEIGHT, dtype=np.float32, mode="r", shape=(GRID, GRID))
    step = 6
    coarse = np.maximum(np.asarray(h[::step, ::step], dtype=np.float32), 0.0)
    pitch = M["map"]["pitch"] * step
    return coarse, pitch


# The Cascade crest (the divide), as lat, lon points through the passes: Stevens Pass and Snoqualmie Pass are the
# project's own control points, the others are approximate positions of Chinook, White and Cascade Pass and the
# Pasayten edge (a few km either way; the lee ramp is 30 km wide, so this does not matter).
CREST = [(46.0, -121.45), (46.5, -121.40), (46.87, -121.51), (47.2, -121.45), (47.425, -121.413),
         (47.745, -121.089), (48.0, -121.10), (48.25, -120.95), (48.47, -121.07), (48.75, -121.2),
         (49.0, -121.15), (50.0, -121.15)]


def crest_x(Z: np.ndarray) -> np.ndarray:
    """x of the Cascade crest for every row of the output grid (length N)."""
    lat = np.array([c[0] for c in CREST])
    x = np.array([xz(c[0], c[1])[0] for c in CREST])
    return np.interp(LAT0 + Z[:, 0] / LAT_M, lat, x)


def build() -> dict:
    X, Z = cell_centres()
    px = np.array([xz(c[1], c[2])[0] for c in CONTROLS])
    pz = np.array([xz(c[1], c[2])[1] for c in CONTROLS])
    cover = idw(px, pz, np.array([c[3] for c in CONTROLS]), X, Z)
    base = idw(px, pz, np.array([c[4] for c in CONTROLS]), X, Z)

    coarse, pitch = terrain()
    crest = crest_x(Z)                                   # per row
    lee = smooth01((X - crest[:, None]) / (LEE_KM * 1000.0))

    # terrain-hugging windward base: upslope of the smoothed terrain in the onshore flow (from the south-west, i.e.
    # towards the north-east) times the height above 150 m; there the base falls towards 500 m (plus a little with height)
    sm = ndimage.gaussian_filter(coarse, 6000.0 / pitch)
    gy, gx = np.gradient(sm, pitch)                                     # d/dz, d/dx, metres per metre
    along = (gx + gy) / math.sqrt(2.0)                                  # slope along the flow
    n = coarse.shape[0]
    fi = (X + HALF) / pitch
    fj = (Z + HALF) / pitch
    hs = ndimage.map_coordinates(sm, [fj, fi], order=1, mode="nearest")
    up = ndimage.map_coordinates(along, [fj, fi], order=1, mode="nearest")
    windward = smooth01(up / 0.04)
    height_w = smooth01((hs - 150.0) / 700.0)
    hug = np.clip(500.0 + 0.1 * np.maximum(hs - 500.0, 0.0), 500.0, 650.0)
    k = windward * height_w * (1.0 - smooth01((X - crest[:, None]) / 8000.0))  # none on the lee side of the crest
    k = ndimage.gaussian_filter(k, 1.2)                                         # the ridges are not single cells
    base = base - k * np.maximum(base - hug, 0.0)

    # clear-air bonus: all of it on the lee side, half of it in the Olympic rain shadow (a high base west of the crest)
    shadow = 0.5 * smooth01((base - 900.0) / 250.0) * (1.0 - lee)
    haze = np.maximum(lee, shadow)

    cover = np.clip(cover, 0.0, 1.0)
    base = np.clip(base, BASE_MIN, BASE_MAX)
    return {"X": X, "Z": Z, "cover": cover, "base": base, "haze": haze, "lee": lee, "crest": crest, "hs": hs,
            "windward": k}


def quantise(a: np.ndarray, lo: float, hi: float) -> np.ndarray:
    return np.clip(np.round((a - lo) / (hi - lo) * 255.0), 0, 255).astype(np.uint8)


def write(f: dict) -> None:
    RESOURCE.parent.mkdir(parents=True, exist_ok=True)
    planes = [quantise(f["cover"], 0, 1), quantise(f["base"], BASE_MIN, BASE_MAX), quantise(f["haze"], 0, 1),
              quantise(f["lee"], 0, 1)]
    with open(RESOURCE, "wb") as fh:
        fh.write(b"CLDM")
        fh.write(struct.pack("<iiiffffi", 1, N, N, HALF, REF_BASE, BASE_MIN, BASE_MAX, len(planes)))
        for p in planes:
            fh.write(np.ascontiguousarray(p).tobytes())
    print(f"wrote {RESOURCE} ({RESOURCE.stat().st_size} bytes)")


# ------------------------------------------------------------------------------------------ reading it back
def read():
    raw = RESOURCE.read_bytes()
    assert raw[:4] == b"CLDM"
    ver, w, h, half, ref, bmin, bmax, np_ = struct.unpack("<iiiffffi", raw[4:36])
    assert ver == 1 and np_ == 4 and w == h
    planes = [np.frombuffer(raw, np.uint8, w * h, 36 + i * w * h).reshape(h, w) / 255.0 for i in range(np_)]
    return dict(w=w, half=half, ref=ref, bmin=bmin, bmax=bmax, cover=planes[0],
                base=bmin + planes[1] * (bmax - bmin), haze=planes[2], lee=planes[3])


def sample(a: np.ndarray, half: float, x: float, z: float) -> float:
    """Bilinear between cell centres, edge values outside: the same as CloudMap.Sample."""
    w = a.shape[1]
    cell = 2 * half / w
    fx, fz = (x + half) / cell - 0.5, (z + half) / cell - 0.5
    i0, j0 = math.floor(fx), math.floor(fz)
    tx, tz = fx - i0, fz - j0

    def c(i, j):
        return a[min(max(j, 0), w - 1), min(max(i, 0), w - 1)]

    return float((c(i0, j0) * (1 - tx) + c(i0 + 1, j0) * tx) * (1 - tz) + (c(i0, j0 + 1) * (1 - tx) + c(i0 + 1, j0 + 1) * tx) * tz)


def rainmap():
    raw = RAINMAP.read_bytes()
    assert raw[:4] == b"NORM"
    _, w, h, half, _, _ = struct.unpack("<iiifff", raw[4:28])
    return np.frombuffer(raw, np.uint8, w * h, 28).reshape(h, w) / 255.0, half


def local_base(base_map, ref, b, top, w=1.0, offset_scale=1.0):
    """CloudMap.LocalBase (see the C# for the reasoning): the mission base b plus the map's offset from ref, scaled by
    the wetness w, kept at least 200 m up and at most 600 m under the top (but never under the mission base itself)."""
    lb = b + (base_map - ref) * w * offset_scale
    cap = max(b, top - 600.0)
    return max(200.0, min(lb, cap))


# ---- the puff gate noise of CloudMap.Gate, in numpy (same integer hash), to check that "gate < cover" keeps about `cover`
def _hash(ix, iz, seed):
    with np.errstate(over="ignore"):
        h = (ix.astype(np.uint32) * np.uint32(374761393) + iz.astype(np.uint32) * np.uint32(668265263)
             + np.uint32(seed) * np.uint32(2246822519) + np.uint32(1442695041))
        h = (h ^ (h >> np.uint32(13))) * np.uint32(1274126177)
        h = h ^ (h >> np.uint32(16))
    return (h & np.uint32(0xFFFFFF)).astype(np.float64) / 16777216.0


def _value_noise(fx, fz, seed):
    ix, iz = np.floor(fx).astype(np.int64), np.floor(fz).astype(np.int64)
    tx, tz = fx - ix, fz - iz
    tx, tz = tx * tx * (3 - 2 * tx), tz * tz * (3 - 2 * tz)
    a, b = _hash(ix, iz, seed), _hash(ix + 1, iz, seed)
    c, d = _hash(ix, iz + 1, seed), _hash(ix + 1, iz + 1, seed)
    return (a * (1 - tx) + b * tx) * (1 - tz) + (c * (1 - tx) + d * tx) * tz


def gate_raw(x, z):
    return 0.65 * _value_noise(x / 4500.0, z / 4500.0, 11) + 0.35 * _value_noise(x / 1800.0 + 31.7, z / 1800.0 - 17.3, 29)


def gate(x, z, sd=0.158):
    return 1.0 / (1.0 + np.exp(-1.702 * (gate_raw(x, z) - 0.5) / sd))


def check_gate() -> None:
    rng = np.random.default_rng(1)
    x = rng.uniform(-2e5, 2e5, 400_000)
    z = rng.uniform(-2e5, 2e5, 400_000)
    n = gate_raw(x, z)
    print(f"gate noise: mean {n.mean():.3f}, sd {n.std():.3f} (CloudMap.Gate assumes 0.5 +- 0.158)")
    g = gate(x, z)
    print("fraction of the sky kept at cover " + "  ".join(f"{c:.2f}->{(g < c).mean():.3f}" for c in (0.3, 0.5, 0.65, 0.8, 0.9, 0.97, 1.0)))


def check() -> None:
    check_gate()
    f = read()
    rm, rhalf = rainmap()
    print(f"{'place':14s} {'x_km':>7s} {'z_km':>7s} {'cover':>5s} {'map_base':>8s} {'LB@900':>7s} {'LB@1500':>8s} {'haze':>5s} "
          f"{'lee':>5s} {'rain_map':>8s} {'rain_x_lee':>10s}")
    for name, lat, lon in PLACES:
        x, z = xz(lat, lon)
        cv = sample(f["cover"], f["half"], x, z)
        bs = sample(f["base"], f["half"], x, z)
        hz = sample(f["haze"], f["half"], x, z)
        le = sample(f["lee"], f["half"], x, z)
        r = sample(rm, rhalf, x, z)
        scale = 1.0 + (0.5 - 1.0) * le
        print(f"{name:14s} {x / 1000:7.1f} {z / 1000:7.1f} {cv:5.2f} {bs:8.0f} {local_base(bs, f['ref'], 900, 2400):7.0f} "
              f"{local_base(bs, f['ref'], 1500, 2400):8.0f} {hz:5.2f} {le:5.2f} {r:8.2f} {r * scale:10.2f}")


def preview(f: dict) -> None:
    from PIL import Image
    OUT.mkdir(parents=True, exist_ok=True)

    def img(a, lo, hi, name):
        v = np.clip((a - lo) / (hi - lo), 0, 1)
        rgb = np.stack([v * 255, (1 - abs(v - 0.5) * 2) * 200 + 55, (1 - v) * 255], -1).astype(np.uint8)
        Image.fromarray(rgb[::-1]).resize((640, 640), Image.NEAREST).save(OUT / name)

    img(f["cover"], 0.5, 1.0, "cover.png")
    img(f["base"], 400, 2200, "base.png")
    img(f["haze"], 0, 1, "haze.png")
    img(f["lee"], 0, 1, "lee.png")
    print("previews in", OUT)


def main(argv: list[str]) -> None:
    if "--check" in argv:
        check()
        return
    f = build()
    write(f)
    cx = f["crest"]
    zs = [(48.75, "N Cascades"), (47.745, "Stevens"), (47.425, "Snoqualmie"), (46.85, "Rainier")]
    for lat, name in zs:
        z = xz(lat, LON0)[1]
        j = int(np.clip((z + HALF) / (2 * HALF / N) - 0.5, 0, N - 1))
        print(f"crest x at {name} (lat {lat}): {cx[j] / 1000:.1f} km")
    if "--preview" in argv:
        preview(f)
    check()


if __name__ == "__main__":
    main(sys.argv[1:])
