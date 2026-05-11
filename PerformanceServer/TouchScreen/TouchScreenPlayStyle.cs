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
    /// pp penalty under the assumption that touchscreen play is easier on
    /// aim — specifically because multiple fingers can be placed at multiple
    /// hit positions simultaneously, making jumps trivial. That assumption
    /// is correct for most TD play but misses one case:
    ///
    /// <list type="bullet">
    ///   <item>
    ///     <description>
    ///     <b>Tap</b> (cursor teleports between hits in the replay): could
    ///     be single-finger tap, multi-finger tap, or anything in between.
    ///     The replay can't tell them apart — the cursor reflects whichever
    ///     finger touched last, and a multi-finger player's replay looks
    ///     identical to a single-finger player who taps each hit. Since we
    ///     can't rule out multi-finger aim, the penalty assumption holds
    ///     and TD remains in place.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///     <b>Drag</b> (cursor moves continuously through hits in the
    ///     replay): this is physical evidence of single-finger aim. The
    ///     primary touch must have stayed in contact the entire interval;
    ///     a multi-finger player cannot produce a continuously-moving
    ///     cursor because the cursor only follows ONE touch at a time
    ///     (osu! filters secondary-touch positions from the cursor trace
    ///     — additional fingers register button events but don't move the
    ///     cursor). So a continuous cursor proves single-finger aim, and
    ///     the multi-finger-easier-aim premise of the TD penalty doesn't
    ///     apply. This is the FairTouchScreen outcome.
    ///     </description>
    ///   </item>
    /// </list>
    ///
    /// The classifier emits one of <see cref="Tap"/> / <see cref="Drag"/> /
    /// <see cref="Mixed"/> / <see cref="Unknown"/>. The downstream pp
    /// pipeline (<c>PerformanceController.cs</c>) strips the TD mod from
    /// the calculator's input iff the verdict is <see cref="Drag"/>.
    /// Anything else keeps the penalty.
    /// </remarks>
    public enum TouchScreenPlayStyle
    {
        /// <summary>
        /// Classifier didn't reach a verdict — replay was too short, the
        /// beatmap had too few non-slider intervals to look at, or the
        /// metrics straddled the decision boundary in a way the heuristic
        /// is unwilling to commit on. Callers should treat this as the
        /// conservative TD-default (full pp penalty applied) to avoid
        /// false-positive Drag classifications giving free pp.
        /// </summary>
        Unknown = 0,

        /// <summary>
        /// Cursor teleports between hits — could be multi-finger aim or
        /// single-finger tap. The replay can't tell them apart, so we
        /// conservatively keep the TD pp penalty applied. (Multi-finger
        /// aim is the case the penalty was designed to address.)
        /// </summary>
        Tap = 1,

        /// <summary>
        /// Cursor moves continuously through hits — physical proof of
        /// single-finger aim, since secondary touches don't move the
        /// cursor in osu!. This is the FairTouchScreen verdict — pp
        /// recalc strips the TD mod from the calculator's input.
        /// </summary>
        Drag = 2,

        /// <summary>
        /// Replay shows characteristics of both styles to roughly equal
        /// degree, OR the composite landed on Drag but one of the hard
        /// gates failed. Treated as Tap downstream — TD penalty applies.
        /// The conservative shelf.
        /// </summary>
        Mixed = 3,
    }
}
