# TouchScreen play-style classifier

Server-side replay heuristic that decides whether a TD-tagged osu!
replay's cursor trace shows **teleport** between hits (could be multi-
finger aim) or **continuous motion** through hits (proof of single-
finger aim). Output drives whether the TD pp penalty applies on
recalc — continuous-motion plays get the FairTouchScreen outcome,
teleport plays keep the penalty.

## TL;DR

```
POST /touchscreen/classify
{
  "replay_file": "<base64 .osr bytes>",
  "beatmap_file": "<raw .osu text>",
  "beatmap_id": 12345,    // optional, log-only
  "score_id": 67890       // optional, log-only
}
```

```
200 OK
{
  "style": "tap" | "drag" | "mixed" | "unknown",
  "confidence": 0.83,
  "metrics": {
    "stationary_ratio": 0.62,
    "moving_ratio":     0.05,
    "jumping_ratio":    0.04,
    "path_inflation":   1.02,
    "max_stillness_fraction": 0.51,
    "intervals_analysed": 47,
    "total_frames_analysed": 1183,
    "tap_score":  0.78,
    "drag_score": 0.21
  }
}
```

When `style == "drag"`, the **caller** (g0v0-server) persists that on
the score row (`scores.td_play_style = 2`) and the pp recalc pipeline
strips the TD mod before invoking the pp formulae — the
"FairTouchScreen" outcome. Tap / Mixed / Unknown verdicts all keep
the TD mod applied. No client code is touched anywhere in the chain.

## Why Drag (and not Tap) is FairTouchScreen

The TD pp penalty exists because touch input lets the player use
**multiple fingers for aim** — multi-finger aim makes jumps trivially
easy compared to a mouse / tablet, where one cursor must travel to
each note.

In the replay's cursor trace:
- A teleporting cursor (`Tap` verdict) is **compatible with** multi-finger
  aim — each finger pre-positions on a different note, and the
  recorded cursor just reflects whichever touched last. We can't
  distinguish single-finger tap from multi-finger tap, so we
  conservatively assume the worst (multi-finger) and keep the penalty.
- A continuously moving cursor (`Drag` verdict) is **proof of
  single-finger aim**. osu!'s input layer filters secondary-touch
  positions from the cursor trace — they fire button events but never
  move the cursor — so a continuous cursor cannot be produced by
  multi-finger aim. One finger maintained primary contact, dragging
  through every hit position. The multi-finger-aim premise of the TD
  penalty doesn't apply, and the score deserves the same pp as the
  same play would get on tablet / mouse.

## Why this lives only in the perf server

The decision is made on the server, on a `.osr` already in storage,
not on the client. Concretely:

- The classifier needs to be cheat-resistant. A client that classifies
  itself can lie. Replay analysis on already-submitted bytes can't be
  faked by a future user input — the data already exists.
- The classifier needs to apply retroactively to ~30% of the existing
  TD score corpus (the ones with replays). Putting logic in the client
  release would only help future plays.
- The thresholds are tunable on the server without forcing a client
  release. A drag-tapper who studies a leaked threshold list can't
  craft an input that beats the bar if the thresholds move underneath
  them.

Anything in this `TouchScreen/` directory should be assumed to be
server-only forever. None of these types are referenced from the
shared `osu.Game.*` DLLs.

## How the heuristic works

For each pair of consecutive hit objects in the beatmap, the gap
between `current.GetEndTime()` and `next.StartTime` is an
**inter-hit interval**. By construction no hit object's playable
window overlaps an interval (we use `GetEndTime` not `StartTime`).
That matters because sliders and spinners force cursor motion
regardless of input technique — every touch player looks like a drag
player during them, so we exclude those windows entirely.

For each surviving interval (≥ 50 ms, ≤ 4 s, ≥ 5 frames), we walk
the replay frames in time order and bucket each one by its cursor
velocity since the previous frame:

| Velocity (px/ms) | Bucket | Tap pattern | Drag pattern |
|---|---|---|---|
| < 0.05 | **stationary** | most of the interval | almost none |
| 0.05 – 3.0 | **moving** | very rare | most of the interval |
| > 3.0 | **jumping** | a few per interval (the lift-and-replant) | essentially none |

The "stationary on lift" assumption is platform behaviour: every
osu!-supported touchscreen layer reports the cursor as held at the
last touched position when contact is released. Combined with the
fact that you can't physically move your finger between two hit
positions in zero time without lifting, a tap player's replay
**must** contain large stretches of stationary frames interrupted by
single-frame jumps. A drag player's replay can't contain those
stretches.

We also compute:

- **Path inflation** = (cursor distance traversed in the interval) /
  (Euclidean distance between the two hit positions). A tap player
  scores near 0 (no movement) or near 1 (straight-line jump); a drag
  player scores above 1 (curved drag).
- **Max stillness fraction** = longest contiguous stationary run / total
  frames in the interval. Catches plays where the cursor jitters
  slightly (sub-threshold movement) while held — a stylus tip is not
  perfectly still, but the longest still run is still very long.

Across all surviving intervals we take medians (robust against the
noisy tail — slider-leave intervals where cursor is still bleeding
motion, BPM jumps, etc.) and feed those into a weighted composite for
each style. The weights live in `TouchScreenClassifierConfig.cs`
with their reasoning written inline; **edit the file and explain your
evidence in the comment when you tune**.

```
tap_score  = 0.55·stationary + 0.30·max_stillness + 0.15·min(4·jumping, 1)
drag_score = 0.55·moving + 0.25·path_inflation_excess + 0.20·(1 − stationary)
```

A verdict requires the winning score to clear an absolute floor (0.55)
AND beat the opposing score by a margin (0.20). Anything inside that
window is classified `Mixed` and treated as Drag downstream
(conservative — no free pp from ambiguous calls).

Confidence is the winning composite score, scaled down on
sub-20-interval replays so a Tap call on 6 intervals is correctly
reported as shakier than the same call on 60. Replays with fewer
than 5 surviving intervals get `Unknown` with confidence 0, and the
score keeps its existing TD penalty (the conservative default).

## Tuning

All thresholds are in `TouchScreenClassifierConfig.cs`. The unit
tests-to-build (TBD) replay synthetic frame streams against the
classifier and assert verdict + confidence ranges. When you adjust a
threshold, also update the tests so the chosen value gets evidence
attached, not just intuition.

## Integration points outside this directory

- `TouchScreenController.cs` exposes the HTTP endpoint above. It
  delegates parsing of the .osr to `ReplayLoader.cs` (a thin shim
  over `osu.Game.Scoring.Legacy.LegacyScoreDecoder` that injects the
  perf server's ruleset manager).
- `PerformanceController.cs` was extended with one extra field on the
  request body, `td_play_style`. When equal to `"tap"`, the controller
  removes any `ModTouchDevice` from the calculator's input mods just
  before calling `CreatePerformanceCalculator()`. That's the entire
  pp-bypass mechanism — surgical, server-side, zero client impact.
- g0v0-server's `app/calculators/performance/performance_server.py`
  has matching helpers (`classify_touchscreen`,
  `td_play_style_from_wire`) and threads the persisted enum back into
  the `/performance` call. The replay-upload endpoint in
  `app/router/lio.py` calls the classifier automatically after saving
  a TD score's replay; the bulk job in
  `tools/classify_touchscreen.py` reprocesses historical TD scores
  with replays in one shot.
