"""Regional rain-intensity map for the NOCloudDeck plugin (Cascadia Falling).

Source: CHELSA V2.1 bio12 (annual precipitation, 1981-2010, 30 arc-second grid; EnviDat lists CC0 1.0, credit given
anyway, see source/climate/SOURCE.txt) corrected with station normals (NOAA NCEI 1991-2020, public domain; ECCC
1981-2010, Open Government Licence - Canada). The global GeoTIFF is
655 MB, so this script reads only the rows that cover the map with HTTP range requests (the file is stored as one
deflate-compressed strip per row) and keeps that lossless window under source/climate/ (see SOURCE.txt there).

Steps:
  1. fetch (once) the window lat 46.0..50.0 N, lon 126.0..120.0 W -> source/climate/CHELSA_bio12_..._window.tif
  2. fill any no-data cells (none in this window: CHELSA has values over the sea too) by normalized convolution,
  2b. correct CHELSA with station normals: CHELSA is too wet in the Olympic rain shadow (Sequim, Port Townsend,
     Whidbey), so the log ratio station/CHELSA at each station (NCEI 1991-2020 COOP/first-order/SNOTEL stations,
     ECCC 1981-2010 BC stations, cached under source/climate/) is spread by inverse-distance weighting against a
     zero background and applied (fades out 20-40 km from the nearest station),
  3. average it into a 256 x 256 grid in the project's map frame (design/mission.json "map": a 399,360 m box centred
     on lat0/lon0, x = (lon - lon0) * 111320 * cos(lat0) east, z = (lat - lat0) * 111320 north, row 0 south; the
     game's GlobalPosition x/z are these same metres, see tools/airbases.py to_xz and build_mission.py globalPosition),
  4. intensity = clamp(log(P / P0) / log(P1 / P0), 0, 1), P0 = 300 mm (none), P1 = 2,500 mm (full),
  5. smooth slightly (Gaussian, sigma 0.8 cell = 1.25 km),
  6. write plugin/NOCloudDeck/Resources/rainmap.bin (embedded in the DLL) and build/rainmap/ previews + numbers.

rainmap.bin layout (little-endian): "NORM" magic, int32 version (1), int32 width, int32 height, float32 half extent
(m), float32 P0, float32 P1, then width*height bytes, intensity*255, row-major, row 0 = south (z = -half), column 0 =
west (x = -half). Cell (i, j) covers x in [-half + i*cell, -half + (i+1)*cell].

Run: python -I tools/rainmap.py [--refetch]
Needs numpy, scipy, Pillow, requests and tifffile (BSD-3-Clause; pip install tifffile), and the project checkout
(it reads design/mission.json and writes into plugin/ and build/rainmap/).
"""
from __future__ import annotations

import json
import math
import struct
import sys
import zlib
from pathlib import Path

import numpy as np
import requests
from PIL import Image, ImageDraw, ImageFont
from scipy import ndimage

ROOT = Path(__file__).resolve().parent.parent
CLIMATE = ROOT / "source" / "climate"
OUT = ROOT / "build" / "rainmap"
RESOURCE = ROOT / "plugin" / "NOCloudDeck" / "Resources" / "rainmap.bin"

URL = "https://os.zhdk.cloud.switch.ch/chelsav2/GLOBAL/climatologies/1981-2010/bio/CHELSA_bio12_1981-2010_V.2.1.tif"
WINDOW_TIF = CLIMATE / "CHELSA_bio12_1981-2010_V.2.1_cascadia_window.tif"
WINDOW_JSON = CLIMATE / "CHELSA_bio12_1981-2010_V.2.1_cascadia_window.json"
LAT_N, LAT_S, LON_W, LON_E = 50.0, 46.0, -126.0, -120.0

N = 256          # rain grid cells per side (1,560 m each)
P0, P1 = 300.0, 2500.0
SMOOTH_SIGMA = 0.8

M = json.loads((ROOT / "design" / "mission.json").read_text(encoding="utf-8"))
LAT0, LON0, HALF = M["map"]["lat0"], M["map"]["lon0"], M["map"]["half"]
LAT_M = 111_320.0
LON_M = 111_320.0 * math.cos(math.radians(LAT0))

TOWNS = [  # name, lat, lon (from source/osm/network.json places unless noted)
    ("Forks", 47.950222, -124.386165),
    ("Aberdeen", 46.975371, -123.815722),
    ("Sequim", 48.0849312, -123.1096706),
    ("Port Townsend", 48.1179702, -122.769544),
    ("Victoria", 48.4283182, -123.364953),
    ("Seattle", 47.6038321, -122.330062),
    ("Tacoma", 47.2557587, -122.4418996),
    ("Everett", 47.9793437, -122.2127011),
    ("Bellingham", 48.7544012, -122.478836),
    ("Vancouver", 49.2608724, -123.113952),
    ("Hope", 49.3799779, -121.4415851),
    ("Snoqualmie Pass", 47.4245, -121.4134),  # not in the OSM extract; summit village, approximate
    ("Ellensburg", 46.9970635, -120.545122),
    ("Yakima", 46.601557, -120.510842),
]
EXTRA = [  # shown on the preview only
    ("Port Angeles", 48.118146, -123.430741),
    ("Oak Harbor", 48.2931559, -122.643224),
    ("Olympia", 47.0451022, -122.8950075),
    ("Mt Olympus", 47.8013, -123.7108),  # approximate summit position
]


# ---------------------------------------------------------------------------------------------- fetch (once)
def _get(a: int, n: int) -> bytes:
    r = requests.get(URL, headers={"Range": f"bytes={a}-{a + n - 1}"}, timeout=120)
    r.raise_for_status()
    if len(r.content) != n:
        raise RuntimeError(f"range {a}+{n}: got {len(r.content)} bytes")
    return r.content


def _ifd(head: bytes) -> dict:
    """Classic little-endian TIFF, first IFD; returns {tag: (type, count, value-or-offset bytes)}."""
    if head[:4] != b"II*\x00":
        raise RuntimeError("expected a classic little-endian TIFF")
    off = struct.unpack("<I", head[4:8])[0]
    n = struct.unpack("<H", head[off:off + 2])[0]
    tags = {}
    for i in range(n):
        e = head[off + 2 + 12 * i: off + 14 + 12 * i]
        tag, typ, cnt = struct.unpack("<HHI", e[:8])
        tags[tag] = (typ, cnt, e[8:12])
    return tags


def _tag_array(tags: dict, tag: int, fetch) -> np.ndarray:
    typ, cnt, raw = tags[tag]
    dt = {3: "<u2", 4: "<u4", 12: "<f8", 16: "<u8"}[typ]
    size = np.dtype(dt).itemsize * cnt
    data = raw[:size] if size <= 4 else fetch(struct.unpack("<I", raw)[0], size)
    return np.frombuffer(data, dtype=dt, count=cnt)


def _tag_str(tags: dict, tag: int, fetch) -> str:
    typ, cnt, raw = tags[tag]
    data = raw[:cnt] if cnt <= 4 else fetch(struct.unpack("<I", raw)[0], cnt)
    return data.rstrip(b"\x00").decode("ascii", "replace")


def fetch_window() -> None:
    import tifffile

    CLIMATE.mkdir(parents=True, exist_ok=True)
    head = _get(0, 65536)
    tags = _ifd(head)
    width = int(struct.unpack("<H", tags[256][2][:2])[0])
    height = int(struct.unpack("<H", tags[257][2][:2])[0])
    bits = struct.unpack("<H", tags[258][2][:2])[0]
    comp = struct.unpack("<H", tags[259][2][:2])[0]
    pred = struct.unpack("<H", tags[317][2][:2])[0] if 317 in tags else 1
    rps = struct.unpack("<H", tags[278][2][:2])[0] if tags[278][0] == 3 else struct.unpack("<I", tags[278][2])[0]
    if (bits, comp, rps) != (16, 8, 1):
        raise RuntimeError(f"unexpected layout bits={bits} compression={comp} rows/strip={rps}")
    offsets = _tag_array(tags, 273, _get).astype(np.int64)
    counts = _tag_array(tags, 279, _get).astype(np.int64)
    scale = _tag_array(tags, 33550, _get)
    tie = _tag_array(tags, 33922, _get)
    gdal_meta = _tag_str(tags, 42112, _get) if 42112 in tags else ""
    nodata = _tag_str(tags, 42113, _get) if 42113 in tags else ""
    res_x, res_y = float(scale[0]), float(scale[1])
    lon_ul, lat_ul = float(tie[3]), float(tie[4])
    r0 = int(math.floor((lat_ul - LAT_N) / res_y))
    r1 = int(math.ceil((lat_ul - LAT_S) / res_y))
    c0 = int(math.floor((LON_W - lon_ul) / res_x))
    c1 = int(math.ceil((LON_E - lon_ul) / res_x))
    print(f"CHELSA {width}x{height}, res {res_x:.6f} deg, rows {r0}..{r1}, cols {c0}..{c1}")
    rows = []
    # strips are fetched in runs of contiguous byte ranges
    r = r0
    while r < r1:
        r_end = min(r + 64, r1)
        a = int(offsets[r])
        contiguous = all(offsets[k + 1] == offsets[k] + counts[k] for k in range(r, r_end - 1))
        if not contiguous:
            r_end = r + 1
        n = int(offsets[r_end - 1] + counts[r_end - 1] - a)
        blob = _get(a, n)
        for k in range(r, r_end):
            s = int(offsets[k] - a)
            raw = zlib.decompress(blob[s:s + int(counts[k])])
            row = np.frombuffer(raw, dtype="<u2", count=width).copy()
            if pred == 2:
                row = np.cumsum(row, dtype=np.uint16)  # horizontal differencing, modulo 2^16
            rows.append(row[c0:c1])
        r = r_end
        print(f"  rows to {r}", flush=True)
    grid = np.stack(rows)
    tifffile.imwrite(WINDOW_TIF, grid, compression="zlib", predictor=True,
                     description="CHELSA V2.1 bio12 1981-2010, lossless window of the global file (raw uint16 values)")
    WINDOW_JSON.write_text(json.dumps({
        "url": URL, "global_size": [width, height], "rows": [r0, r1], "cols": [c0, c1],
        "res_deg": [res_x, res_y], "upper_left_corner": [lon_ul + c0 * res_x, lat_ul - r0 * res_y],
        "gdal_metadata": gdal_meta, "nodata": nodata,
        "note": "row 0 = north edge; pixel (r, c) centre lon = ul_lon + (c + 0.5) * res, lat = ul_lat - (r + 0.5) * res",
    }, indent=1), encoding="utf-8")
    print("wrote", WINDOW_TIF, grid.shape)


# ---------------------------------------------------------------------------------------------- processing
def load_precip() -> tuple[np.ndarray, dict]:
    import tifffile

    meta = json.loads(WINDOW_JSON.read_text(encoding="utf-8"))
    raw = tifffile.imread(WINDOW_TIF).astype(np.float64)
    nod = meta.get("nodata", "").strip()
    valid = np.ones(raw.shape, bool)
    if nod:
        valid &= raw != float(nod)
    # GDAL scale/offset (CHELSA stores bio12 as uint16 with scale 0.1 kg m-2 a-1)
    scale, offset = 1.0, 0.0
    gm = meta.get("gdal_metadata", "")
    import re
    m = re.search(r'name="scale"[^>]*>([^<]+)<', gm, re.I) or re.search(r'scale_factor[^>]*>([^<]+)<', gm, re.I)
    if m:
        scale = float(m.group(1))
    m = re.search(r'name="offset"[^>]*>([^<]+)<', gm, re.I) or re.search(r'add_offset[^>]*>([^<]+)<', gm, re.I)
    if m:
        offset = float(m.group(1))
    meta["applied_scale"], meta["applied_offset"] = scale, offset
    p = raw * scale + offset
    p[~valid] = np.nan
    return p, meta


def fill_sea(p: np.ndarray) -> np.ndarray:
    """Fill no-data cells with a distance-weighted blend of nearby land values (normalized convolution, growing
    sigma); land values are kept exactly."""
    out = p.copy()
    mask = np.isfinite(p)
    val = np.where(mask, np.log(np.maximum(p, 1.0)), 0.0)
    w = mask.astype(np.float64)
    filled = mask.copy()
    est = np.zeros_like(val)
    for sigma in (2, 4, 8, 16, 32, 64, 128):
        num = ndimage.gaussian_filter(val * w, sigma, mode="nearest")
        den = ndimage.gaussian_filter(w, sigma, mode="nearest")
        ok = (den > 1e-3) & ~filled
        est[ok] = num[ok] / den[ok]
        filled |= ok
        if filled.all():
            break
    if not filled.all():
        est[~filled] = np.log(np.nanmedian(p))
    out[~mask] = np.exp(est[~mask])
    return out


# ---------------------------------------------------------------------------------------------- station normals
NCEI_DIR = CLIMATE / "ncei_normals"
NCEI_BASE = "https://www.ncei.noaa.gov/data/normals-annualseasonal/1991-2020"
ECCC_JSON = CLIMATE / "eccc_normals_1981-2010_total_precip.json"
ECCC_URL = "https://api.weather.gc.ca/collections/climate-normals/items"
BOX = (LAT0 - HALF / LAT_M - 0.1, LAT0 + HALF / LAT_M + 0.1, LON0 - HALF / LON_M - 0.1, LON0 + HALF / LON_M + 0.1)


def fetch_stations() -> list[dict]:
    """Annual precipitation normals of the stations in the map box (cached):
    NCEI U.S. Climate Normals 1991-2020 (COOP USC*, first-order USW*, SNOTEL USS* stations; public domain) and
    ECCC Canadian Climate Normals 1981-2010 (normal 56 'Total precipitation mm', month 13 = annual; OGL-Canada)."""
    import csv
    import io

    NCEI_DIR.mkdir(parents=True, exist_ok=True)
    inv = NCEI_DIR / "inventory_30yr.txt"
    if not inv.exists():
        r = requests.get(f"{NCEI_BASE}/doc/inventory_30yr.txt", timeout=120)
        r.raise_for_status()
        inv.write_bytes(r.content)
    lat_s, lat_n, lon_w, lon_e = BOX
    out = []
    for line in inv.read_text(errors="replace").splitlines():
        parts = line.split()
        if len(parts) < 4 or not parts[0][:3] in ("USC", "USW", "USS"):
            continue
        try:
            lat, lon = float(parts[1]), float(parts[2])
        except ValueError:
            continue
        if not (lat_s <= lat <= lat_n and lon_w <= lon <= lon_e):
            continue
        f = NCEI_DIR / f"{parts[0]}.csv"
        if not f.exists():
            r = requests.get(f"{NCEI_BASE}/access/{parts[0]}.csv", timeout=60)
            if r.status_code == 404:
                f.write_bytes(b"")  # remember the miss
                continue
            r.raise_for_status()
            f.write_bytes(r.content)
        text = f.read_text(errors="replace")
        if not text.strip():
            continue
        row = next(csv.DictReader(io.StringIO(text)), None)
        if not row:
            continue
        v = (row.get("ANN-PRCP-NORMAL") or "").strip()
        comp = (row.get("comp_flag_ANN-PRCP-NORMAL") or "").strip()
        try:
            mm = float(v) * 25.4
        except ValueError:
            continue
        if mm <= 0 or comp not in ("S", "R", "P", "C"):  # complete, standard, provisional or completed
            continue
        out.append({"src": "NCEI", "id": parts[0], "name": (row.get("NAME") or "").strip(), "lat": lat, "lon": lon,
                    "mm": mm, "flag": comp})
    if not ECCC_JSON.exists():
        feats, offset = [], 0
        while True:
            r = requests.get(ECCC_URL, params={"f": "json", "bbox": f"{lon_w},{lat_s},{lon_e},{lat_n}", "NORMAL_ID": 56,
                                               "MONTH": 13, "limit": 500, "offset": offset}, timeout=120)
            r.raise_for_status()
            d = r.json()
            feats += d.get("features", [])
            offset += d.get("numberReturned", 0)
            if not d.get("numberReturned") or offset >= d.get("numberMatched", 0):
                break
        ECCC_JSON.write_text(json.dumps(feats, indent=0), encoding="utf-8")
    for ft in json.loads(ECCC_JSON.read_text(encoding="utf-8")):
        p = ft["properties"]
        if p.get("E_NORMAL_ELEMENT_NAME") != "Total precipitation mm" or p.get("VALUE") is None:
            continue
        lon, lat = ft["geometry"]["coordinates"][:2]
        if p.get("PROVINCE_CODE") != "BC" or not (lat_s <= lat <= lat_n and lon_w <= lon <= lon_e):
            continue
        out.append({"src": "ECCC", "id": p["CLIMATE_IDENTIFIER"], "name": p["STATION_NAME"], "lat": lat, "lon": lon,
                    "mm": float(p["VALUE"]), "flag": p.get("NORMAL_CODE", "")})
    return out


RESID_CLAMP = 0.8    # |ln(station / CHELSA)| at most (a factor of 2.2)
RESID_RANGE = 20e3   # m: weight of a station falls to that of the zero-residual background at about this distance
RESID_CORE = 3e3     # m: softening so a station does not pin a spike


def station_correction(p: np.ndarray, meta: dict, stations: list[dict]) -> tuple[np.ndarray, list[dict]]:
    """CHELSA is wetter than the station normals in the Olympic rain shadow (for example Sequim 2 E 428 mm vs
    CHELSA 729 mm at the same point). Correct it with the stations' log residuals, spread by inverse-distance
    weighting against a zero-residual background so the correction fades out ~20-40 km from the nearest station."""
    res_x, res_y = meta["res_deg"]
    lon_ul, lat_ul = meta["upper_left_corner"]
    used = []
    for s in stations:
        r = (lat_ul - s["lat"]) / res_y - 0.5
        c = (s["lon"] - lon_ul) / res_x - 0.5
        if not (0 <= r < p.shape[0] - 1 and 0 <= c < p.shape[1] - 1):
            continue
        g = float(ndimage.map_coordinates(p, [[r], [c]], order=1)[0])
        if not np.isfinite(g) or g <= 0:
            continue
        s = dict(s, chelsa_mm=round(g), resid=float(np.clip(math.log(s["mm"] / g), -RESID_CLAMP, RESID_CLAMP)))
        used.append(s)
    # residual field on the window grid (computed on a 1/4-resolution grid, then interpolated)
    step = 4
    rr = np.arange(0, p.shape[0], step)
    cc = np.arange(0, p.shape[1], step)
    lat = lat_ul - (rr + 0.5) * res_y
    lon = lon_ul + (cc + 0.5) * res_x
    LON, LAT = np.meshgrid(lon, lat)
    num = np.zeros(LAT.shape)
    den = np.full(LAT.shape, 1.0 / RESID_RANGE ** 2)
    for s in used:
        d2 = ((LAT - s["lat"]) * LAT_M) ** 2 + ((LON - s["lon"]) * 111_320.0 * np.cos(np.radians(LAT))) ** 2
        w = 1.0 / (d2 + RESID_CORE ** 2)
        num += w * s["resid"]
        den += w
    field = num / den
    full = ndimage.zoom(field, (p.shape[0] / field.shape[0], p.shape[1] / field.shape[1]), order=1)
    full = full[:p.shape[0], :p.shape[1]]
    if full.shape != p.shape:
        full = np.pad(full, ((0, p.shape[0] - full.shape[0]), (0, p.shape[1] - full.shape[1])), mode="edge")
    return p * np.exp(full), used


def to_map_grid(p: np.ndarray, meta: dict, sub: int = 4) -> np.ndarray:
    """Mean annual precipitation (mm) per rain-grid cell (sub x sub bilinear samples per cell)."""
    res_x, res_y = meta["res_deg"]
    lon_ul, lat_ul = meta["upper_left_corner"]
    cell = 2 * HALF / N
    offs = (np.arange(sub) + 0.5) / sub
    acc = np.zeros((N, N))
    for oz in offs:
        for ox in offs:
            x = -HALF + (np.arange(N) + ox) * cell
            z = -HALF + (np.arange(N) + oz) * cell
            X, Z = np.meshgrid(x, z)  # row = z (south first), col = x
            lat = LAT0 + Z / LAT_M
            lon = LON0 + X / LON_M
            r = (lat_ul - lat) / res_y - 0.5
            c = (lon - lon_ul) / res_x - 0.5
            acc += ndimage.map_coordinates(p, [r, c], order=1, mode="nearest")
    return acc / (sub * sub)


def intensity(pmm: np.ndarray) -> np.ndarray:
    return np.clip(np.log(np.maximum(pmm, 1.0) / P0) / math.log(P1 / P0), 0.0, 1.0)


def xz(lat: float, lon: float) -> tuple[float, float]:
    return (lon - LON0) * LON_M, (lat - LAT0) * LAT_M


def sample(grid: np.ndarray, x: float, z: float) -> float:
    """Bilinear sample at cell centres, the same rule the plugin uses (RainMap.Sample)."""
    cell = 2 * HALF / N
    fx = (x + HALF) / cell - 0.5
    fz = (z + HALF) / cell - 0.5
    i0 = int(math.floor(fx))
    j0 = int(math.floor(fz))
    tx, tz = fx - i0, fz - j0
    def g(i, j):
        return float(grid[min(max(j, 0), N - 1), min(max(i, 0), N - 1)])
    return (g(i0, j0) * (1 - tx) * (1 - tz) + g(i0 + 1, j0) * tx * (1 - tz)
            + g(i0, j0 + 1) * (1 - tx) * tz + g(i0 + 1, j0 + 1) * tx * tz)


def coastline(px: int) -> np.ndarray | None:
    """Boolean land mask px x px (row 0 north) from build/height.f32 (7801^2 float32, row 0 south), if present."""
    hp = ROOT / "build" / "height.f32"
    metaf = ROOT / "build" / "meta.json"
    if not hp.exists() or not metaf.exists():
        return None
    meta = json.loads(metaf.read_text())
    n = int(meta["nx"])
    h = np.memmap(hp, dtype="<f4", mode="r", shape=(n, n))
    idx = np.linspace(0, n - 1, px).round().astype(int)
    sub = np.asarray(h[np.ix_(idx, idx)])
    return (sub > 0.5)[::-1]


def colour(i: np.ndarray) -> np.ndarray:
    """Light grey (dry) -> blue -> deep violet (wettest)."""
    stops = np.array([[0.00, 236, 226, 200], [0.20, 205, 215, 200], [0.45, 120, 175, 215],
                      [0.70, 40, 100, 190], [0.90, 40, 40, 140], [1.00, 70, 10, 100]])
    out = np.zeros(i.shape + (3,))
    for k in range(3):
        out[..., k] = np.interp(i, stops[:, 0], stops[:, k + 1])
    return out.astype(np.uint8)


def preview(grid: np.ndarray, pmm: np.ndarray, rows: list) -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    px = 1024
    img_i = np.array(Image.fromarray((grid[::-1] * 255).astype(np.uint8)).resize((px, px), Image.BILINEAR)) / 255.0
    rgb = colour(img_i)
    land = coastline(px)
    if land is not None:
        edge = land ^ ndimage.binary_erosion(land)
        rgb[edge] = (20, 20, 20)
    im = Image.fromarray(rgb)
    d = ImageDraw.Draw(im)
    try:
        font = ImageFont.load_default(size=15)
    except TypeError:
        font = ImageFont.load_default()
    for name, lat, lon in TOWNS + EXTRA:
        x, z = xz(lat, lon)
        u = (x + HALF) / (2 * HALF) * px
        v = (HALF - z) / (2 * HALF) * px
        val = sample(grid, x, z)
        d.ellipse([u - 4, v - 4, u + 4, v + 4], fill=(255, 255, 255), outline=(0, 0, 0))
        label = f"{name} {val:.2f}"
        tw = d.textlength(label, font=font)
        tx = u + 7 if u + 7 + tw < px - 4 else u - 7 - tw
        ty = v - 9 if name != "Sequim" else v + 3  # Sequim sits just under Port Angeles' label
        d.text((tx, ty), label, fill=(0, 0, 0), font=font, stroke_width=2, stroke_fill=(255, 255, 255))
    # legend
    for k in range(200):
        d.line([(20 + k, px - 40), (20 + k, px - 25)], fill=tuple(int(c) for c in colour(np.array(k / 199))))
    d.text((20, px - 62), f"rain intensity 0 (<= {P0:.0f} mm/yr) .. 1 (>= {P1:.0f} mm/yr)", fill=(0, 0, 0), font=font,
           stroke_width=2, stroke_fill=(255, 255, 255))
    d.text((20, 12), "NOCloudDeck rain map - CHELSA V2.1 bio12 1981-2010 (Karger et al.), corrected to NOAA NCEI 1991-2020 "
           "and ECCC 1981-2010 station normals", fill=(0, 0, 0), font=font, stroke_width=2, stroke_fill=(255, 255, 255))
    im.save(OUT / "rainmap_preview.png")
    # annual precipitation picture (log scale) for checking the source itself
    lp = np.clip((np.log10(np.maximum(pmm[::-1], 1)) - 2.0) / 2.0, 0, 1)
    Image.fromarray((lp * 255).astype(np.uint8)).resize((512, 512), Image.BILINEAR).save(OUT / "precip_log_mm.png")


def main() -> None:
    if "--refetch" in sys.argv or not WINDOW_TIF.exists() or not WINDOW_JSON.exists():
        fetch_window()
    p, meta = load_precip()
    print(f"scale {meta['applied_scale']} offset {meta['applied_offset']}; land P range "
          f"{np.nanmin(p):.0f}..{np.nanmax(p):.0f} mm, {np.isnan(p).mean() * 100:.1f}% no data")
    pf = fill_sea(p)
    stations = fetch_stations()
    pc, used = station_correction(pf, meta, stations)
    OUT.mkdir(parents=True, exist_ok=True)
    with open(OUT / "stations.csv", "w", encoding="utf-8") as f:
        f.write("source,id,name,lat,lon,normal_mm,chelsa_mm,log_residual\n")
        for s in sorted(used, key=lambda s: s["resid"]):
            f.write(f"{s['src']},{s['id']},\"{s['name']}\",{s['lat']:.4f},{s['lon']:.4f},{s['mm']:.0f},"
                    f"{s['chelsa_mm']},{s['resid']:.3f}\n")
    res = np.array([s["resid"] for s in used])
    print(f"{len(used)} stations ({sum(s['src'] == 'NCEI' for s in used)} NCEI, {sum(s['src'] == 'ECCC' for s in used)} "
          f"ECCC); log residual station/CHELSA median {np.median(res):+.3f}, 10-90% {np.percentile(res, 10):+.3f}.."
          f"{np.percentile(res, 90):+.3f}")
    pmm = to_map_grid(pc, meta)
    raw_i = intensity(pmm)
    grid = ndimage.gaussian_filter(raw_i, SMOOTH_SIGMA, mode="nearest")
    grid = np.clip(grid, 0, 1)
    q = np.round(grid * 255).astype(np.uint8)
    RESOURCE.parent.mkdir(parents=True, exist_ok=True)
    with open(RESOURCE, "wb") as f:
        f.write(b"NORM")
        f.write(struct.pack("<iiifff", 1, N, N, HALF, P0, P1))
        f.write(q.tobytes(order="C"))
    gq = q.astype(np.float64) / 255.0
    rows = []
    for name, lat, lon in TOWNS + EXTRA:
        x, z = xz(lat, lon)
        rows.append({"name": name, "lat": lat, "lon": lon, "x": round(x), "z": round(z),
                     "precip_mm": round(sample(pmm, x, z)), "intensity": round(sample(gq, x, z), 3)})
    preview(gq, pmm, rows)
    OUT.mkdir(parents=True, exist_ok=True)
    (OUT / "towns.json").write_text(json.dumps(rows, indent=1), encoding="utf-8")
    np.save(OUT / "precip_mm_256.npy", pmm.astype(np.float32))
    print(f"wrote {RESOURCE} ({RESOURCE.stat().st_size} bytes)")
    print(f"{'town':18s} {'P mm':>6s} {'intensity':>9s}")
    for r in rows:
        print(f"{r['name']:18s} {r['precip_mm']:6d} {r['intensity']:9.2f}")


if __name__ == "__main__":
    main()
