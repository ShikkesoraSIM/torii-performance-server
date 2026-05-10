// Copyright (c) 2025 GooGuTeam
// Licensed under the MIT Licence. See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Osu.Replays;
using osuTK;

namespace PerformanceServer.TouchScreen
{
    /// <summary>
    /// Decides whether a TD-tagged osu! replay is genuine tap play
    /// (FairTouchScreen — no pp penalty) or drag-tap cheese
    /// (regular TouchScreen — pp penalty applies).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The classifier is intentionally heuristic, not ML. The signals we use
    /// are physically grounded — they fall out of how a touchscreen reports
    /// cursor position on a lift event, not out of a black-box pattern. The
    /// thresholds in <see cref="TouchScreenClassifierConfig"/> are the
    /// tunable knobs; everything else here is structural.
    /// </para>
    ///
    /// <para>
    /// <b>Why these signals work.</b> On every osu!-supported platform the
    /// touch-input layer treats a touch release as "cursor stays at last
    /// reported position until a new touch begins". A tap player's
    /// behaviour is therefore:
    /// </para>
    /// <list type="number">
    ///   <item><description>Tap hit object A → cursor reported at A's position.</description></item>
    ///   <item><description>Lift finger → cursor frozen at A's position. (No frames of motion.)</description></item>
    ///   <item><description>Tap hit object B → cursor reported at B's position. (One frame of huge motion.)</description></item>
    /// </list>
    /// <para>
    /// A drag-tap player's behaviour is the opposite — the dragging finger
    /// never lifts, so frames between hits show a continuous stream of small
    /// movements. The reverse-engineerable difference is the cursor's
    /// stationary-frame ratio, and the symmetric jump-frame ratio.
    /// </para>
    ///
    /// <para>
    /// <b>What we exclude.</b> Frames during a slider or spinner are useless
    /// — the gameplay forces cursor motion regardless of input technique, so
    /// every touch player looks like a drag player there. We only analyse
    /// the gap intervals between consecutive hit objects (any object kind),
    /// using each object's <see cref="HitObjectExtensions.GetEndTime"/> as
    /// the interval-start and the next object's <c>StartTime</c> as the
    /// interval-end. By construction those windows contain no active hit
    /// object.
    /// </para>
    /// </remarks>
    public static class TouchScreenClassifier
    {
        /// <summary>
        /// Run the classification.
        /// </summary>
        /// <param name="frames">
        /// The replay's frames, already converted to <see cref="OsuReplayFrame"/>
        /// by the legacy decoder. Must be in chronological order.
        /// </param>
        /// <param name="beatmap">
        /// The playable beatmap (post-mod conversion). Hit objects are
        /// expected to be <see cref="OsuHitObject"/> instances; non-osu
        /// objects are silently skipped.
        /// </param>
        /// <returns>
        /// A verdict, a confidence, and the aggregated metric bag — see
        /// <see cref="TouchScreenAnalysisResult"/>.
        /// </returns>
        public static TouchScreenAnalysisResult Classify(
            IReadOnlyList<OsuReplayFrame> frames,
            IBeatmap beatmap)
        {
            if (frames == null || frames.Count == 0)
                return TouchScreenAnalysisResult.Unknown();
            if (beatmap?.HitObjects == null || beatmap.HitObjects.Count == 0)
                return TouchScreenAnalysisResult.Unknown();

            // Project hit objects to (StartTime, EndTime, StartPos, EndPos)
            // ordered by StartTime. We only need OsuHitObject for the
            // position info; anything else gets skipped.
            var hitObjects = beatmap.HitObjects
                                    .OfType<OsuHitObject>()
                                    .OrderBy(h => h.StartTime)
                                    .ToList();

            if (hitObjects.Count < 2)
                return TouchScreenAnalysisResult.Unknown();

            // Build intervals: gap between each object's end and the next
            // object's start. Filter on duration and frame count below.
            var perIntervalMetrics = new List<IntervalMetrics>(hitObjects.Count);

            int frameCursor = 0; // walking pointer into frames, monotonic — frames are sorted

            for (int i = 0; i < hitObjects.Count - 1; i++)
            {
                var current = hitObjects[i];
                var next = hitObjects[i + 1];

                double intervalStart = current.GetEndTime();
                double intervalEnd = next.StartTime;
                double duration = intervalEnd - intervalStart;

                if (duration < TouchScreenClassifierConfig.MinIntervalDurationMs ||
                    duration > TouchScreenClassifierConfig.MaxIntervalDurationMs)
                    continue;

                // Advance frameCursor to the first frame at or after intervalStart.
                // Don't re-scan from the beginning every interval — frames are
                // sorted, so amortised this is O(n) total across all intervals.
                while (frameCursor < frames.Count && frames[frameCursor].Time < intervalStart)
                    frameCursor++;

                // Collect frames inside the interval. Use a local index so we
                // don't move frameCursor past this interval — the next
                // interval might re-use the final frame or two.
                int scan = frameCursor;
                var intervalFrames = new List<OsuReplayFrame>();
                while (scan < frames.Count && frames[scan].Time <= intervalEnd)
                {
                    intervalFrames.Add(frames[scan]);
                    scan++;
                }

                if (intervalFrames.Count < TouchScreenClassifierConfig.MinFramesPerInterval)
                    continue;

                var metrics = analyseInterval(intervalFrames, current.StackedEndPosition, next.StackedPosition);
                perIntervalMetrics.Add(metrics);
            }

            if (perIntervalMetrics.Count < TouchScreenClassifierConfig.MinIntervalsForVerdict)
            {
                return TouchScreenAnalysisResult.Unknown(new Dictionary<string, double>
                {
                    [TouchScreenAnalysisResult.MetricNames.IntervalsAnalysed] = perIntervalMetrics.Count,
                });
            }

            // Aggregate via median per metric — robust against the noisy
            // tail (slider-leave intervals where the cursor is still moving
            // from the slider, sudden BPM changes, etc).
            double stationaryRatio = median(perIntervalMetrics, m => m.StationaryRatio);
            double movingRatio = median(perIntervalMetrics, m => m.MovingRatio);
            double jumpingRatio = median(perIntervalMetrics, m => m.JumpingRatio);
            double pathInflation = median(perIntervalMetrics, m => m.PathInflation);
            double maxStillness = perIntervalMetrics.Sum(m => m.MaxStillnessFraction) / perIntervalMetrics.Count;

            // Midpoint progress: only aggregate over intervals where the
            // metric is meaningful (euclidean distance ≥ 1 px so the
            // projection is defined). Stack intervals contribute the
            // neutral 0.5 default but we don't want those biasing the
            // median, so filter them out here. If literally every interval
            // is a stack (rare; bm has only stacked notes), fall back to
            // the neutral default.
            var validMidpointIntervals = perIntervalMetrics
                .Where(m => m.MidpointProgressValid)
                .ToList();
            double midpointProgress = validMidpointIntervals.Count > 0
                ? median(validMidpointIntervals, m => m.MidpointProgress)
                : 0.5;

            int totalFrames = perIntervalMetrics.Sum(m => m.FrameCount);

            // Composite scores (each in [0, 1]). Weights sum to 1.0 per
            // composite — see TouchScreenClassifierConfig for the
            // breakdown reasoning.
            double tapScore =
                stationaryRatio * TouchScreenClassifierConfig.TapWeightStationary
                + maxStillness * TouchScreenClassifierConfig.TapWeightMaxStillness
                + Math.Min(jumpingRatio * 4.0, 1.0) * TouchScreenClassifierConfig.TapWeightJumping
                + (1.0 - midpointProgress) * TouchScreenClassifierConfig.TapWeightLowMidpointProgress;
            // ^ jumping is rare in absolute terms (a few % is already strong
            //   signal); amplify by 4x before clamping to [0, 1] so it can
            //   contribute meaningfully to the composite.
            // The midpoint-progress signal carries no amplification — it's
            // already in [0, 1] from the projection clamp, and 0.0 means
            // "definitely tap" while 0.5 means "definitely drag".

            double pathInflationExcess = Math.Clamp(
                (pathInflation - 1.0) / (TouchScreenClassifierConfig.PathInflationCap - 1.0),
                0.0, 1.0);

            // For drag we scale midpoint-progress by 2× so the natural
            // drag midpoint of 0.5 hits the top of the contribution
            // range. Tap's natural midpoint is 0, so (1 - midpoint) hits
            // 1.0 at the right value without scaling.
            double dragScore =
                movingRatio * TouchScreenClassifierConfig.DragWeightMoving
                + pathInflationExcess * TouchScreenClassifierConfig.DragWeightPathInflation
                + (1.0 - stationaryRatio) * TouchScreenClassifierConfig.DragWeightNotStationary
                + Math.Min(midpointProgress * 2.0, 1.0) * TouchScreenClassifierConfig.DragWeightMidpointProgress;

            var metricsOut = new Dictionary<string, double>
            {
                [TouchScreenAnalysisResult.MetricNames.StationaryRatio] = stationaryRatio,
                [TouchScreenAnalysisResult.MetricNames.MovingRatio] = movingRatio,
                [TouchScreenAnalysisResult.MetricNames.JumpingRatio] = jumpingRatio,
                [TouchScreenAnalysisResult.MetricNames.PathInflation] = pathInflation,
                [TouchScreenAnalysisResult.MetricNames.MaxStillnessFraction] = maxStillness,
                [TouchScreenAnalysisResult.MetricNames.MidpointProgress] = midpointProgress,
                [TouchScreenAnalysisResult.MetricNames.IntervalsAnalysed] = perIntervalMetrics.Count,
                [TouchScreenAnalysisResult.MetricNames.TotalFramesAnalysed] = totalFrames,
                [TouchScreenAnalysisResult.MetricNames.TapScore] = tapScore,
                [TouchScreenAnalysisResult.MetricNames.DragScore] = dragScore,
            };

            // Verdict.
            TouchScreenPlayStyle style;
            float baseConfidence;
            if (tapScore >= TouchScreenClassifierConfig.VerdictAbsoluteFloor &&
                tapScore - dragScore >= TouchScreenClassifierConfig.VerdictMarginRequired)
            {
                style = TouchScreenPlayStyle.Tap;
                baseConfidence = (float)Math.Min(1.0, tapScore);
            }
            else if (dragScore >= TouchScreenClassifierConfig.VerdictAbsoluteFloor &&
                     dragScore - tapScore >= TouchScreenClassifierConfig.VerdictMarginRequired)
            {
                style = TouchScreenPlayStyle.Drag;
                baseConfidence = (float)Math.Min(1.0, dragScore);
            }
            else
            {
                // Neither side won outright. Could be (a) a genuinely mixed
                // play or (b) the heuristic can't tell. We return Mixed with
                // capped confidence so the pp pipeline treats it as Drag
                // (conservative).
                style = TouchScreenPlayStyle.Mixed;
                baseConfidence = (float)Math.Max(tapScore, dragScore);
            }

            // Penalise confidence on sparse interval counts. The verdict
            // doesn't change — but a Tap call on 6 intervals is shakier
            // than the same Tap call on 60.
            if (perIntervalMetrics.Count < TouchScreenClassifierConfig.LowConfidenceIntervalsThreshold)
            {
                double fraction = (double)perIntervalMetrics.Count
                                  / TouchScreenClassifierConfig.LowConfidenceIntervalsThreshold;
                baseConfidence *= (float)(0.5 + 0.5 * fraction);
            }

            return new TouchScreenAnalysisResult(style, baseConfidence, metricsOut);
        }

        // ─────────────────────────────────────────────────────────────────

        private readonly record struct IntervalMetrics(
            double StationaryRatio,
            double MovingRatio,
            double JumpingRatio,
            double PathInflation,
            double MaxStillnessFraction,
            double MidpointProgress,
            bool MidpointProgressValid,
            int FrameCount);

        private static IntervalMetrics analyseInterval(
            IReadOnlyList<OsuReplayFrame> intervalFrames,
            Vector2 prevHitEndPosition,
            Vector2 nextHitStartPosition)
        {
            int stationary = 0, moving = 0, jumping = 0;
            int validMotionSamples = 0;
            double pathLength = 0.0;

            int currentStill = 0, longestStill = 0;

            for (int i = 1; i < intervalFrames.Count; i++)
            {
                var prev = intervalFrames[i - 1];
                var curr = intervalFrames[i];

                double dt = curr.Time - prev.Time;
                if (dt <= 0)
                    continue; // duplicate or out-of-order frame — skip

                validMotionSamples++;

                double dx = curr.Position.X - prev.Position.X;
                double dy = curr.Position.Y - prev.Position.Y;
                double distance = Math.Sqrt(dx * dx + dy * dy);
                pathLength += distance;

                double velocity = distance / dt;

                if (velocity < TouchScreenClassifierConfig.StationaryVelocityThreshold)
                {
                    stationary++;
                    currentStill++;
                    if (currentStill > longestStill)
                        longestStill = currentStill;
                }
                else
                {
                    currentStill = 0;
                    if (velocity > TouchScreenClassifierConfig.JumpVelocityThreshold)
                        jumping++;
                    else
                        moving++;
                }
            }

            // Use the count of frames that actually contributed a velocity
            // sample (skips duplicates / out-of-order). Falls back to 1 to
            // avoid division by zero on the pathological case where every
            // pair was filtered out — that case can't pass MinFramesPerInterval
            // anyway, so this branch only matters for crash safety.
            int delta = Math.Max(1, validMotionSamples);
            double euclidean = (nextHitStartPosition - prevHitEndPosition).Length;

            // Guard against zero baselines: if the two hit objects are at
            // the same point, any cursor motion at all is "infinite"
            // inflation. Treat that case as inflation = 1.0 (neutral) —
            // it's a stack-repeat anyway and uninformative.
            double inflation = euclidean < 1.0 ? 1.0 : pathLength / euclidean;

            // Midpoint progress: where along the prev→next path is the
            // cursor at the temporal midpoint of the interval? Tap players
            // sit at progress ≈ 0 (held at prev) until the very end. Drag
            // players sit at progress ≈ 0.5 (mid-path). This signal is
            // independent of frame rate and absolute velocity, which the
            // velocity-bucket signals are NOT — at very high replay poll
            // rates the per-frame motion drops below the stationary
            // threshold even for genuine drag play, and only this
            // position-based signal preserves the discrimination.
            double midpointProgress = 0.5;
            bool midpointValid = false;
            if (intervalFrames.Count >= 2 && euclidean >= 1.0)
            {
                double midTime = 0.5 * (intervalFrames[0].Time + intervalFrames[^1].Time);
                OsuReplayFrame? closest = null;
                double closestGap = double.MaxValue;
                foreach (var f in intervalFrames)
                {
                    double gap = Math.Abs(f.Time - midTime);
                    if (gap < closestGap)
                    {
                        closestGap = gap;
                        closest = f;
                    }
                }
                if (closest != null)
                {
                    Vector2 fromStart = closest.Position - prevHitEndPosition;
                    Vector2 pathVec = nextHitStartPosition - prevHitEndPosition;
                    // Project fromStart onto pathVec, then normalise by
                    // path length squared to get a fraction in [0, 1] for
                    // points strictly on the path (clamped for off-path
                    // samples, which a drag with curvature can produce).
                    double dot = (double)fromStart.X * pathVec.X + (double)fromStart.Y * pathVec.Y;
                    double progress = dot / (euclidean * euclidean);
                    midpointProgress = Math.Clamp(progress, 0.0, 1.0);
                    midpointValid = true;
                }
            }

            return new IntervalMetrics(
                StationaryRatio: (double)stationary / delta,
                MovingRatio: (double)moving / delta,
                JumpingRatio: (double)jumping / delta,
                PathInflation: inflation,
                MaxStillnessFraction: (double)longestStill / delta,
                MidpointProgress: midpointProgress,
                MidpointProgressValid: midpointValid,
                FrameCount: intervalFrames.Count);
        }

        private static double median(IList<IntervalMetrics> items, Func<IntervalMetrics, double> selector)
        {
            if (items.Count == 0) return 0.0;
            var sorted = items.Select(selector).OrderBy(x => x).ToList();
            int mid = sorted.Count / 2;
            return sorted.Count % 2 == 1
                ? sorted[mid]
                : 0.5 * (sorted[mid - 1] + sorted[mid]);
        }
    }
}
