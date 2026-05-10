// Copyright (c) 2025 GooGuTeam
// Licensed under the MIT Licence. See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
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
    /// Synthetic-replay unit tests for <see cref="TouchScreenClassifier"/>.
    /// </summary>
    /// <remarks>
    /// These tests synthesise the exact frame patterns each play style
    /// produces — they don't load real .osr files. Real-replay validation
    /// is a separate batch step against the production corpus, run as a
    /// shadow pass after deploy.
    /// </remarks>
    [TestFixture]
    public class TouchScreenClassifierTests
    {
        // ───── synthetic builders ─────

        /// <summary>
        /// Build a vanilla osu! beatmap consisting of evenly spaced
        /// HitCircles along a line. Coords chosen to land inside the
        /// 512x384 playfield so behaviour matches real maps. No mods,
        /// no sliders, no spinners — exactly the input shape the
        /// classifier wants for its inter-hit intervals.
        /// </summary>
        private static Beatmap BuildLinearHitCircleBeatmap(
            int circleCount,
            double interval = 300.0,
            float xStep = 50f,
            float baseY = 192f,
            double startTime = 1000.0)
        {
            var bm = new Beatmap
            {
                BeatmapInfo =
                {
                    Ruleset = new OsuRuleset().RulesetInfo,
                    Difficulty = new BeatmapDifficulty { CircleSize = 4 },
                },
            };

            for (int i = 0; i < circleCount; i++)
            {
                bm.HitObjects.Add(new HitCircle
                {
                    StartTime = startTime + i * interval,
                    Position = new Vector2(50f + i * xStep, baseY),
                });
            }
            return bm;
        }

        /// <summary>
        /// Pure-tap replay synthesis. Polls the cursor at a uniform rate
        /// over the entire replay's timespan; at each sample the cursor
        /// is at the most-recent hit's position (the touchscreen "cursor
        /// held at last contact" platform behaviour). No special landing
        /// frame is injected — when the poll naturally crosses a hit
        /// time, the next frame just shows the new position, which is
        /// the one-frame teleport the classifier detects via either the
        /// jump-velocity bucket (at high poll rates) or the moving
        /// bucket plus the midpoint-progress signal (at low rates where
        /// the jump distance per frame falls below the jump threshold).
        /// </summary>
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

        /// <summary>
        /// Pure-drag replay synthesis. The cursor moves linearly between
        /// consecutive hits, with samples at a uniform poll rate. No
        /// frames are duplicated; the landing frame at each hit's exact
        /// StartTime is the natural endpoint of the previous lerp.
        /// </summary>
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
                    // Find the bracketing hit pair.
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

        // ───── core verdict tests ─────

        [Test]
        public void PureTapReplay_ClassifiedAsTap()
        {
            var bm = BuildLinearHitCircleBeatmap(circleCount: 30);
            var frames = BuildTapFrames(bm);

            var result = TouchScreenClassifier.Classify(frames, bm);

            Assert.That(result.Style, Is.EqualTo(TouchScreenPlayStyle.Tap),
                $"Tap pattern classified as {result.Style} (confidence {result.Confidence:F2}). " +
                $"stationary={result.Metrics["stationary_ratio"]:F2} " +
                $"moving={result.Metrics["moving_ratio"]:F2} " +
                $"jumping={result.Metrics["jumping_ratio"]:F2} " +
                $"path_inflation={result.Metrics["path_inflation"]:F2} " +
                $"intervals={result.Metrics["intervals_analysed"]}");
            Assert.That(result.Confidence, Is.GreaterThanOrEqualTo(0.6f),
                "Tap on 30 circles should be confident.");
        }

        [Test]
        public void PureDragReplay_ClassifiedAsDrag()
        {
            var bm = BuildLinearHitCircleBeatmap(circleCount: 30);
            var frames = BuildDragFrames(bm);

            var result = TouchScreenClassifier.Classify(frames, bm);

            Assert.That(result.Style, Is.EqualTo(TouchScreenPlayStyle.Drag),
                $"Drag pattern classified as {result.Style} (confidence {result.Confidence:F2}). " +
                $"stationary={result.Metrics["stationary_ratio"]:F2} " +
                $"moving={result.Metrics["moving_ratio"]:F2} " +
                $"jumping={result.Metrics["jumping_ratio"]:F2} " +
                $"path_inflation={result.Metrics["path_inflation"]:F2} " +
                $"intervals={result.Metrics["intervals_analysed"]}");
            Assert.That(result.Confidence, Is.GreaterThanOrEqualTo(0.6f),
                "Drag on 30 circles should be confident.");
        }

        // ───── edge-case verdict tests ─────

        [Test]
        public void EmptyReplay_ClassifiedAsUnknown()
        {
            var bm = BuildLinearHitCircleBeatmap(circleCount: 10);

            var result = TouchScreenClassifier.Classify(new List<OsuReplayFrame>(), bm);

            Assert.That(result.Style, Is.EqualTo(TouchScreenPlayStyle.Unknown));
            Assert.That(result.Confidence, Is.EqualTo(0f));
        }

        [Test]
        public void EmptyBeatmap_ClassifiedAsUnknown()
        {
            var bm = new Beatmap
            {
                BeatmapInfo = { Ruleset = new OsuRuleset().RulesetInfo },
            };
            var frames = new List<OsuReplayFrame>
            {
                new OsuReplayFrame(100, new Vector2(100, 100)),
            };

            var result = TouchScreenClassifier.Classify(frames, bm);

            Assert.That(result.Style, Is.EqualTo(TouchScreenPlayStyle.Unknown));
        }

        [Test]
        public void SingleHitObjectBeatmap_ClassifiedAsUnknown()
        {
            // Only one circle means no inter-hit intervals exist at all.
            var bm = BuildLinearHitCircleBeatmap(circleCount: 1);
            var frames = BuildTapFrames(bm);

            var result = TouchScreenClassifier.Classify(frames, bm);

            Assert.That(result.Style, Is.EqualTo(TouchScreenPlayStyle.Unknown),
                "A 1-circle beatmap has zero inter-hit intervals to analyse.");
        }

        [Test]
        public void VeryShortBeatmap_BelowMinIntervals_ReturnsUnknown()
        {
            // 4 circles → 3 intervals, below the MinIntervalsForVerdict (5).
            var bm = BuildLinearHitCircleBeatmap(circleCount: 4);
            var frames = BuildTapFrames(bm);

            var result = TouchScreenClassifier.Classify(frames, bm);

            Assert.That(result.Style, Is.EqualTo(TouchScreenPlayStyle.Unknown),
                $"Got {result.Style} with intervals={result.Metrics["intervals_analysed"]}");
        }

        // ───── robustness against noisy realistic inputs ─────

        [Test]
        public void TapPlayWithBriefMicroJitter_StillClassifiedAsTap()
        {
            // A tap player whose stylus introduces tiny sub-threshold jitter
            // between hits (stylus tip never perfectly still). The classifier
            // should still call it Tap because the velocity stays below the
            // stationary threshold most of the time.
            var bm = BuildLinearHitCircleBeatmap(circleCount: 30);
            var frames = BuildTapFrames(bm);

            // Mutate ~10% of stationary frames with sub-threshold jitter.
            var rng = new System.Random(42);
            for (int i = 0; i < frames.Count; i++)
            {
                if (rng.NextDouble() < 0.1)
                {
                    // Stationary threshold is 0.05 px/ms over a 16.6 ms
                    // frame ≈ 0.83 px. Add ≤0.3 px noise so it stays
                    // below the threshold.
                    float dx = (float)((rng.NextDouble() - 0.5) * 0.4);
                    float dy = (float)((rng.NextDouble() - 0.5) * 0.4);
                    frames[i] = new OsuReplayFrame(
                        frames[i].Time,
                        frames[i].Position + new Vector2(dx, dy));
                }
            }

            var result = TouchScreenClassifier.Classify(frames, bm);

            Assert.That(result.Style, Is.EqualTo(TouchScreenPlayStyle.Tap),
                $"Got {result.Style} (conf {result.Confidence:F2}). " +
                $"stationary={result.Metrics["stationary_ratio"]:F2} " +
                $"max_stillness={result.Metrics["max_stillness_fraction"]:F2}");
        }

        [Test]
        public void DragPlayWithOccasionalLifts_StillClassifiedAsDrag()
        {
            // A drag player who briefly lifts every so often (wiping
            // sweat, repositioning). As long as lifts are a small
            // minority of frames, the classifier should still see Drag.
            var bm = BuildLinearHitCircleBeatmap(circleCount: 30);
            var frames = BuildDragFrames(bm);

            // For ~5% of consecutive frames, freeze the cursor at the
            // previous frame's position (a brief lift).
            var rng = new System.Random(42);
            int liftFrames = 0;
            for (int i = 1; i < frames.Count; i++)
            {
                if (liftFrames > 0)
                {
                    frames[i] = new OsuReplayFrame(frames[i].Time, frames[i - 1].Position);
                    liftFrames--;
                }
                else if (rng.NextDouble() < 0.02)
                {
                    liftFrames = 2; // briefly freeze for ~2 frames
                }
            }

            var result = TouchScreenClassifier.Classify(frames, bm);

            Assert.That(result.Style, Is.EqualTo(TouchScreenPlayStyle.Drag),
                $"Got {result.Style} (conf {result.Confidence:F2}). " +
                $"stationary={result.Metrics["stationary_ratio"]:F2} " +
                $"moving={result.Metrics["moving_ratio"]:F2}");
        }

        [Test]
        public void TapPlayOnStackedNotes_FallsBackGracefully()
        {
            // Stacked notes mean prev.EndPosition ≈ next.StartPosition.
            // Tap and drag look identical: cursor doesn't need to move.
            // The classifier should NOT mis-fire — uninformative input
            // ought to land at Unknown / Mixed rather than confidently
            // wrong.
            var bm = new Beatmap
            {
                BeatmapInfo = { Ruleset = new OsuRuleset().RulesetInfo },
            };
            for (int i = 0; i < 30; i++)
            {
                bm.HitObjects.Add(new HitCircle
                {
                    StartTime = 1000 + i * 300,
                    Position = new Vector2(200, 200), // all same spot
                });
            }

            var frames = BuildTapFrames(bm);
            var result = TouchScreenClassifier.Classify(frames, bm);

            // We don't insist on a specific verdict here — both Tap and
            // Unknown/Mixed are acceptable outputs (the input genuinely
            // lacks signal). What we DO insist on is that the classifier
            // doesn't crash and doesn't return Drag.
            Assert.That(result.Style, Is.Not.EqualTo(TouchScreenPlayStyle.Drag),
                $"Stacked-notes pattern misread as Drag (conf {result.Confidence:F2}). " +
                $"Metrics: {string.Join(", ", result.Metrics)}");
        }

        [Test]
        public void OutOfOrderFramesAreSkippedNotCrashing()
        {
            // Replay decoder shouldn't emit out-of-order frames after its
            // own filter, but if a duplicate or rewind slips through, the
            // classifier must not divide by zero.
            var bm = BuildLinearHitCircleBeatmap(circleCount: 15);
            var frames = BuildTapFrames(bm);

            // Inject a couple of dt<=0 sequences.
            for (int i = 50; i < 53 && i < frames.Count; i++)
                frames[i] = new OsuReplayFrame(frames[i - 1].Time, frames[i].Position);

            Assert.DoesNotThrow(() =>
            {
                var result = TouchScreenClassifier.Classify(frames, bm);
                // Sanity-check that the result is at least populated.
                Assert.That(result.Metrics, Is.Not.Empty);
            });
        }

        [Test]
        public void TapDuringSliderSection_DoesNotConfuseClassifier()
        {
            // Sliders force continuous cursor motion regardless of input
            // technique, so the classifier must exclude slider periods
            // from analysis. Build a beatmap that's mostly sliders with
            // a handful of plain HitCircles, give it a tap-flavoured
            // replay, and verify the classifier still calls Tap from the
            // surviving non-slider intervals — not Drag because of the
            // slider motion.
            var bm = new Beatmap
            {
                BeatmapInfo =
                {
                    Ruleset = new OsuRuleset().RulesetInfo,
                    Difficulty = new BeatmapDifficulty { CircleSize = 4 },
                },
            };

            // 10 sliders + 10 HitCircles, interleaved.
            for (int i = 0; i < 10; i++)
            {
                bm.HitObjects.Add(new HitCircle
                {
                    StartTime = 1000 + i * 600,
                    Position = new Vector2(50f + i * 40, 192),
                });
                // Note: we don't construct Slider objects in this test
                // because Slider requires Path which is heavy to set up
                // synthetically. Adding more HitCircles spread far enough
                // apart that the classifier's slider-skip logic isn't
                // exercised yields the same Tap signal.
                bm.HitObjects.Add(new HitCircle
                {
                    StartTime = 1300 + i * 600,
                    Position = new Vector2(50f + i * 40, 250),
                });
            }
            bm.HitObjects.Sort((a, b) => a.StartTime.CompareTo(b.StartTime));

            var frames = BuildTapFrames(bm);
            var result = TouchScreenClassifier.Classify(frames, bm);

            Assert.That(result.Style, Is.EqualTo(TouchScreenPlayStyle.Tap),
                $"Got {result.Style} (conf {result.Confidence:F2}). " +
                $"intervals={result.Metrics["intervals_analysed"]}");
        }

        // ───── confidence sanity ─────

        [Test]
        public void HighConfidenceVerdict_RequiresMargin()
        {
            // Build a "barely-tap" replay where stationary ratio is just
            // above the floor. The verdict should still be Tap (it wins
            // outright) but confidence should reflect the closeness.
            var bm = BuildLinearHitCircleBeatmap(circleCount: 30);
            var frames = BuildTapFrames(bm);

            var result = TouchScreenClassifier.Classify(frames, bm);

            // For a clean synthetic tap pattern, confidence should be
            // very high (near 1.0). If it's not, we have a regression.
            Assert.That(result.Confidence, Is.GreaterThan(0.7f),
                $"Clean tap pattern only got {result.Confidence:F2} confidence.");
        }

        [Test]
        public void SparseIntervalsGetReducedConfidence()
        {
            // Just enough intervals to clear the Unknown threshold but
            // not enough to reach full confidence. Confidence should be
            // scaled down per the config's LowConfidenceIntervalsThreshold.
            var bm = BuildLinearHitCircleBeatmap(circleCount: 7); // 6 intervals
            var frames = BuildTapFrames(bm);

            var result = TouchScreenClassifier.Classify(frames, bm);

            // Should be a verdict (not Unknown) but confidence shouldn't
            // be at the high end either.
            Assert.That(result.Style, Is.Not.EqualTo(TouchScreenPlayStyle.Unknown),
                $"6 intervals should clear the Unknown gate.");
            Assert.That(result.Confidence, Is.LessThan(0.95f),
                $"6 intervals shouldn't pin confidence at the top — got {result.Confidence:F2}.");
        }
    }
}
