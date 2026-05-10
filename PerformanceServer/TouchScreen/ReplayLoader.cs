// Copyright (c) 2025 GooGuTeam
// Licensed under the MIT Licence. See the LICENCE file in the repository root for full licence text.

using System.IO;
using osu.Game.Beatmaps;
using osu.Game.Rulesets;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;
using PerformanceServer.Rulesets;

namespace PerformanceServer.TouchScreen
{
    /// <summary>
    /// Thin adapter around <see cref="LegacyScoreDecoder"/> so the perf
    /// server can parse a raw <c>.osr</c> stream against a beatmap we
    /// already have in hand.
    /// </summary>
    /// <remarks>
    /// <see cref="LegacyScoreDecoder"/> is abstract — it needs callbacks to
    /// resolve the ruleset by ID and the beatmap by md5 hash, and the
    /// framework's own subclass (lazer's <c>LegacyScoreImporter</c>) goes
    /// through the realm database which we don't have here. Instead, we
    /// inject the ruleset manager already wired up for pp calculation and
    /// pass through the beatmap the caller already prepared. The decoder
    /// then does the rest, including the per-ruleset
    /// <c>IConvertibleReplayFrame.FromLegacy</c> conversion that turns raw
    /// <see cref="osu.Game.Replays.Legacy.LegacyReplayFrame"/>s into
    /// <see cref="osu.Game.Rulesets.Osu.Replays.OsuReplayFrame"/>s we can
    /// reason about.
    /// </remarks>
    public sealed class ReplayLoader
    {
        private readonly IRulesetManager rulesetManager;

        public ReplayLoader(IRulesetManager rulesetManager)
        {
            this.rulesetManager = rulesetManager;
        }

        /// <summary>
        /// Decode a <c>.osr</c> stream into a populated <see cref="Score"/>.
        /// The returned score's <c>Replay.Frames</c> list is already
        /// converted to the ruleset's frame type.
        /// </summary>
        /// <param name="osrStream">
        /// The raw .osr bytes as a seekable stream. Position is consumed.
        /// </param>
        /// <param name="workingBeatmap">
        /// The beatmap the replay was played on. The md5 stored inside the
        /// .osr is ignored — we trust the caller knows which beatmap they
        /// asked us to load against. (Mismatches surface elsewhere as
        /// nonsense classifier output, which is the right failure mode.)
        /// </param>
        public Score Parse(Stream osrStream, WorkingBeatmap workingBeatmap)
        {
            var decoder = new InjectedDecoder(rulesetManager, workingBeatmap);
            return decoder.Parse(osrStream);
        }

        private sealed class InjectedDecoder : LegacyScoreDecoder
        {
            private readonly IRulesetManager rulesetManager;
            private readonly WorkingBeatmap workingBeatmap;

            public InjectedDecoder(IRulesetManager rulesetManager, WorkingBeatmap workingBeatmap)
            {
                this.rulesetManager = rulesetManager;
                this.workingBeatmap = workingBeatmap;
            }

            protected override Ruleset? GetRuleset(int rulesetId)
            {
                try
                {
                    return rulesetManager.GetRuleset(rulesetId);
                }
                catch (System.ArgumentException)
                {
                    // The decoder treats null as "ruleset not registered" and
                    // raises a clean error of its own. Don't propagate the
                    // manager's ArgumentException up — it would surface as a
                    // 500 to the client instead of the decoder's tidy
                    // "unsupported ruleset" message.
                    return null;
                }
            }

            // We ignore the md5 the decoder hands us — the caller already
            // told us which beatmap to use. Returning the same WorkingBeatmap
            // regardless is fine because the decoder only uses it to apply
            // the score's mods and pull hit-object timing for frame-time
            // adjustment; both of those want OUR beatmap, not whatever the
            // .osr's hash happens to match in some hypothetical store.
            protected override WorkingBeatmap GetBeatmap(string md5Hash) => workingBeatmap;
        }
    }
}
