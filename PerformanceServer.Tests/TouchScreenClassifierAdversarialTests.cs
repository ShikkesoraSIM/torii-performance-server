// Copyright (c) 2025 GooGuTeam
// Licensed under the MIT Licence. See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Osu.Replays;
using osuTK;
using PerformanceServer.TouchScreen;

namespace PerformanceServer.Tests
{
    /// <summary>
    /// Adversarial tests: realistic noisy patterns, edge BPMs, mixed
    /// styles, and metric-level assertions (not just the verdict label).
    /// </summary>
    /// <remarks>
    /// Where the happy-path tests in <see cref="TouchScreenClassifierTests"/>
    /// answer "does the classifier work on textbook input", these answer
    /// "does it stay accurate when the input misbehaves" — fast streams,
    /// slow drags, mostly-tap-with-bursts-of-drag, irregular frame poll
    /// rates, etc. Each test also calls out the metric range we expect,
    /// so a future refactor that breaks the metric balance fails loudly
    /// instead of silently shifting verdicts.
    /// </remarks>
    [TestFixture]
    public class TouchScreenClassifierAdversarialTests
    {
        private static Beatmap BuildBeatmap(IEnumerable<(double time, Vector2 pos)> circles)
        {
            var bm = new Beatmap
            {
                BeatmapInfo =
                {
                    Ruleset = new OsuRuleset().RulesetInfo,
                    Difficulty = new BeatmapDifficulty { CircleSize = 4 },
                },
            };
            foreach (var (time, pos) in circles)
            {
                bm.HitObjects.Add(new HitCircle { StartTime = time, Position = pos });
            }
            return bm;
        }

        // ───── BPM / frame rate stress ─────

        [Test]
        public void HighBpmStream_TapPattern_StillTap()
        {
            // 240 BPM 1/4 = 250 ms intervals — fast but humanly playable
            // for tap on harder maps. Build 60 circles in a tight stream.
            var circles = Enumerable.Range(0, 60)
                                    .Select(i => (
                                        time: 1000.0 + i * 250,
                                        pos: new Vector2(100 + (i % 2) * 80, 200)))
                                    .ToList();
            var bm = BuildBeatmap(circles);
            var frames = BuildTapFrames(bm, frameRateHz: 240);

            var result = TouchScreenClassifier.Classify(frames, bm);

            Assert.That(result.Style, Is.EqualTo(TouchScreenPlayStyle.Tap),
                $"High-BPM tap stream misread as {result.Style}. " +
                $"stationary={result.Metrics["stationary_ratio"]:F2} " +
                $"jumping={result.Metrics["jumping_ratio"]:F2} " +
                $"intervals={result.Metrics["intervals_analysed"]}");
            Assert.That(result.Metrics["intervals_analysed"], Is.GreaterThanOrEqualTo(50),
                "Most intervals on a tight stream should clear the 50ms floor.");
        }

        [Test]
        public void HighBpmStream_DragPattern_StillDrag()
        {
            // Same 240 BPM stream, dragged through. Use a realistic
            // inter-hit spacing (40 px/interval) so the cursor's actual
            // motion exceeds the stationary velocity floor — at very low
            // spacings (single-digit pixels) drag and tap look identical
            // to the velocity-bucket signals and only the midpoint
            // progress signal can tell them apart, which is its own
            // (separately covered) edge case.
            var circles = Enumerable.Range(0, 60)
                                    .Select(i => (
                                        time: 1000.0 + i * 250,
                                        pos: new Vector2(100 + (i % 2) * 40, 200)))
                                    .ToList();
            var bm = BuildBeatmap(circles);
            var frames = BuildDragFrames(bm, frameRateHz: 240);

            var result = TouchScreenClassifier.Classify(frames, bm);

            Assert.That(result.Style, Is.EqualTo(TouchScreenPlayStyle.Drag),
                $"High-BPM drag stream misread as {result.Style}. " +
                $"moving={result.Metrics["moving_ratio"]:F2} " +
                $"stationary={result.Metrics["stationary_ratio"]:F2} " +
                $"midpoint={result.Metrics["midpoint_progress"]:F2}");
        }

        [Test]
        public void TightSpacingSlowDrag_FallsBackToMidpointSignal()
        {
            // When inter-hit spacing is so tight that per-frame motion
            // is below the stationary velocity floor (5 px between 250 ms
            // hits at 240 Hz = 0.02 px/ms, under the 0.05 floor), the
            // velocity-bucket signals can't see the drag. The midpoint
            // progress signal can — cursor is at progress=0.5 at midpoint
            // — and it should rescue the verdict to at least Drag or
            // Mixed (NOT Tap, which would unjustly strip the TD penalty).
            var circles = Enumerable.Range(0, 60)
                                    .Select(i => (
                                        time: 1000.0 + i * 250,
                                        pos: new Vector2(100 + i * 5, 200)))
                                    .ToList();
            var bm = BuildBeatmap(circles);
            var frames = BuildDragFrames(bm, frameRateHz: 240);

            var result = TouchScreenClassifier.Classify(frames, bm);

            // Drag is the desired outcome; Mixed is acceptable (treated
            // as Drag downstream → TD penalty stays applied → no false
            // FairTouchScreen). Tap is forbidden — that would be a
            // confident misclassification giving free pp to a drag-tap
            // cheese play.
            Assert.That(result.Style, Is.Not.EqualTo(TouchScreenPlayStyle.Tap),
                $"Slow-drag misread as Tap (conf {result.Confidence:F2}). " +
                $"midpoint_progress={result.Metrics["midpoint_progress"]:F2} — " +
                $"if this is near 0.5 the midpoint signal is correct but the " +
                $"composite weights aren't giving it enough authority.");
        }

        [Test]
        public void LowBpmCalm_TapPattern_StillTap()
        {
            // 90 BPM intervals — sluggish, lots of frames per interval.
            var circles = Enumerable.Range(0, 20)
                                    .Select(i => (
                                        time: 1000.0 + i * 666.0,
                                        pos: new Vector2(50 + i * 25, 192)))
                                    .ToList();
            var bm = BuildBeatmap(circles);
            var frames = BuildTapFrames(bm);

            var result = TouchScreenClassifier.Classify(frames, bm);

            Assert.That(result.Style, Is.EqualTo(TouchScreenPlayStyle.Tap));
            Assert.That(result.Metrics["stationary_ratio"], Is.GreaterThan(0.7),
                "On a calm map, the cursor spends >70% of each interval held.");
        }

        [Test]
        public void IrregularFrameRate_DoesNotConfuseClassifier()
        {
            // Some hardware delivers replay frames at wildly varying rates
            // (poll intervals of 4–20 ms mixed). Replay the tap pattern
            // with jittery frame timing and verify the verdict survives.
            var circles = Enumerable.Range(0, 30)
                                    .Select(i => (
                                        time: 1000.0 + i * 300,
                                        pos: new Vector2(50 + i * 15, 192)))
                                    .ToList();
            var bm = BuildBeatmap(circles);
            var frames = BuildTapFrames(bm, frameRateHz: 60);

            // Perturb every frame's time by ±5ms.
            var rng = new System.Random(1337);
            for (int i = 0; i < frames.Count; i++)
            {
                double jitter = (rng.NextDouble() - 0.5) * 10;
                frames[i] = new OsuReplayFrame(frames[i].Time + jitter, frames[i].Position);
            }
            // Re-sort because the jitter can flip neighbour ordering.
            frames.Sort((a, b) => a.Time.CompareTo(b.Time));

            var result = TouchScreenClassifier.Classify(frames, bm);

            Assert.That(result.Style, Is.EqualTo(TouchScreenPlayStyle.Tap),
                $"Got {result.Style} (conf {result.Confidence:F2}).");
        }

        // ───── mixed-style verdict ─────

        [Test]
        public void HalfTapHalfDrag_GetsMixedOrConservative()
        {
            // First half of the map played as tap, second half as drag.
            // The verdict should NOT be confidently Tap — that would give
            // free pp to a player who clearly drag-cheesed the back half.
            var circles = Enumerable.Range(0, 40)
                                    .Select(i => (
                                        time: 1000.0 + i * 400,
                                        pos: new Vector2(50 + i * 10, 192)))
                                    .ToList();
            var bm = BuildBeatmap(circles);

            var tapFrames = BuildTapFrames(BuildBeatmap(circles.Take(20)));
            var dragFrames = BuildDragFrames(BuildBeatmap(circles.Skip(19).ToList()));
            // Skip(19) so there's a shared boundary hit between the two halves.

            var frames = tapFrames.Concat(dragFrames)
                                  .OrderBy(f => f.Time)
                                  .ToList();
            // Dedupe possible identical (Time, Position) at the boundary.
            for (int i = frames.Count - 1; i > 0; i--)
            {
                if (frames[i].Time == frames[i - 1].Time && frames[i].Position == frames[i - 1].Position)
                    frames.RemoveAt(i);
            }

            var result = TouchScreenClassifier.Classify(frames, bm);

            // Acceptable outcomes:
            //   * Mixed (treated as Drag downstream — conservative)
            //   * Drag (the second half dominated)
            //   * Unknown (shouldn't happen with 39 intervals but tolerated)
            // NOT acceptable:
            //   * Tap (would unjustly strip the TD penalty)
            Assert.That(result.Style, Is.Not.EqualTo(TouchScreenPlayStyle.Tap),
                $"Half-and-half play misread as Tap (conf {result.Confidence:F2}). " +
                $"tap_score={result.Metrics["tap_score"]:F2} " +
                $"drag_score={result.Metrics["drag_score"]:F2}");
        }

        // ───── metric-level sanity ─────

        [Test]
        public void TapMetrics_HaveExpectedShape()
        {
            var bm = BuildBeatmap(Enumerable.Range(0, 30)
                                            .Select(i => (
                                                time: 1000.0 + i * 300,
                                                pos: new Vector2(50 + i * 20, 192))));
            var frames = BuildTapFrames(bm);

            var result = TouchScreenClassifier.Classify(frames, bm);

            // Pin the metric ranges. If a future refactor breaks one of
            // these, the verdict might still pass on this clean input
            // but the heuristic would have silently drifted. Pinning
            // catches that.
            Assert.Multiple(() =>
            {
                Assert.That(result.Metrics["stationary_ratio"], Is.GreaterThan(0.7),
                    "Tap pattern: stationary ratio should be > 0.7");
                Assert.That(result.Metrics["moving_ratio"], Is.LessThan(0.2),
                    "Tap pattern: moving ratio should be < 0.2");
                Assert.That(result.Metrics["path_inflation"], Is.LessThan(1.1),
                    "Tap pattern: path inflation should be near 1.0 (straight teleport).");
                Assert.That(result.Metrics["tap_score"], Is.GreaterThan(result.Metrics["drag_score"]),
                    "Tap pattern: tap_score should clearly beat drag_score.");
            });
        }

        [Test]
        public void DragMetrics_HaveExpectedShape()
        {
            var bm = BuildBeatmap(Enumerable.Range(0, 30)
                                            .Select(i => (
                                                time: 1000.0 + i * 300,
                                                pos: new Vector2(50 + i * 20, 192))));
            var frames = BuildDragFrames(bm);

            var result = TouchScreenClassifier.Classify(frames, bm);

            Assert.Multiple(() =>
            {
                Assert.That(result.Metrics["moving_ratio"], Is.GreaterThan(0.7),
                    "Drag pattern: moving ratio should be > 0.7");
                Assert.That(result.Metrics["stationary_ratio"], Is.LessThan(0.2),
                    "Drag pattern: stationary ratio should be < 0.2");
                Assert.That(result.Metrics["drag_score"], Is.GreaterThan(result.Metrics["tap_score"]),
                    "Drag pattern: drag_score should clearly beat tap_score.");
            });
        }

        // ───── interval filter robustness ─────

        [Test]
        public void IntervalShorterThan50ms_IsSkipped()
        {
            // Build a beatmap where some intervals are < 50ms. Confirm the
            // classifier filters those and still reaches a verdict on the
            // remaining intervals.
            var times = new[] { 1000.0, 1020.0, 1040.0, 1500.0, 1800.0, 2100.0, 2400.0, 2700.0, 3000.0, 3300.0 };
            var bm = BuildBeatmap(times.Select((t, i) => (time: t, pos: new Vector2(50 + i * 20, 192))));
            var frames = BuildTapFrames(bm);

            var result = TouchScreenClassifier.Classify(frames, bm);

            Assert.That(result.Metrics["intervals_analysed"], Is.LessThan(9),
                "First two intervals (20ms each) should be filtered out.");
        }

        [Test]
        public void IntervalLongerThan4000ms_IsSkipped()
        {
            // Simulate a break period — a 6-second gap between two hits.
            // That interval should be excluded entirely.
            var bm = BuildBeatmap(new[]
            {
                (time: 1000.0, pos: new Vector2(100, 100)),
                (time: 1400.0, pos: new Vector2(120, 100)),
                (time: 1800.0, pos: new Vector2(140, 100)),
                (time: 2200.0, pos: new Vector2(160, 100)),
                (time: 2600.0, pos: new Vector2(180, 100)),
                (time: 9000.0, pos: new Vector2(200, 100)), // 6.4s gap
                (time: 9400.0, pos: new Vector2(220, 100)),
                (time: 9800.0, pos: new Vector2(240, 100)),
                (time: 10200.0, pos: new Vector2(260, 100)),
                (time: 10600.0, pos: new Vector2(280, 100)),
            });
            var frames = BuildTapFrames(bm);
            var result = TouchScreenClassifier.Classify(frames, bm);

            Assert.That(result.Metrics["intervals_analysed"], Is.LessThanOrEqualTo(8),
                "9 candidates − 1 over-long interval = 8 analysed.");
        }

        [Test]
        public void OutOfOrderHitObjects_AreSortedBeforeAnalysis()
        {
            // Beatmap with hit objects in the WRONG order — verify the
            // classifier sorts internally and produces the same verdict
            // as if they'd been ordered.
            var bm = new Beatmap
            {
                BeatmapInfo = { Ruleset = new OsuRuleset().RulesetInfo },
            };
            var ordered = Enumerable.Range(0, 30)
                                    .Select(i => new HitCircle
                                    {
                                        StartTime = 1000.0 + i * 300,
                                        Position = new Vector2(50 + i * 15, 192),
                                    })
                                    .ToList();
            // Shuffle.
            var rng = new System.Random(7);
            for (int i = ordered.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (ordered[i], ordered[j]) = (ordered[j], ordered[i]);
            }
            foreach (var h in ordered) bm.HitObjects.Add(h);

            // Build frames against the SORTED beatmap so we get clean
            // tap pattern, then pass the SHUFFLED beatmap to the
            // classifier — it must sort internally.
            var sortedBm = new Beatmap
            {
                BeatmapInfo = { Ruleset = new OsuRuleset().RulesetInfo },
            };
            foreach (var h in ordered.OrderBy(o => o.StartTime))
                sortedBm.HitObjects.Add(h);
            var frames = BuildTapFrames(sortedBm);

            var result = TouchScreenClassifier.Classify(frames, bm);

            Assert.That(result.Style, Is.EqualTo(TouchScreenPlayStyle.Tap),
                $"Got {result.Style} on unsorted beatmap — classifier may not be sorting internally. " +
                $"intervals={result.Metrics["intervals_analysed"]}");
        }

        // ───── replay sparseness ─────

        [Test]
        public void IntervalsWithTooFewFrames_AreSkipped()
        {
            // Build a beatmap whose intervals normally pass the filter,
            // but then strip the replay frames down to ~2 per interval.
            // Those intervals must be skipped (MinFramesPerInterval = 5).
            var bm = BuildBeatmap(Enumerable.Range(0, 30)
                                            .Select(i => (
                                                time: 1000.0 + i * 200, // 200 ms intervals
                                                pos: new Vector2(50 + i * 10, 192))));
            // Build at 15 Hz → ~3 frames per 200 ms interval, below the
            // 5-frame minimum.
            var frames = BuildTapFrames(bm, frameRateHz: 15);

            var result = TouchScreenClassifier.Classify(frames, bm);

            Assert.That(result.Style, Is.EqualTo(TouchScreenPlayStyle.Unknown),
                $"Sparse replays should land at Unknown, got {result.Style} " +
                $"(intervals={result.Metrics.GetValueOrDefault("intervals_analysed", 0)}).");
        }

        // ───── consistency across many runs ─────

        [Test]
        public void Classifier_IsDeterministic()
        {
            // Same input → same verdict and metrics. No internal RNG, no
            // wall-clock dependency — this is a sanity guard for future
            // refactors.
            var bm = BuildBeatmap(Enumerable.Range(0, 30)
                                            .Select(i => (
                                                time: 1000.0 + i * 300,
                                                pos: new Vector2(50 + i * 15, 192))));
            var frames = BuildTapFrames(bm);

            var r1 = TouchScreenClassifier.Classify(frames, bm);
            var r2 = TouchScreenClassifier.Classify(frames, bm);
            var r3 = TouchScreenClassifier.Classify(frames, bm);

            Assert.That(r2.Style, Is.EqualTo(r1.Style));
            Assert.That(r3.Style, Is.EqualTo(r1.Style));
            Assert.That(r2.Confidence, Is.EqualTo(r1.Confidence));
            Assert.That(r3.Confidence, Is.EqualTo(r1.Confidence));
        }

        // ───── diagnostic dump (always passes) ─────

        [Test]
        public void DiagnosticDump_RealisticPatterns()
        {
            // Not an assertion test — just prints metrics for the canonical
            // patterns so we have human-readable numbers in the test
            // output. Useful when tuning thresholds.
            var bm = BuildBeatmap(Enumerable.Range(0, 30)
                                            .Select(i => (
                                                time: 1000.0 + i * 300,
                                                pos: new Vector2(50 + i * 15, 192))));

            var tap = TouchScreenClassifier.Classify(BuildTapFrames(bm), bm);
            var drag = TouchScreenClassifier.Classify(BuildDragFrames(bm), bm);

            Console.WriteLine($"\n  [TAP ]  style={tap.Style}  conf={tap.Confidence:F3}");
            foreach (var (k, v) in tap.Metrics.OrderBy(kv => kv.Key))
                Console.WriteLine($"           {k} = {v:F3}");

            Console.WriteLine($"\n  [DRAG]  style={drag.Style}  conf={drag.Confidence:F3}");
            foreach (var (k, v) in drag.Metrics.OrderBy(kv => kv.Key))
                Console.WriteLine($"           {k} = {v:F3}");

            Assert.Pass();
        }

        // ───── synthesis helpers ─────
        //
        // Identical logic to TouchScreenClassifierTests.BuildTapFrames /
        // BuildDragFrames — duplicated rather than shared because NUnit
        // test fixtures don't have a clean cross-class fixture-init story
        // and the helpers are short. If you change one, change both.

        private static List<OsuReplayFrame> BuildTapFrames(Beatmap bm, double frameRateHz = 60)
        {
            var hits = bm.HitObjects.Cast<HitCircle>().OrderBy(h => h.StartTime).ToList();
            if (hits.Count == 0) return new List<OsuReplayFrame>();

            double frameDt = 1000.0 / frameRateHz;
            var frames = new List<OsuReplayFrame>();

            double startT = hits[0].StartTime - frameDt;
            double endT = hits[^1].StartTime + frameDt;
            int hitIndex = 0;

            for (double t = startT; t <= endT + 1e-6; t += frameDt)
            {
                while (hitIndex + 1 < hits.Count && hits[hitIndex + 1].StartTime <= t)
                    hitIndex++;
                frames.Add(new OsuReplayFrame(t, hits[hitIndex].Position));
            }
            return frames;
        }

        private static List<OsuReplayFrame> BuildDragFrames(Beatmap bm, double frameRateHz = 60)
        {
            var hits = bm.HitObjects.Cast<HitCircle>().OrderBy(h => h.StartTime).ToList();
            if (hits.Count == 0) return new List<OsuReplayFrame>();

            double frameDt = 1000.0 / frameRateHz;
            var frames = new List<OsuReplayFrame>();

            double startT = hits[0].StartTime - frameDt;
            double endT = hits[^1].StartTime + frameDt;

            for (double t = startT; t <= endT + 1e-6; t += frameDt)
            {
                Vector2 pos;
                if (t <= hits[0].StartTime)
                    pos = hits[0].Position;
                else if (t >= hits[^1].StartTime)
                    pos = hits[^1].Position;
                else
                {
                    int k = 0;
                    while (k + 1 < hits.Count && hits[k + 1].StartTime <= t)
                        k++;
                    if (k + 1 >= hits.Count)
                    {
                        pos = hits[k].Position;
                    }
                    else
                    {
                        double duration = hits[k + 1].StartTime - hits[k].StartTime;
                        float progress = (float)((t - hits[k].StartTime) / duration);
                        pos = Vector2.Lerp(hits[k].Position, hits[k + 1].Position, progress);
                    }
                }
                frames.Add(new OsuReplayFrame(t, pos));
            }
            return frames;
        }
    }
}
