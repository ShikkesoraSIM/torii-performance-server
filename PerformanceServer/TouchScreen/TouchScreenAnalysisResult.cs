// Copyright (c) 2025 GooGuTeam
// Licensed under the MIT Licence. See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;

namespace PerformanceServer.TouchScreen
{
    /// <summary>
    /// What <see cref="TouchScreenClassifier"/> returns: the verdict, how
    /// strongly it believes the verdict, and the raw aggregate metrics it
    /// based the decision on.
    /// </summary>
    /// <remarks>
    /// The metrics dictionary is exposed deliberately. Threshold tuning will
    /// be data-driven — running the classifier in shadow mode over a corpus
    /// of human-labelled replays and adjusting <see cref="TouchScreenClassifierConfig"/>
    /// until the rate of disagreement is acceptable. Persisting the metrics
    /// in the database (or at least logging them) lets us re-run that tuning
    /// without re-parsing every replay.
    /// </remarks>
    public readonly struct TouchScreenAnalysisResult
    {
        /// <summary>The verdict.</summary>
        public TouchScreenPlayStyle Style { get; }

        /// <summary>
        /// Verdict confidence in <c>[0, 1]</c>.
        /// <list type="bullet">
        ///   <item><description>≥ 0.80: take the verdict at face value.</description></item>
        ///   <item><description>0.60–0.80: treat as a hint, log for review.</description></item>
        ///   <item><description>&lt; 0.60: treat as <see cref="TouchScreenPlayStyle.Unknown"/> — the heuristic doesn't want to commit.</description></item>
        /// </list>
        /// Independently, <see cref="TouchScreenPlayStyle.Unknown"/> is always emitted
        /// at confidence 0 to mean "I literally couldn't compute".
        /// </summary>
        public float Confidence { get; }

        /// <summary>
        /// Free-form metric bag the classifier filled in along the way.
        /// Keys come from <see cref="MetricNames"/>. Stable for at-rest
        /// serialisation.
        /// </summary>
        public IReadOnlyDictionary<string, double> Metrics { get; }

        public TouchScreenAnalysisResult(
            TouchScreenPlayStyle style,
            float confidence,
            IReadOnlyDictionary<string, double> metrics)
        {
            Style = style;
            Confidence = confidence;
            Metrics = metrics;
        }

        public static TouchScreenAnalysisResult Unknown(IReadOnlyDictionary<string, double>? metrics = null) =>
            new(TouchScreenPlayStyle.Unknown, 0f, metrics ?? new Dictionary<string, double>());

        /// <summary>Stable keys for <see cref="Metrics"/>.</summary>
        public static class MetricNames
        {
            /// <summary>
            /// Median fraction (across analysed inter-hit intervals) of frames
            /// whose inter-frame cursor distance is below the "stationary"
            /// threshold. Tap players park the cursor between hits → this
            /// runs high (0.4–0.8). Drag players never stop → this runs near
            /// 0.
            /// </summary>
            public const string StationaryRatio = "stationary_ratio";

            /// <summary>
            /// Median fraction of frames with cursor velocity in the
            /// continuous-movement band (above stationary, below jump). Drag
            /// players score 0.6+ here; tap players score near 0 because
            /// they never sustain motion.
            /// </summary>
            public const string MovingRatio = "moving_ratio";

            /// <summary>
            /// Median fraction of frames with cursor velocity above the jump
            /// threshold (single-frame teleport between hits). Tap players
            /// produce a small but nonzero number; drag players produce
            /// essentially zero (they never go that fast).
            /// </summary>
            public const string JumpingRatio = "jumping_ratio";

            /// <summary>
            /// Median ratio of (cursor path length traversed during interval)
            /// to (Euclidean distance between the two hits bordering the
            /// interval). Tap players approach 0 (cursor doesn't move) or
            /// approach 1 (single-frame straight jump); drag players sit
            /// above 1 because they curve. A median &gt; 1.3 is a strong
            /// drag indicator.
            /// </summary>
            public const string PathInflation = "path_inflation";

            /// <summary>
            /// Mean (across intervals) of the longest contiguous run of
            /// stationary frames, expressed as a fraction of the interval's
            /// frame count. Catches a class of stylus play where the cursor
            /// briefly drifts (jitter) but is mostly held — naive
            /// <see cref="StationaryRatio"/> alone can underestimate these.
            /// </summary>
            public const string MaxStillnessFraction = "max_stillness_fraction";

            /// <summary>
            /// Median (across intervals where the metric is meaningful) of
            /// the cursor's progress along the prev-hit → next-hit path at
            /// the temporal midpoint of the interval, in [0, 1]. Tap
            /// players sit near 0 (cursor held at previous hit until the
            /// last frame); drag players sit near 0.5 (cursor mid-path).
            /// Position-based, so robust to frame rate where the per-frame
            /// velocity bucketing degrades.
            /// </summary>
            public const string MidpointProgress = "midpoint_progress";

            /// <summary>
            /// Number of inter-hit intervals that survived filtering and fed
            /// the medians above. Below 10 → low confidence; below 5 →
            /// Unknown verdict.
            /// </summary>
            public const string IntervalsAnalysed = "intervals_analysed";

            /// <summary>
            /// Total number of replay frames seen across all analysed intervals.
            /// Diagnostic only — flags suspiciously sparse replays.
            /// </summary>
            public const string TotalFramesAnalysed = "total_frames_analysed";

            /// <summary>
            /// Internal score in [0, 1] for "this looks like a tap player".
            /// Composed from stationary + max-stillness + jumping signals.
            /// Persisted so threshold tuning can be done offline.
            /// </summary>
            public const string TapScore = "tap_score";

            /// <summary>
            /// Internal score in [0, 1] for "this looks like a drag player".
            /// Composed from moving ratio + path inflation + (1 - stationary).
            /// </summary>
            public const string DragScore = "drag_score";
        }
    }
}
