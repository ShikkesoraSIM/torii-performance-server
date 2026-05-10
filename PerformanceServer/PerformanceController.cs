// Copyright (c) 2025 GooGuTeam
// Licensed under the MIT Licence. See the LICENCE file in the repository root for full licence text.

using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using osu.Game.Beatmaps;
using osu.Game.Database;
using osu.Game.Online.API;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Scoring;
using osu.Game.Rulesets.Scoring.Legacy;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;
using PerformanceServer.Rulesets;

namespace PerformanceServer
{
    public class PerformanceRequestBody : INeedsRuleset
    {
        [JsonProperty("beatmap_id")] public int BeatmapId { get; set; }
        [JsonProperty("checksum")] public string? Checksum { get; set; }
        [JsonProperty("mods")] public List<APIMod> Mods { get; set; } = [];
        [JsonProperty("is_legacy")] public bool IsLegacy { get; set; }
        [JsonProperty("accuracy")] public float Accuracy { get; set; }
        [JsonProperty("ruleset_id")] public int? RulesetId { get; set; }
        [JsonProperty("ruleset")] public string? RulesetName { get; set; }
        [JsonProperty("combo")] public int Combo { get; set; }
        [JsonProperty("statistics")] public Dictionary<HitResult, int> Statistics { get; set; } = new();
        [JsonProperty("beatmap_file")] public string? BeatmapFile { get; set; }

        /// <summary>
        /// Optional touchscreen play-style classification, sourced from the
        /// <c>/touchscreen/classify</c> endpoint and persisted in the
        /// <c>scores.td_play_style</c> column by g0v0-server.
        /// </summary>
        /// <remarks>
        /// When this is <c>"tap"</c> AND the score carries a TD mod, the
        /// controller silently strips the TD mod before invoking the pp
        /// calculator. The effect is that genuine tap players get the same
        /// pp they'd get on mouse/tablet — the FairTouchScreen outcome —
        /// without needing a separate mod or a dedicated calculator
        /// pathway. Any other value (drag / mixed / unknown / null) leaves
        /// the mod list untouched and the TD penalty applies as before.
        ///
        /// The bypass is implemented here rather than inside the ruleset's
        /// pp calculator so the decision stays server-side and out of the
        /// client DLL.
        /// </remarks>
        [JsonProperty("td_play_style")] public string? TdPlayStyle { get; set; }
    }

    [ApiController]
    [Route("performance")]
    public class PerformanceController(IRulesetManager manager) : ControllerBase
    {
        [HttpPost]
        [Consumes("application/json")]
        [Produces("application/json")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public async Task<ActionResult<PerformanceAttributes>> CalculatePerformance(
            [FromBody] PerformanceRequestBody body)
        {
            Ruleset ruleset;
            try
            {
                ruleset = manager.GetRuleset(body);
            }
            catch (ArgumentException e)
            {
                return BadRequest(e.Message);
            }

            List<Mod> mods = body.Mods.Select(m => m.ToMod(ruleset)).ToList();
            if (body.IsLegacy && !mods.Any(m => m is ModClassic))
            {
                Mod? classicMod = ruleset.CreateMod<ModClassic>();
                if (classicMod != null)
                    mods.Add(classicMod);
            }

            // FairTouchScreen bypass: a TD-tagged score whose replay was
            // classified as discrete-tap play gets the TD penalty removed.
            // The mechanism is intentionally surgical — we strip the mod
            // from the calculator's input rather than touching the
            // ruleset's pp formulae, so the decision stays server-side and
            // requires zero client changes.
            //
            // The classifier itself runs separately (POST /touchscreen/
            // classify) and the verdict is persisted by g0v0-server in
            // scores.td_play_style. The pp recalc passes that column
            // through to us here as the td_play_style field.
            //
            // Conservative on every other value: drag / mixed / unknown /
            // null all keep the TD mod and the existing penalty.
            if (string.Equals(body.TdPlayStyle, "tap", StringComparison.OrdinalIgnoreCase))
                mods.RemoveAll(m => m is ModTouchDevice);

            ScoreInfo scoreInfo = new()
            {
                IsLegacyScore = body.IsLegacy,
                BeatmapInfo = new BeatmapInfo { OnlineID = body.BeatmapId },
                Statistics = body.Statistics,
                Mods = mods.ToArray(),
                Accuracy = body.Accuracy,
                MaxCombo = body.Combo,
                Ruleset = ruleset.RulesetInfo,
            };
            ProcessorWorkingBeatmap workingBeatmap;
            if (body.BeatmapFile != null)
            {
                workingBeatmap = new ProcessorWorkingBeatmap(body.BeatmapFile);
            }
            else
            {
                try
                {
                    Beatmap beatmap =
                        await ProcessorWorkingBeatmap.ReadById(body.BeatmapId, body.Checksum ?? "");
                    workingBeatmap = new ProcessorWorkingBeatmap(beatmap);
                }
                catch (InvalidOperationException)
                {
                    return StatusCode(503, "Failed to fetch beatmap from online.");
                }
            }

            IBeatmap? playableBeatmap = workingBeatmap.GetPlayableBeatmap(ruleset.RulesetInfo, scoreInfo.Mods);
            scoreInfo.BeatmapInfo = playableBeatmap.BeatmapInfo;
            LegacyScoreDecoder.PopulateMaximumStatistics(scoreInfo, workingBeatmap);
            if (scoreInfo.IsLegacyScore)
            {
                StandardisedScoreMigrationTools.UpdateFromLegacy(
                    scoreInfo,
                    ruleset,
                    LegacyBeatmapConversionDifficultyInfo.FromBeatmap(playableBeatmap),
                    ((ILegacyRuleset)ruleset).CreateLegacyScoreSimulator().Simulate(workingBeatmap, playableBeatmap));
            }

            DifficultyAttributes? difficultyAttributes =
                ruleset.CreateDifficultyCalculator(workingBeatmap).Calculate(scoreInfo.Mods);
            PerformanceCalculator? performanceCalculator = ruleset.CreatePerformanceCalculator();
            PerformanceAttributes? performanceAttributes =
                performanceCalculator?.Calculate(scoreInfo, difficultyAttributes);
            return performanceAttributes == null
                ? BadRequest("Failed to calculate performance attributes.")
                : Ok(performanceAttributes);
        }
    }
}