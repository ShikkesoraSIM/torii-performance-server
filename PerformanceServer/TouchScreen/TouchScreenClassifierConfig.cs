// Copyright (c) 2025 GooGuTeam
// Licensed under the MIT Licence. See the LICENCE file in the repository root for full licence text.

namespace PerformanceServer.TouchScreen
{
    /// <summary>
    /// Every magic number the classifier consults, in one place, named.
    /// </summary>
    /// <remarks>
    /// All thresholds are documented with the physical / behavioural reasoning
    /// for the chosen value, not just the number. When tuning against a corpus
    /// of labelled replays, edit one of these and explain the new evidence in
    /// the comment — don't just bump the value silently. The classifier is
    /// only as honest as this file.
    ///
    /// Units throughout:
    /// <list type="bullet">
    ///   <item><description>Distances: osu! playfield pixels (the field is 512 × 384).</description></item>
    ///   <item><description>Times: milliseconds.</description></item>
    ///   <item><description>Velocities: px/ms.</description></item>
    /// </list>
    /// </remarks>
    public static class TouchScreenClassifierConfig
    {
        // ───── velocity bucketing ─────

        /// <summary>
        /// Below this velocity, a frame counts as "stationary" — the cursor
        /// effectively held in place between hits. 0.05 px/ms is 50 px/s,
        /// which covers tiny float-precision drift and stylus jitter without
        /// admitting actual movement.
        /// </summary>
        public const double StationaryVelocityThreshold = 0.05;

        /// <summary>
        /// Above this velocity, a frame counts as a "jump" — a single-frame
        /// teleport. 3.0 px/ms is 3000 px/s; the playfield is 512 wide, so
        /// crossing it in &lt;170 ms qualifies. Sustainable hand motion sits
        /// well under 1 px/ms; only an instantaneous input event (lifting a
        /// stylus and replanting it elsewhere within one input poll) gets
        /// counted here.
        /// </summary>
        public const double JumpVelocityThreshold = 3.0;

        // ───── interval filtering ─────

        /// <summary>
        /// Inter-hit intervals shorter than this are ignored. At 32-th notes
        /// of 200 BPM (≈ 37 ms) the cursor literally hasn't had time to do
        /// anything informative — a few frames of replay can't tell tap from
        /// drag.
        /// </summary>
        public const double MinIntervalDurationMs = 50.0;

        /// <summary>
        /// Inter-hit intervals longer than this are ignored. They're almost
        /// always either (a) the pre-first-note delay, (b) a break period,
        /// or (c) the post-last-note tail. None of these tell us anything
        /// about technique.
        /// </summary>
        public const double MaxIntervalDurationMs = 4000.0;

        /// <summary>
        /// Intervals must contain at least this many replay frames to be
        /// analysed. Below ~5 frames, ratio metrics quantise too hard
        /// (every frame is 20% of the bucket) and the signal is unreliable.
        /// </summary>
        public const int MinFramesPerInterval = 5;

        // ───── verdict gating ─────

        /// <summary>
        /// Minimum analysed intervals to emit anything other than
        /// <see cref="TouchScreenPlayStyle.Unknown"/>. Below 5 the signal is
        /// dominated by per-interval noise — even a confident-looking median
        /// is statistically meaningless.
        /// </summary>
        public const int MinIntervalsForVerdict = 5;

        /// <summary>
        /// Below this many intervals the verdict, even if computed, is
        /// labelled at reduced confidence. 20+ gets full confidence.
        /// </summary>
        public const int LowConfidenceIntervalsThreshold = 20;

        /// <summary>
        /// Either Tap or Drag must score at least this much (on its
        /// composite 0..1 scale) to win the verdict outright. Below this,
        /// fall back to <see cref="TouchScreenPlayStyle.Mixed"/>.
        /// </summary>
        public const double VerdictAbsoluteFloor = 0.55;

        /// <summary>
        /// And it must beat the opposing score by at least this margin —
        /// avoids classifying borderline replays where both scores are
        /// roughly equal (e.g. 0.62 vs 0.58).
        /// </summary>
        public const double VerdictMarginRequired = 0.20;

        // ───── composite weights ─────
        //
        // Each composite's weights sum to 1.0. If you tune one, tune the
        // others. The breakdown gives the midpoint-progress signal the
        // plurality (≈50%) because it's the only position-based input
        // and therefore the only one that survives all frame-rate and
        // spacing edge cases. The velocity-bucket signals are the
        // confirmation — they're cheap, fast, and correct on the common
        // case where per-frame motion is well above the stationary
        // floor.
        //
        // Physical intuition for the dominant weight: at the temporal
        // midpoint of an inter-hit interval, a tap player's cursor is
        // physically at the previous hit position (it's been held since
        // contact ended). A drag player's cursor is physically half-way
        // along the path to the next hit. Neither can fake the other —
        // tap can't be at mid-path because lifting freezes the cursor,
        // and drag can't be at the start because the finger has already
        // moved. The signal is robust to frame poll rate, hit spacing,
        // BPM, and replay file format.

        /// <summary>
        /// Weight of the stationary-frame ratio. Confirmation signal —
        /// "yes, the cursor really was held all the way".
        /// </summary>
        public const double TapWeightStationary = 0.20;

        /// <summary>
        /// Weight of the max-stillness fraction. Catches plays where the
        /// cursor jitters slightly while held but the longest contiguous
        /// hold still dominates.
        /// </summary>
        public const double TapWeightMaxStillness = 0.10;

        /// <summary>
        /// Weight of the jumping-frame ratio. Tap plays produce at
        /// least a few one-frame jumps as the stylus replants.
        /// </summary>
        public const double TapWeightJumping = 0.10;

        /// <summary>
        /// Weight of <c>(1 − midpoint_progress)</c>. THE primary tap
        /// signal — cursor sitting at the previous hit position at
        /// midpoint is the physically-unfakeable tap fingerprint.
        /// See the composite-weights header comment for why this gets
        /// the plurality.
        /// </summary>
        public const double TapWeightLowMidpointProgress = 0.60;

        /// <summary>
        /// Weight of the moving-frame ratio. Confirmation signal — "yes,
        /// the cursor really was in motion frame after frame".
        /// </summary>
        public const double DragWeightMoving = 0.20;

        /// <summary>
        /// Weight of the path-inflation excess (max 0.5 above 1.0). Drag
        /// plays curve through the playfield; a high inflation is a
        /// drag-only signature.
        /// </summary>
        public const double DragWeightPathInflation = 0.10;

        /// <summary>
        /// Weight of (1 − stationary ratio). Lightweight confirmation —
        /// a true drag never holds still even briefly.
        /// </summary>
        public const double DragWeightNotStationary = 0.10;

        /// <summary>
        /// Weight of <c>midpoint_progress</c>. THE primary drag signal,
        /// mirror of <see cref="TapWeightLowMidpointProgress"/>. See the
        /// composite-weights header comment.
        /// </summary>
        public const double DragWeightMidpointProgress = 0.60;

        /// <summary>
        /// Path inflation cap used by the drag composite. A 1.0 → 1.5
        /// ratio gets full weight; anything above 1.5 is the same as 1.5
        /// (no extra credit for absurdly curved paths, which are likely
        /// noise).
        /// </summary>
        public const double PathInflationCap = 1.5;
    }
}
