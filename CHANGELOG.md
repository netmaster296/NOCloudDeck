# Changelog

Version history of NO Cloud Deck. The README describes the current version (1.2.0).

## 1.2.0

1.2.0 is the weather pass after our 2026-11-08 review of the Cascadia Falling test sessions. It has since run in game (see the README for what is and is not verified); the items below:

- **Ambient light lift** (`Light.AmbientLift`, default 1.5). Under the stock rule the sun is switched off below the
  cloud and the ground is lit by ambient only, which the review measured at about 20 % of its clear-noon brightness
  (terrain luma 0.078 against 0.39; the sky was six times brighter than the terrain). The plugin now multiplies
  `RenderSettings.ambientIntensity` (the value the stock lighting body writes once a second, `LevelInfo.cs:465`) by up
  to `AmbientLift`. The dawn mood stays: the sun is still off, the fog and sky are the same, and the lift is a
  multiple of the stock ambient, so it is smaller at dawn and dusk and zero at night. The weight is the shade (the
  stock "num3" rule that blocks the sun, or the in-cloud share, whichever is larger), zero above the deck top, scaled
  by the wetness, the fade-out and the local cloud cover (a patchy deck east of the crest is lifted less), and eased
  over 3 s. The sun's colour and direction are not touched. The log says when the lift engages and when it is
  released. The factor is a first guess from the review's 1.4-1.6 range: measure the terrain luma again before
  raising it. The mountain sites stay inside the cloud; this only makes the light there readable.
- **Fog mode** (the visibility maths). Up to 1.1.1 the density was `3.912 / visibility`, which is right for
  exponential fog but gives only about half the visibility if the scene uses squared-exponential fog (the review saw
  a mean density of 0.000593, 6.6 km as Exp or 3.3 km as Exp2, and photos that read 2-4 km). The scene's fog mode is
  not in the decompile. The plugin now reads `RenderSettings.fogMode`, logs it (`fog mode is ...`, and again in the
  "cloud deck on" line), and uses `3.912 / V` for Exp or `sqrt(3.912) / V = 1.978 / V` for Exp2 (the haze and rain
  densities are then combined in quadrature, as the Exp2 exponent needs). Linear fog is treated as Exp with a
  warning. So if the scene is Exp2, the visibilities in the config are now met as written and the fog is thinner than
  in 1.1.1 (12 km in light rain really is 12 km). The 1.1.1 fog is unchanged if the scene is Exp.
- **East-of-crest lee fade.** The stock cloud sheet at the mission base is no longer cut off at a single height. Its
  opacity is faded with the height difference between the local base and the mission base (starting 60 m over it, gone
  at 310 m, hidden at alpha 0.02 and shown again at 0.06), through a material property block on the plane's renderer,
  so Leavenworth (local base 1,249 m over the 900 m mission base, 349 m) no longer sits on the edge of a threshold. The
  opacity property of the stock plane's shader is not known (shader graph): the plugin lists the shader's properties
  in the log once and tries `_Opacity`, `_Alpha`, `_CloudOpacity`, `_OpacityMultiplier` and `_Fade` as floats, then
  `_BaseColor` and `_Color` as colours. If none exists, it falls back to the 1.1.x hard hide (hide over +310 m, show
  under +190 m), and says so. The log reports each change of the plane's state (`lee fade: stock cloud plane
  fading / hidden / restored`) with the local base, the mission base and the difference. The review's photos showed
  the stock sheet east of the crest only because the deck had been stood down by NOTelemetry (fixed in 1.1.1), so
  this has still not been seen working.
- **Rain prewarm** (`Rain.RainPrewarm`, default on). Rain used to build up over one streak life (up to 4 s) after it
  started, and never at all while the game was paused (the field-camera photo shoot ran at timeScale 0, so every photo
  had 0 streaks). Now, when fewer than half of the streaks the intensity calls for are alive, or the camera jumps more
  than 200 m in a frame (teleport, respawn, mission start), the box is filled in one go with streaks of mixed ages
  (at most 3,000, at most once a second). The log says so once. Particles only: nothing is synced.

## 1.1.1

1.1.1 fixes one bug. The plugin checks every 30 s whether another mod
has patched `CloudLayer.Update`, and stands the cloud deck down if so. It used to count every prefix, so NOTelemetry's
timing probes (void prefixes on that method, owner `com.jack.notelemetry`) made it stand down by mistake. Now only a
patch that can change what the method does counts: a prefix whose patch method returns `bool` (it can skip the
original) or a transpiler, from an owner other than this plugin. Void prefixes, postfixes and finalizers never count,
and the owner `com.jack.notelemetry` is always ignored. The log says once which owners it found and which it ignored
(`patches on CloudLayer.Update from other owners (counted: ...; ignored: ...)`), and again only if that changes. The
check for the weather mods `neutral.rain` and `WeatherController` (plugin ids) is unchanged.

## 1.1.0

1.1.0 makes the deck regional and lets it follow a changing sky:

- **Regional deck** (`RegionalDeck`, default on). A built-in map (airport weather statistics, see
  [The cloud map](README.md#the-cloud-map)) gives each place a cloud cover, a cloud base and a clear-air bonus. The cloud
  base is lower on the windward Olympic and Cascade slopes (500 m, hugging the terrain) and higher in the Olympic rain
  shadow and east of the Cascade crest. East of the crest the cloud thins into coherent patches (cover 0.65-0.85) and
  the rain falls to about half 30 km east of the crest, so Leavenworth gets about 0.15 of the rain map and Wenatchee and
  Ellensburg about none. The stock cloud sheet at the mission's base is faded out (1.2.0; hidden in 1.1.x) where the local base is more than 60 m
  above it (with 25 m hysteresis), the fog under the base is clearer in the lee, and the in-cloud fog follows the cover.
  The mission's cloud altitude is still the one server-set number (the Cascadia mission now uses 900 m); the map only
  shifts the deck around it, and it never touches the cloud occlusion that IR missiles use.
- **A clearing sky.** The plugin now runs down to the Moderate weather set (index 2). Between the Thunderstorm set
  (conditions 0.8) and conditions 0.4 a wetness factor falls from 1 to 0 and scales the rain, the rain sound, the haze
  and the puff density, and brings the deck top down towards the base. Below conditions 0.4 (lighter than Moderate)
  everything fades out over about 10 s instead of switching off at once, then the stock clouds take over. A step in the
  mission's weather (a `ModifyEnvironment` action) is eased over a few seconds, and a step in the cloud height is
  followed by the deck at 30 m/s. Lightning is left to the game.
- **Perf log.** Five new columns at the end: `cam_x_m`, `cam_z_m`, `local_cover`, `local_base_m`, `rain_local`.

## 1.0.2 and 1.0.1

1.0.1 added a performance log, an automatic A/B benchmark and frame-rate settings, after the first in-game test
found the clouds costly. 1.0.2 lets the benchmark start by itself once you are flying level and steady
(`BenchmarkAutoRuns`), runs it in whole rounds for about a set time (`BenchmarkMinutes`, default 8 min), measures each
phase for 15 s instead of 10 s, and ends with summary rows that pool every window of a phase. See
[Performance in the README](README.md#performance).

## 1.0.0

First build: cloud deck, rain, fog and lightning for the Cascadia Falling mission.
