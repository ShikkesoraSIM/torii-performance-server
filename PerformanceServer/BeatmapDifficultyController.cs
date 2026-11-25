// Copyright (c) 2025 GooGuTeam
// Licensed under the MIT Licence. See the LICENCE file in the repository root for full licence text.

using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using osu.Game.Beatmaps;
using osu.Game.Online.API;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Mods;
using osu.Game.Scoring;
using PerformanceServer.Rulesets;

namespace PerformanceServer
{
    public class DifficultyRequestBody : INeedsRuleset
    {
        [JsonProperty("beatmap_id")] public int BeatmapId { get; set; }
        [JsonProperty("checksum")] public string? Checksum { get; set; }
        [JsonProperty("mods")] public List<APIMod> Mods { get; set; } = [];
        [JsonProperty("ruleset_id")] public int? RulesetId { get; set; }
        [JsonProperty("ruleset")] public string? RulesetName { get; set; }
        [JsonProperty("beatmap_file")] public string? BeatmapFile { get; set; }
    }

    [ApiController]
    [Route("difficulty")]
    public class BeatmapDifficultyController(IRulesetManager manager) : ControllerBase
    {
        [HttpPost]
        [Consumes("application/json")]
        [Produces("application/json")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public async Task<ActionResult<DifficultyAttributes>> CalculateDifficulty([FromBody] DifficultyRequestBody body)
        {
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

            Ruleset ruleset = manager.GetRuleset(body, workingBeatmap.BeatmapInfo.Ruleset.OnlineID);
            Mod[] mods = body.Mods.Select(m => m.ToMod(ruleset)).ToArray();
            DifficultyAttributes? difficultyAttributes =
                ruleset.CreateDifficultyCalculator(workingBeatmap).Calculate(mods);

            Dictionary<string, object>? attr =
                JsonConvert.DeserializeObject<Dictionary<string, object>>(
                    JsonConvert.SerializeObject(difficultyAttributes));
            if (attr != null)
                attr["ruleset"] = ruleset.ShortName;

            return attr != null ? Ok(attr) : BadRequest("Failed to calculate difficulty.");
        }
    }
}