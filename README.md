# NO Cloud Deck

A BepInEx 5 plugin for Nuclear Option (built against game version 0.34.6) that turns the game's Moderate, Overcast and
Thunderstorm weather into a Pacific Northwest rain day: a cloud deck with a base and a top, rain, haze and rarer
lightning. It was made for the *Cascadia Falling* mission (NOCustomMaps) and by default runs only there.

Version 1.2.0. GUID `com.jack.noclouddeck`. MIT licence (see `LICENSE`). The data credits for the rain and cloud maps
are at the end of this file.

## Requirements, install, where it runs

- Nuclear Option 0.34.6 and BepInEx 5. The Cascadia Falling map needs NOCustomMaps (tested with 1.4.2).
- Install with the Nuclear Option Mod Manager (NOMNOM), or copy `NOCloudDeck.dll` (from the GitHub release) into
  `BepInEx\plugins\`. The config file `BepInEx\config\com.jack.noclouddeck.cfg` is created the first time the game
  starts with the plugin.
- **Client-side, visual only.** It writes no networked value (weather, cloud height, wind, time of day). Each player who
  wants to see the deck and the rain installs it; it does nothing on a headless dedicated server. See
  [Multiplayer](#multiplayer) for the fairness note.
- **It is active only on Cascadia Falling.** The `General / Missions` setting (default `Cascadia Falling`) is a
  comma-separated list; the plugin is active only in a mission whose name contains one of the entries (case-insensitive).
  The reason is that the built-in rain and cloud maps cover that map's area. An empty `Missions` setting makes it act in
  every mission, but then the rain and cloud maps are applied to a place they were not made for. In any other mission
  the game looks as it does without the plugin.
- For Cascadia Falling, the mission's weather should be set to weatherIntensity 0.8 (Thunderstorm: lightning, and no
  extra stock fog) with the cloud altitude at the wanted base, for example 900 m. Weather that clears later in the
  mission (conditions falling to 0.4 and below) is followed as described in the CHANGELOG under 1.1.0.

## What has been verified in game

From Cascadia Falling runs on game 0.34.6 with NOCustomMaps 1.4.2: the saved BepInEx logs of six launch tests on
2026-11-08 that ran this version, and the recorder data (not the logs, which were not kept) of two playtests on
2026-11-09/10:

- It loaded and logged no errors or warnings in any of the saved logs.
- The log shows `NO Cloud Deck 1.2.0 loaded (rain map 256x256, 399.4 km square, ... cloud map 128x128 ...)`, the
  stock cloud plane shader's properties (`Shader Graphs/CloudLayer`), and `ambient mode is Trilight: lifting by scaling
  the sky, equator and ground ambient colours`.
- It ran through both playtests (14.5 and 29 minutes) at about 0.08 ms per frame (the plugin's own cost).
- Known: its deck outlasts the mission's noon weather break by about 60-70 s, and the stock game switches lighting
  abruptly when weather intensity crosses 0.6.
- `LeeFade` stays off by default. The stock cloud plane shader exposes no opacity property (the logged properties
  include `_fadeDepth` and `_fadeStrength` only), so the fade through a guessed property cannot work; the plugin hard-hides
  the stock plane where the local cloud base is more than 310 m over the mission base (and shows it again under 190 m).

What is **not** verified is listed in [Not yet verified in game](#not-yet-verified-in-game) at the end.

## Changes

See [CHANGELOG.md](CHANGELOG.md). The README below describes the current version, 1.2.0.

## AI assistance

This plugin and its documentation were written with AI assistance (Claude, by Anthropic) and tested as described under "What has been verified in game" and "Not yet verified in game".

## What it does

The stock game draws the clouds as one flat sheet at the mission's cloud altitude. When the camera is under that
sheet, it greys the whole scene and turns the sun off. Lightning comes every 2 to 19 seconds, and there is no rain.

With this plugin, in a matching mission whose weather is Moderate, Overcast or Thunderstorm (the wetter, the fuller):

- **Cloud deck.** The mission's cloud altitude becomes the cloud base, and the clouds fill the space from the base up
  to `CloudTop` (default 2,400 m above sea level):
  - The stock flat cloud plane stays at the base, and a copy of it (same mesh, material and scale) is drawn at the top.
    The far plane is hidden when it is behind the near one: the top plane while you are more than `PlaneCullMargin`
    under the base, the base plane while you are more than `PlaneCullMargin` over the top. The planes are blended, so
    the far one shows through gaps in the near one, and switching it off is a hard cut, not a fade.
  - The stock puff particles are spread through the deck: some form a ragged base, some bulging tops, some fill the
    middle. Under the base most new puffs go to the ragged base, above the top most go to the tops, and inside the
    deck the mix is even. That matters because the cloud planes are transparent and cannot hide puffs behind them.
    Sizes, lifetimes, placement and the spawn rate are the stock ones. The particle cap is the stock one times
    `CloudParticleBudget` (default 0.5). The spawn rate is per frame, as in stock (4 puffs a frame at Cloud Detail
    100 %, each living about 15 s), so a cap is reached only above a certain frame rate: the default cap of 1,500 above
    about 25 fps, the stock cap of 3,000 above about 50 fps.
  - The distant cloud ring stays inside the deck.
  - The fly-through wisps run anywhere inside the deck. They have stock strength within 100 m of the base or the top
    and are reduced deeper inside (`FlyThroughInterior`), where the in-cloud fog already hides the view. This limits
    the GPU fill-rate cost.
- **Under the base.** The air is clear apart from rain haze, under the stock overcast light. Visibility follows the
  local rain: about 12 km in light rain (`BelowBaseVisibility`), about 5 km in the heaviest rain, and more where it
  is dry.
- **Inside the deck.** Dense grey fog (`InCloudVisibility`, default 0.5 km), and the sun is hidden.
- **Above the top.** Clear air and sunshine.
- **Rain.** Streaks fall around the camera at 8-9 m/s and drift with the mission wind, with a looping rain sound
  that is muffled in the cockpit. Both are shown only under the cloud base. At speed, the streaks are spread along
  the path the aircraft is about to fly, so the rain stays about as dense at 250 m/s as it is when hovering. How hard
  it rains depends on where you are, from a built-in rain map of annual precipitation:

  | Area | Rain |
  |---|---|
  | Pacific coast, Olympics, Cascade crest | heavy |
  | Seattle, Tacoma, Everett, Bellingham | moderate |
  | Sequim, Port Townsend, Whidbey | light (the Olympic rain shadow) |
  | Ellensburg, Yakima | none |

  The streak texture and the sound are generated in code. The sound plays through the game's own audio mixer, so
  your volume settings apply. By default (`RainMaterial = Auto`) the streaks use the cheapest material whose shader
  variant is known to be in the game build: a copy of a game material on the URP Particles/Unlit shader that is
  already transparent with no optional features, else such a copy on any URP particle shader (in practice the lit one
  1.0.0 used, `WaterShockwave`), else Sprites/Default, else a new Particles/Unlit material, else a copy of any URP
  particle material. The log names the one chosen and its keywords (`rain material ...`). If the rain draws pink,
  opaque or not at all, set `RainMaterial` to another choice; it switches at once.
- **Lightning.** Strikes are occasional, by default one every 90-420 s (about one every 4 minutes on average). Each
  strike lands ahead of you, within 70 degrees of your heading. Its distance is 30-90 % of the local visibility under
  the base, between 1.5 and 8 km: 2.3-6.8 km around Seattle, up to 8 km in the drier areas. So when you look ahead, most
  strikes can be seen through the rain, and all of them are close enough to hear: the game plays thunder only within
  8 km. The thunder is the game's own, delayed by distance at 340 m/s (stock behaviour). Each bolt runs from 50 m above
  the cloud base down to the ground. Strikes favour the wetter areas, and they need the ground at least 150 m below the
  base. Over mountains that reach into the deck, a strike that finds no place is skipped and retried after 5-15 s.

If the mission is not matched, or the weather is lighter than Moderate, the game looks as it does without the
plugin (when the weather becomes that light during a match, the deck fades out over about 10 s). The same is true when one of the community weather mods `neutral.rain` (RainMod) or `WeatherController` is
loaded: the plugin logs a warning and stands down so the two do not fight. It also stands down for any other mod whose patch on `CloudLayer.Update` can change it (a prefix returning bool, or a transpiler); NOTelemetry's timing probes do not count.

When a mission starts, the log (BepInEx/LogOutput.log) gets one line saying whether the plugin is on and with which
settings, for example:

```
[Info   :NO Cloud Deck] mission "Cascadia Falling": cloud deck on (Thunderstorm, base 900 m, top 2400 m MSL; rain on, ...)
```

## Settings

| Section | Setting | Default | Meaning |
|---|---|---|---|
| General | Enabled | true | Master switch. |
| General | Missions | Cascadia Falling | Active only in missions whose name contains one of these (comma-separated). Empty means every mission. |
| Clouds | CloudTop | 2400 | Top of the deck while the weather is fully wet, metres above sea level. The base is the mission's cloud altitude. The top is always at least 300 m above the base, and comes down onto it as the weather clears. |
| Clouds | InCloudVisibility | 0.5 | Visibility inside the deck, km (0.3-1). |
| Clouds | FlyThroughInterior | 0.3 | Strength of the close cloud wisps deep inside the deck, relative to stock (0-1). Lower costs less GPU. (1.0.0: 0.5.) |
| Clouds | CloudParticleBudget | 0.5 | Cloud puffs as a fraction of the stock cap, 3000 x sqrt(Cloud Detail) (0.1-1). The spawn rate stays stock. 1 is the stock count. Lower is faster and the deck is thinner between the planes, as with the game's Cloud Detail slider. |
| Clouds | RegionalDeck | true | Make the deck regional from the built-in cloud map: local cloud base (lower on the windward slopes, higher in the rain shadow and east of the crest), patchy cover east of the crest, the stock sheet hard-hidden where the local base is more than 310 m above the mission base (with `LeeFade` on it would be faded between 60 and 310 m), clearer air in the lee, and the rain map halved 30 km east of the crest. Off = the same deck everywhere, as 1.0.2. |
| Clouds | PlaneCulling | true | Hide the far cloud plane when it is behind the near one (see What it does). |
| Clouds | PlaneCullMargin | 100 | How far beyond the base or top, in metres, before the far plane is hidden (0-1000). The plane is hidden at the margin + 25 m and shown again at the margin - 25 m, so it does not flicker. |
| Rain | BelowBaseVisibility | 12 | Visibility under the base in light rain (intensity 0.25), km (6-20). It scales with intensity^-0.6, between 0.3x this value and 40 km. |
| Rain | RainEnabled | true | Rain streaks and rain sound. The rain haze stays even when this is off. |
| Rain | RainIntensityScale | 1.0 | Multiplies the local rain intensity (streaks, sound and haze), 0.5-2. |
| Rain | UseRainMap | true | Regional rain from the map. When off, the rain is moderate (0.5) everywhere. |
| Rain | MaxRainParticles | 2000 | The most streaks alive at once, reached at intensity 1. (1.0.0: 3000.) |
| Rain | RainMaterial | Auto | Material for the rain streaks: `Auto`, `UnlitClone`, `LitClone` (the 1.0.0 choice), `Unlit` or `Sprite`. See What it does. A choice that is not available falls back to Auto. |
| Rain | RainSoundVolume | 0.5 | Rain volume at full intensity. |
| Rain | RainPrewarm | true | Fill the empty rain box at once (rain starting, a teleport or respawn, a paused game) so rain is visible immediately. Off = 1.1.x behaviour. |
| Light | AmbientLift | 1.5 | Multiple of the stock ambient intensity under and in the cloud deck, where the stock rule switches the sun off (1-1.8; 1 = stock). Eases in over 3 s, scales with shade, wetness and cover, zero above the deck and at night. See the CHANGELOG, 1.2.0. Dry decks get half the lift. If the scene ambient mode is not Skybox the ambient colours are scaled instead (the log says which; in our tests it said Trilight). |
| Clouds | LeeFade | false | Experimental: fade the stock cloud plane through a guessed shader opacity property in the lee of the regional deck; off = hard hide. Leave it off: the stock plane's shader has no opacity property (see above). |
| Lightning | LightningMinInterval | 90 | Shortest time between strikes, s. |
| Lightning | LightningMaxInterval | 420 | Longest time between strikes, s. |
| Performance | PerfLog | true | Write the performance log (see Performance). |
| Performance | BenchmarkInMultiplayer | false | Allow the benchmark in a multiplayer match. Leave it off except on a private server (see Benchmark). |
| Performance | BenchmarkAutoRuns | 0 | Number of game sessions in which the benchmark starts by itself, once you have flown level and steady at least 100 m above the ground for 20 s (0-100). Each run that finishes counts it down by one and saves the config; an aborted run does not. 0 is off. |
| Performance | BenchmarkMinutes | 8 | About how long a benchmark runs, minutes (2-20), whether it started by itself or from the trigger file. It runs whole rounds of about 4 min and ends at the round boundary closest to this time, so 8 gives 2 rounds. At least one round always runs. |
| Internal | ConfigVersion | (set by the plugin) | The version whose defaults the file has. Do not edit. |

The visibility ranges and `AmbientLift` are kept narrow on purpose (see Multiplayer). `AmbientLift` is a per-client look,
like the fog: a player on 1.8 sees the ground a little more clearly under cloud than one on 1.0.

**Updating from 1.0.0.** BepInEx keeps the values already in the config file, so a file written by 1.0.0 would keep
the old defaults. On its first start, 1.0.1 or later moves `FlyThroughInterior` from 0.5 to 0.3 and `MaxRainParticles`
from 3000 to 2000, but only where the file still has the old default. A value you changed is left alone. It then
writes its own version into `ConfigVersion` (for example `1.1.0`) and never does this again. The log says what it
changed. Updating from 1.0.1 changes no values; the two new benchmark settings are added with their defaults.

Heights are above sea level, not above the ground. The mission sets one base, and the two cloud planes are flat
(the stock one at the mission base, the copy at the top); with `RegionalDeck` the puffs, fog and rain follow the
local base from the cloud map, but the planes themselves cannot bend. Hills and mountains that rise above the local
base stand in the cloud. With the 900 m base of the Cascadia mission, Ellensburg's field (about 540 m) is 360 m under
the mission base, and the map lifts the local base there to the 1,800 m cap, so the stock sheet is hidden and the
sky is only a thin patchy deck of puffs (cover 0.67). Where the planes and the local base disagree by less than
60 m the stock sheet stays at full strength (it is hidden above 310 m; with `LeeFade` on it would fade between 60 and 310 m), and a camera can see it at the mission base while the puffs hang higher or lower.

## Multiplayer

The plugin is purely visual and local. It does not write any networked value (weather, cloud height, wind, time of
day). Three things that feed the IR missile seeker on the host are left as they are: the cloud occlusion test, the
sun's colour and transform, and the cloud layer's height. It does nothing on a headless dedicated server.

Each player needs the plugin to see the deck, the rain and the rarer lightning. Players without it see the stock
thunderstorm at the same cloud altitude. Lightning strikes are random on each machine, as in the stock game.

**Fairness in PvP.** The fog is drawn on each player's own machine, and the plugin cannot check what other players
run. A player without the plugin, or with `Enabled = false`, sees far more than one with it: in stock at this weather,
visibility is about 50 km. They can see aircraft hiding in the deck, which modded players see only within about
0.5 km, and ground targets 10-30 km away under the base, which modded players see only within 5-12 km. For a fair
match, **every player should run NO Cloud Deck with the default settings**. Say so in the mission description. The
visibility settings accept only a narrow range, so the config cannot be turned into a large advantage. The same goes for `AmbientLift` (1-1.8): it brightens the ground under cloud on the player's own screen only.

The TV mode of the targeting camera sees the same rain haze as the main view: a TV sensor sees no further through
rain than the eye. IR mode switches fog off for that camera (stock, `TargetCam.cs`), so IR targeting sees through the
haze.

**IR missiles and the sun.** The host's IR seeker still uses the stock single cloud layer. It counts a target or the
sun as hidden by cloud only up to 200 m above the base. Higher up inside the deck, an IR missile can still be drawn
off by the sun even though the plugin hides it, and IR shots through the upper deck are not weakened by the cloud.
This cannot be changed safely from a visual mod: those values are read on the host for every player.

`PlaneCulling` changes only which plane is drawn when it is behind the other one; the plane on your side of the deck
is always drawn. `CloudParticleBudget` does change what you see: it thins the puffs, which together with the planes
hide an aircraft in the deck from a player under the base, where the haze is clear. This is the same trade the game's
own Cloud Detail slider makes, and the plugin cannot check either one. The fairness advice applies to it too: every
player on the default settings.

The benchmark (see Performance) switches the fog and clouds off for a while, so it does not start in a multiplayer
match unless `BenchmarkInMultiplayer` is on, which is meant for a private server.

## Performance

Much of the cost is the stock Thunderstorm weather itself. Its cloud sampler is 1.0 everywhere, so the stock cloud
system runs at its cap of about 3000 x sqrt(Cloud Detail) puffs of 429-1001 m. A Scattered sky at weather 0.35 runs
about 840. The deck adds a second cloud plane, fly-through wisps over 1.8 km of height instead of about 230 m, and the
rain. The settings `CloudParticleBudget`, `FlyThroughInterior`, `PlaneCulling` and `MaxRainParticles` trade some of the
look for frame rate. The game's own Cloud Detail slider (graphics settings) also scales the puff cap.

### Performance log

While a mission that matches `Missions` is loaded, the plugin writes one CSV row per second to
`BepInEx/NOCloudDeck-perf-<yyyyMMdd-HHmmss>.csv`. It does this whether the deck is on or not, so stock and modded
runs can be compared. Each game session gets a new file. Turn it off with `PerfLog = false`.

The file starts with a comment block, lines starting with `#`, that records:

- the game and Unity versions, the OS, GPU name, vendor, API and VRAM, CPU, core count and RAM;
- the render size, the display resolution and refresh rate, the fullscreen mode;
- the Unity quality level, vSync count and target frame rate;
- the game's graphics settings (Cloud Detail, Vsync, FPS limit, shadows, anti-aliasing, LOD bias, textures,
  anisotropic filtering, lights) and the URP asset's render scale, MSAA and HDR;
- every setting of this plugin;
- which engine counters this game build can read.

About 10 s into the first mission, an `event` row and a log line repeat the counter check with in-mission data.

To read the file in pandas: `pd.read_csv(path, comment='#')`. In a spreadsheet, the comment lines appear as rows at
the top.

Rows have a `row_type`:

- `sample`: one second.
- `bench`: one benchmark phase's measured window (see below).
- `summary`: written at the end of a benchmark run; one phase pooled over all its windows, in one camera zone or in
  all of them (see [Summary](#summary)).
- `event`: a note, such as mission start (with the mission, weather, cloud base and Cloud Detail), mission end, or
  benchmark start and end.

| Column | Meaning |
|---|---|
| `row_type` | `sample`, `bench`, `summary` or `event`. |
| `t_s` | Real time since the game started, s. |
| `frames` | Frames in the second (or in the bench window, or in all the windows a summary row pools). |
| `fps_avg` | frames / sum of the frame times. |
| `ft_avg_ms`, `ft_median_ms`, `ft_p95_ms`, `ft_max_ms` | Frame time: mean, median, 95th percentile and maximum, ms. Taken from every frame's unscaled delta time. |
| `ftm_cpu_ms`, `ftm_cpu_main_ms`, `ftm_cpu_render_ms`, `ftm_gpu_ms` | CPU, main thread, render thread and GPU frame times from Unity's FrameTimingManager. Empty when the game build does not enable it. |
| `pr_main_thread_ms`, `pr_cpu_main_ms`, `pr_cpu_render_ms`, `pr_gpu_ms` | The same from Unity's ProfilerRecorder ("Main Thread", "CPU Main Thread Frame Time", "CPU Render Thread Frame Time", "GPU Frame Time"). Empty where the release build does not report them. |
| `batches`, `setpass_calls`, `draw_calls`, `triangles`, `vertices` | Per-frame render counters from ProfilerRecorder, averaged over the second. Empty where not reported. |
| `cam_alt_m` | Camera altitude above sea level, m. |
| `zone` | `below` the base, `in` the deck, or `above` the top. When the deck is not running, the top is the base. In `summary` rows also `all` (every zone) or `unknown` (windows with no camera). |
| `base_m`, `top_m` | Cloud base and top, m above sea level. |
| `weather_set` | Weather set index: 0 Clear, 1 Scattered, 2 Moderate, 3 Overcast, 4 Thunderstorm. |
| `rain_intensity` | Local rain intensity at the camera (rain map x lee scaling x RainIntensityScale x the weather's wetness). |
| `cam_x_m`, `cam_z_m` | Camera position in metres from the map centre (east, north), the frame of the rain and cloud maps. |
| `local_cover`, `local_base_m` | The cloud map at the camera: cover 0..1 (1 when `RegionalDeck` is off), and the local cloud base m above sea level (the mission base when `RegionalDeck` is off or the mod is not active). |
| `rain_local` | The rain map times the lee scaling at the camera, before the weather's wetness and `RainIntensityScale`. |
| `cloud_puffs`, `cloud_puff_cap` | Live puffs of the stock near-cloud system, and its current cap. |
| `distant_puffs` | Live puffs of the distant ring. |
| `flythrough_particles` | Live fly-through wisps. |
| `rain_particles` | Live rain streaks. |
| `base_plane_on`, `top_plane_on` | 1 if the base (stock) plane or the deck's top plane is drawn. |
| `fog_density` | RenderSettings.fogDensity, or 0 when fog is off. |
| `mod_active`, `deck_running` | 1 if the plugin is active, and 1 if its cloud replacement is running. |
| `cfg_enabled`, `cfg_rain`, `cfg_rain_map`, `cfg_plane_cull` | The Enabled, RainEnabled, UseRainMap and PlaneCulling settings (1/0). |
| `puff_budget` | The puff budget in use, including a benchmark override. Empty when the deck is not running. |
| `flythrough_interior`, `max_rain` | The FlyThroughInterior and MaxRainParticles settings. |
| `bench_phase` | The benchmark phase, or `<phase> (settle)` while a phase settles, or empty. |
| `bench_round` | The benchmark round (1, 2, ...) in `sample` and `bench` rows while a run goes, else empty. |
| `gc_gen0` | Managed garbage collections since start. In a `bench` row: collections during the window. |
| `managed_mb` | Managed heap in use, MB. |
| `note` | Text of an `event` row; details of a `bench` row (round, interruptions) or a `summary` row (window count, change against all-on). |

The logger keeps each second's frame times in a preallocated buffer and formats rows into a preallocated character
buffer, so it allocates nothing per frame. It flushes the file every 5 s, at mission end and after a benchmark. While a
benchmark runs, the file is written at the start of each phase instead, so a disk write never lands in a measured
window. Turning `PerfLog` off mid-session ends the file; turning it on again starts a new one.

### Benchmark

The benchmark runs a fixed set of A/B phases, in whole rounds, for about `BenchmarkMinutes` (default 8 min). It starts
in one of two ways.

**By itself (`BenchmarkAutoRuns`).** For one automatic run, set this in `BepInEx\config\com.jack.noclouddeck.cfg`
(with the game closed: the plugin reads the file when the game starts):

```
[Performance]
BenchmarkAutoRuns = 1
```

Then start the game, fly Cascadia Falling, take off, and settle into level flight (autopilot works well). When the
mission loads, the log says `benchmark starts automatically once you have flown level and steady ...`, and adds why
it cannot start if, for example, the deck is not on. The countdown runs while all of these hold:

- you are in your own aircraft (`GameManager.GetLocalAircraft`), it is not disabled and you have not ejected;
- the camera follows that aircraft (`CameraStateManager.followingUnit`), in any view;
- the map is closed and the game is not paused;
- you are at least 100 m above the ground, ships or sea (the game's own `radarAlt`) and not landed;
- your height changed by no more than 10 m/s over the last second, so a take-off or a climb does not count.

When they first hold, the log says `benchmark starts automatically in about 20 s ...`. If any of them stops holding
before the 20 s are up, the countdown stops (`automatic benchmark countdown stopped: <why>`) and starts over the next
time they hold. Spawning only puts you in the aircraft and points the camera at it, on the runway or the deck too, so
that alone does not start the countdown.

There is no start on a timer alone: a run on the spawn or map screen would measure the menu, not the clouds, and
opening or closing the map during a run discards the window anyway. So if you never fly level and steady, no automatic
run starts. While it waits, the log says why about 60 s after the mission loads and every 2 minutes after that
(`automatic benchmark still waiting: <why> ...`).

It starts at most once per game session. When the run finishes, `BenchmarkAutoRuns` goes down by one and the config
file is saved, so with 1 the next session plays normally. A run that is aborted does not count: the log says
`the automatic benchmark did not finish, so it does not count ...`, and it starts by itself again in the next session
(not again in this one; the trigger file still works). A value of N gives one automatic run in each of the next N
sessions. In a multiplayer match it does not start (unless `BenchmarkInMultiplayer` is on), and the log says so. If
the deck is off when the countdown ends, it is checked again for 10 s and then given up for that mission. If you start
a run with the trigger file before the automatic one begins, the automatic start is skipped in that mission.

Change the config only with the game closed. Changing any setting of this plugin while a run goes (for example with a
configuration manager) aborts it.

**From the trigger file.** During a matching mission, create an empty file named `noclouddeck.bench` in
`BepInEx/config/`, for example from PowerShell:

```
New-Item "<game folder>\BepInEx\config\noclouddeck.bench"
```

The plugin checks for the file about once a second, once the mission has been loaded for 10 s (so the clouds have
filled in), deletes it, and starts. A run started this way lasts `BenchmarkMinutes` too. There is no hotkey: most of
the game's controls go through Rewired, whose key bindings are player data and can be changed, so the game code cannot
show that any key is free.

The file is deleted without starting a run, and the log says why, when:

- the plugin loads and finds one left from an earlier session;
- this is a multiplayer match and `BenchmarkInMultiplayer` is off;
- the cloud deck is not on in this mission (every phase would measure the same stock scene);
- the performance log failed earlier in the session.

The automatic start is refused for the same last three reasons. A file created while a run is going is deleted when
the run ends, so it does not start a second run. Create it again for another run.

**During the run.** Keep the view as steady as you can: same place, heading, height and camera. Autopilot level flight,
or the same external view, works well. Keep the game window in front: a phase does not measure while the game window
is in the background, the game is paused, the map is open (or closed, if it was open at the start), or the camera
follows another unit than at the start. If that happens during a measurement, the phase settles and measures again
(noted in its row and log line); after more than 2 minutes of it, or 5 interruptions of one phase, the run stops. The
run stops at once if you leave the aircraft it started in (the pilot dies, the aircraft is disabled or you eject), change
aircraft, or take control of one when the run started without one. Each phase is a client-visual change only. The config file is not changed, apart from the count-down of
`BenchmarkAutoRuns` after a finished automatic run.

What you see: the clouds, rain and fog switch every 17 to 31 s or so. Most phases last about 17 s (2 s to settle,
15 s measured). `mod-off`, and `all-on` from the second round on, settle for 16 s first, so they last about 31 s. The
two phases that raise the puff budget can settle up to 12 s longer while the deck fills. In turn you will see: the deck
as configured; the top cloud plane gone; the cloud puffs gone; no close wisps in cloud; the distant cloud ring gone;
no rain; the stock fog instead of the deck's haze; a thinner, a much thinner and a full (stock-count) set of puffs;
the stock thunderstorm with the mod off (clear 50 km visibility under the clouds); and the stock thunderstorm with its
puffs and distant ring gone. Then it starts again from the top.

| # | Phase | What changes |
|---|---|---|
| 1 | `all-on` | Nothing: the plugin as configured. The other phases are compared with this one. |
| 2 | `top-plane-hidden` | The deck's top plane is not drawn. |
| 3 | `puffs-hidden` | The cloud puffs are not drawn (their renderer is off; they are still simulated). |
| 4 | `flythrough-off` | No fly-through wisps. |
| 5 | `distant-hidden` | The distant cloud ring is not drawn. |
| 6 | `rain-off` | No rain streaks and no rain sound. |
| 7 | `fog-stock` | The plugin's fog, sky and sun override is off; stock fog. |
| 8 | `budget-50` | Puff budget 0.5 of the stock cap. |
| 9 | `budget-25` | Puff budget 0.25. |
| 10 | `budget-100` | Puff budget 1.0, the stock count (the 1.0.0 setting). |
| 11 | `mod-off` | The whole plugin off: the stock thunderstorm. |
| 12 | `stock-no-puffs-no-distant` | The stock thunderstorm with its puffs and distant ring not drawn. |

One round takes about 4 min: 12 phases, 218 s with the shortest settles, up to about 256 s when the fill phases take
their full 12 s and all-on waits for the stock puffs to turn over (rounds 2 on). The phases repeat in this order, in
whole rounds only, so every phase gets the same number of windows. At the end of each round the run stops unless the
end of the next round would be closer to `BenchmarkMinutes` (the next round is estimated from the one just run). At
least one round always runs, so `BenchmarkMinutes = 2` (or 5) gives one round of about 4 min; the default 8 gives two
rounds, about 8 min; 10 gives two or three, depending on how long the rounds take.

Each phase settles for 2 s, then measures 15 s. Some phases settle longer:

- A phase that raises the puff budget settles until the puffs are at 95 % of the new cap. During that settle,
  16 extra puffs are emitted per frame, for at most 12 s more.
- `mod-off` settles for 16 s, so the puffs the deck placed die out (they live 15 s). From the second round on,
  `all-on` does the same, because it follows the stock phases.
- When the budget drops, the puffs over the new cap are removed at once.

After the run, every override is cleared, every renderer the benchmark switched off is switched back to its earlier
state, and the plugin re-checks the mission straight away. The same happens at once, with the run stopped
(`benchmark aborted (...)`), when the mission ends or the level changes, a setting of this plugin changes, the weather
or the cloud base changes, or the deck stands down by itself (another mod, an error). Later phases would then measure
something else.

For each phase, the CSV gets a `bench` row (with its round in `bench_round`) and the BepInEx log gets one line of this
form (a `-` marks a value the build does not report):

```
[Info   :NO Cloud Deck] bench round <n> <phase>: <fps> fps (<change> % vs all-on), frame median <ms> ms, p95 <ms> ms, max <ms> ms; puffs <n> (cap <n>), distant <n>, fly-through <n>, rain <n>; <zone>, <altitude> m; batches <n>, setpass <n>, gpu <ms> ms
```

The `bench` row holds the frame statistics of every frame in the measured window and the means of the per-second
columns over the whole seconds inside it (a second that began in the settle time is left out). Its change against
all-on is against the all-on window of the same round.

#### Summary

When the run ends (also after an abort, then covering the windows measured so far), the plugin pools every window of
each phase and writes, for each phase, one line over every camera zone (`all`) and one line per zone it was measured
in (`below`, `in`, `above`, or `unknown` when there was no camera). Each is a `summary` row in the CSV and a log line:

```
[Info   :NO Cloud Deck] bench summary <phase> [<zone>]: <n> windows, <fps> fps (<change> % vs all-on), frame median <ms> ms, p95 <ms> ms, max <ms> ms
```

- windows: how many measured windows went into it (the note column in the CSV).
- fps: all frames of those windows divided by their total time (`fps_avg`; `ft_avg_ms` and `ft_max_ms` are exact
  too).
- frame median and p95: taken from every frame delta of those windows, not by averaging the per-window values. The
  deltas are counted in 0.1 ms bins up to 250 ms (one more bin for anything longer), and the value is the centre of
  the bin that holds that rank, so it is within 0.05 ms of the exact value, and never above the longest frame.
- change: against `all-on` in the same zone (the `all` line against `all-on` over every zone). If all-on was never
  measured in that zone, the line says so. `all-on` itself says `reference`.

A window counts in the zone where the camera spent most of its whole seconds. Compare phases on the per-zone lines:
below the base and inside the deck cost very different amounts, and the `all` lines mix whatever zones each phase
happened to be measured in. If you drifted between zones, a phase may have only one window in a zone, and its `all`
line can mislead. In the CSV, `summary` rows leave the per-second columns
(counters, puffs, altitude) empty.

The pooled data are a fixed set of preallocated counters (12 phases x 4 zones x 2,501 bins, about 0.5 MB), filled
at the end of each window, so a long run neither allocates nor grows.

## How it hooks the game

These are Harmony patches. Each patch body catches its own errors, logs the first one, and falls back to the stock
behaviour for that part:

| Patch | Type | What it does |
|---|---|---|
| `CloudLayer.Update` | prefix | Replaced while active. The stock method runs otherwise. The replacement also sets the puff cap from `CloudParticleBudget` and switches the far plane's renderer off (`PlaneCulling`). Both are put back on stand-down. If another mod has a patch on it that can change its behaviour (a prefix returning bool, or a transpiler; owner `com.jack.notelemetry` and void prefixes are ignored; checked at mission start and every 30 s), the clouds are left to that mod, and the fog then follows the stock single cloud sheet: rain haze below it, no in-cloud band. |
| `CloudLayer.UpdateWeatherSets` | postfix | Keeps the distant cloud ring inside the deck. |
| `LevelInfo.UpdateTimeOfDayLighting` | postfix | Fog and sun. NOCustomMaps 1.4.2 has a transpiler on the same method, and both work together. Nothing is written while the camera is under the sea or a lake, so the stock underwater fog stays. The sun switch has hysteresis, so it does not flicker at a threshold. |
| `Lightning.Update` and `Lightning.OnEnable` | postfixes | Set the strike interval. |
| `Lightning.GenerateStrike` | prefix | Replaced while active. |

A part that failed in one mission is tried again in the next one, and its error is logged only once.

## Building

```
dotnet build -c Release
```

This needs the .NET SDK. The csproj has a default game folder (a Steam library path on the author's machine). Override
it with `-p:GameDir=...` (`dotnet build -c Release -p:GameDir="..."`) or a `GameDir` environment variable. The output is
`bin/NOCloudDeck.dll`, and only that file needs to be installed. The build gives one warning, MSB3277, a
System.IO.Compression version conflict that comes from the game's Mirage.dll. Our other Nuclear Option plugins give the
same warning.

### Map generator scripts

`Resources/rainmap.bin` and `Resources/cloudmap.bin` are embedded in the DLL. They were written by two Python scripts
from the Cascadia map project, `tools/rainmap.py` and `tools/cloudmap.py`, which are included in this repository as a
record of how the maps were made. **They are not runnable on their own.** They contain no credentials or absolute local
paths, but they expect the layout of the map project: they read `design/mission.json` (the map frame), and
`build/height.f32` (the heightmap) and the downloaded climate data under `source/climate`, and they write into
`plugin/NOCloudDeck/Resources/` and `build/`. Those inputs are not part of this repository, and the comments in
`cloudmap.py` refer to a working study of station data that is not published. Besides numpy and scipy, `rainmap.py` needs
Pillow, requests and `tifffile` (BSD-3-Clause; `pip install tifffile`) for the climate GeoTIFF; `cloudmap.py` needs
Pillow only for `--preview`. The scripts are covered by this repository's MIT licence.

### The cloud map

`tools/cloudmap.py` (see Map generator scripts above) writes `Resources/cloudmap.bin`, which is embedded in the DLL: a
128 x 128 grid (3.12 km cells) over the same frame as the rain map, with four planes: cover, cloud base (for a 900 m
mission base; the plugin shifts it with the actual base, scaled by the wetness, and keeps it at least 600 m under the
top), clear-air bonus and a lee ramp (0 at the Cascade crest, 1 thirty km east of it). The numbers come from the
2015-2025 hourly airport reports (Iowa Environmental Mesonet ASOS archive) for the hours when Sea-Tac reported steady
rain with a south-westerly wind, interpolated between 16 stations and a set of judgement values for the places with
no airport (the passes, the lee slopes, the ocean). Nothing is measured at the passes. The terrain-hugging base is the
upslope of the smoothed heightmap in the onshore flow. The rain map is annual precipitation, which overstates the dry
east side on rain days, hence the lee ramp: rain x lerp(1, 0.5, ramp), Leavenworth about 0.14, Wenatchee and Ellensburg
about 0. Typical map values (cover / base m MSL): Forks 0.99 / 595, Seattle 0.98 / 941, Everett 0.97 / 789,
Sequim 0.93 / 1,064, Victoria 0.93 / 1,103, Stevens Pass 1.0 / 522, Leavenworth 0.80 / 1,249,
Wenatchee 0.68 / 1,444, Ellensburg 0.67 / 1,848. `python tools/cloudmap.py --check` prints them.

The puff gate is a coherent value noise (4.5 km and 1.8 km cells, drifting with the cloud pattern) compared with the
cover, so a cover of 0.65 leaves about 65 % of the sky in connected patches. The noise is deterministic and uses no
`Random`.

### The rain map

The map is a 256 x 256 grid (1.56 km cells) over Cascadia's 399.36 km map, centred on 48.0 N, 122.9 W. It is built
in four steps:

1. Start from CHELSA V2.1 annual precipitation (1981-2010, 30 arc-seconds).
2. Correct it with station normals. CHELSA is too wet in the Olympic rain shadow: at the Sequim 2 E station it gives
   708 mm against a 428 mm normal. The log ratio station/CHELSA at 189 stations is spread by inverse-distance
   weighting, and the correction fades out 20-40 km from the nearest station.
3. Map precipitation P to intensity with `clamp(ln(P/300 mm) / ln(2500 mm/300 mm), 0, 1)`.
4. Smooth lightly.

## Data credits

The rain map embedded in the DLL is derived from:

- **CHELSA V2.1** bio12, 1981-2010. Karger, D. N., Conrad, O., Boehner, J., Kawohl, T., Kreft, H., Soria-Auza,
  R. W., Zimmermann, N. E., Linder, H. P., Kessler, M. (2021): Climatologies at high resolution for the earth's land
  surface areas. Version 2.1. EnviDat. https://doi.org/10.16904/envidat.228. The paper: Karger et al. (2017),
  Scientific Data 4, 170122, https://doi.org/10.1038/sdata.2017.122. The EnviDat record lists the licence as CC0 1.0.
- **NOAA NCEI U.S. Climate Normals 1991-2020**, annual normals by station (public domain, U.S. Government work).
- **Environment and Climate Change Canada, Canadian Climate Normals 1981-2010**, total precipitation. Contains
  information licensed under the Open Government Licence - Canada
  (https://open.canada.ca/en/open-government-licence-canada).

The cloud map embedded in the DLL (cover, cloud base, clear-air bonus) is derived from:

- **Iowa Environmental Mesonet (IEM), Iowa State University**, ASOS/AWOS archive: hourly airport reports, 2015-2025, for
  16 stations in the Pacific Northwest. IEM is the data provider. The underlying observations come
  from the ASOS/AWOS stations of the U.S. National Weather Service and FAA and, for the Canadian stations, from
  Environment and Climate Change Canada, as distributed through IEM. This is provenance only; see
  https://mesonet.agron.iastate.edu/ for terms.

The plugin uses no code or assets from other weather mods.

## Not yet verified in game

1.2.0 has run in game without errors (see "What has been verified in game" near the top). The checks below were not
done. Each item is about a visual result or a
setting we did not exercise, not about whether the plugin loads. Not yet confirmed for 1.2.0:

- the ambient lift: the log shows the scene's ambient mode is Trilight, so the plugin scales the sky, equator and ground
  ambient colours (not `ambientIntensity`). What terrain brightness this gives under the deck at 07:30 and later in the
  morning has not been measured (the review's target was a terrain luma of 0.18-0.25 with the sky at most 3 times the
  terrain; 1.5 is a first guess and may be too little or, with a 1.8 cap, still not enough at dawn);
- the fog mode the scene really has (read the `fog mode is ...` line in the log), and one photo of a known target 5 km
  away from 300 m altitude to confirm the visibility;
- the lee fade: the shader's properties were logged, and the stock plane's shader has no opacity property (only
  `_fadeDepth` and `_fadeStrength` among the logged ones), so `LeeFade` is off by default and the plane is hard-hidden.
  Not checked: how the hard hide looks east of the crest (Ellensburg, Yakima, Leavenworth), and whether the puffs carry
  the sky there;
- the rain prewarm: that rain is visible at once after a respawn and in a paused shoot, and that the one-off burst
  (up to 3,000 streaks at mixed ages) does not show as a pop;

and for 1.1.0:

- how the regional deck looks: the full-sheet top plane is hidden while the camera is below a patchy local base
  (cover under 0.85, with hysteresis), and the stock sun-off and grey-sky rule is keyed to the local base and scaled
  by the cover there; both are untested in game. The stock plane (its transform must equal the raw cloud height) steps
  40-50 m at each of the mission's weather steps while the puffs, fog and rain follow the eased base at 30 m/s. The
  top plane goes at half the stand-down fade. (1.2.0 fades it out instead of cutting it, see above) and whether the 900 m plane
  against the 500 m windward base looks right on the Olympic and Cascade slopes;
- that the distant ring's emission rate can be scaled (it is skipped if the rate is not a constant);
- the noon clearing: that the mod stays active down to the Moderate set, that the fade-out below conditions 0.4 is
  smooth, and that `ModifyEnvironment` steps in conditions or cloud height are followed without pops;
- that the stock set 2 coverage is above 0.1, which the deck-weather test needs;

and for 1.1.1:

- that with NOTelemetry loaded the deck stays on: the log should show the `patches on CloudLayer.Update from other
  owners` line with `com.jack.notelemetry` under ignored and `counted: none`, and no "another mod patches
  CloudLayer.Update" warning. Not run in game: the rule reads `Patch.PatchMethod.ReturnType` from Harmony's patch info, and
  it is not confirmed that BepInEx's bundled Harmony reports it for every patch (a patch with no method counts as able to
  skip the original);
- that a real weather mod's patch on `CloudLayer.Update` is still caught (a bool prefix or a transpiler). `neutral.rain`
  and `WeatherController` were not tested here, and what they patch was not looked at again;

and for 1.0.1 and 1.0.2:

- that the automatic start fires about 20 s after you settle into level flight (it is read from
  `GameManager.GetLocalAircraft`, the camera's `followingUnit`, `radarAlt`, `IsLanded()`, the map state and the
  aircraft's height, not tested), including when a mission spawns you straight into the air or into a hangar, and
  whether 10 m/s is a good limit for "steady" (gentle autopilot corrections should stay well under it);
- that the run-time checks (aircraft lost or changed, camera on another unit, map opened) catch what they should and
  nothing else: for example, whether any of the game's camera views changes `followingUnit` while you stay in your
  aircraft, which would make every window measure again;
- that `BenchmarkAutoRuns` is counted down and saved after a finished automatic run, and left alone after an aborted
  one; and whether BepInEx or a configuration manager reloads the file when the plugin saves it (it is saved after
  the run has ended, so it cannot abort that run);
- the round-end rule: round lengths (218-256 s) are calculated, not measured, so how many rounds a given
  `BenchmarkMinutes` gives is an estimate;
- that a run of several rounds goes back from the stock phases to `all-on` cleanly in the second and later rounds (the
  deck switches back on mid-run, as it does at the end of a 1.0.1 run);

- which counters the release build reports (the log says so about 10 s into the first mission);
- which rain material is chosen, and whether it renders correctly;
- whether hiding the far cloud plane at `PlaneCullMargin` shows as a visible pop when climbing or descending (the
  `top-plane-hidden` phase and a slow climb through 500 m under a 600 m base would show it);
- whether a puff budget of 0.5 still looks like a solid deck;
- how much each setting saves (that is what the benchmark is for).

Look in the log for lines from "NO Cloud Deck": each feature logs its own failure once and falls back to the stock
game.
