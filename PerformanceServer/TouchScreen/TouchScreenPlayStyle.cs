// Copyright (c) 2025 GooGuTeam
// Licensed under the MIT Licence. See the LICENCE file in the repository root for full licence text.

namespace PerformanceServer.TouchScreen
{
    /// <summary>
    /// How a TD-mod-tagged play was actually performed, based on replay
    /// analysis.
    /// </summary>
    /// <remarks>
    /// The osu! client auto-applies the <c>TD</c> mod (TouchDevice) whenever
    /// the input device is recognised as a touchscreen. The mod carries a flat
    /// pp penalty under the assumption that touchscreen play is universally
    /// easier on aim. That assumption is wrong in two opposite ways:
    ///
    /// <list type="bullet">
    ///   <item>
    ///     <description>
    ///     <b>Fair touch</b> (this enum's <see cref="Tap"/>): the player taps
    ///     each hit object discretely with a finger or stylus, lifting between
    ///     hits. The cursor is either held at the previous hit position (no
    ///     contact = no movement on most platforms) or jumps to the next hit
    ///     in a single frame at contact time. Aim difficulty is comparable to
    ///     mouse/tablet — the penalty is unjustified and should be removed
    ///     (this is the "FairTouchScreen" outcome).
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///     <b>Drag-tap cheese</b> (this enum's <see cref="Drag"/>): the player
    ///     uses one continuous contact to drag the cursor across the screen
    ///     while a second contact handles timing taps. Aim difficulty
    ///     effectively collapses to "stay on the curve" — much easier than
    ///     mouse/tablet. The TD penalty is justified, possibly even too small.
    ///     </description>
    ///   </item>
    /// </list>
    ///
    /// Distinguishing them is the whole point of this classifier. The output
    /// drives whether the TD mod is honoured (Drag) or silently stripped
    /// before pp calculation (Tap).
    /// </remarks>
    public enum TouchScreenPlayStyle
    {
        /// <summary>
        /// Classifier didn't reach a verdict — replay was too short, the
        /// beatmap had too few non-slider intervals to look at, or the
        /// metrics straddled the decision boundary in a way the heuristic
        /// is unwilling to commit on. Callers should treat this as the
        /// conservative TD-default (full pp penalty applied) to avoid
        /// false-positive Tap classifications giving free pp.
        /// </summary>
        Unknown = 0,

        /// <summary>
        /// Discrete-tap play — cursor mostly stationary between hits, with
        /// one-frame jumps at hit times. The TD pp penalty should be lifted
        /// for this score.
        /// </summary>
        Tap = 1,

        /// <summary>
        /// Continuous-drag play — cursor in sustained motion between hits.
        /// The TD pp penalty should be applied (current default behaviour).
        /// </summary>
        Drag = 2,

        /// <summary>
        /// Replay shows characteristics of both styles to roughly equal
        /// degree. Either the player legitimately switched techniques mid-
        /// play (uncommon) or the heuristic's signals are noisy on this
        /// beatmap. Treated as Drag (conservative) by the pp pipeline.
        /// </summary>
        Mixed = 3,
    }
}
