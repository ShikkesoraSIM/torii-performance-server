// Copyright (c) 2025 GooGuTeam
// Licensed under the MIT Licence. See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Replays;
using PerformanceServer.Rulesets;
using PerformanceServer.TouchScreen;

namespace PerformanceServer
{
    /// <summary>
    /// Body for <see cref="TouchScreenController.Classify"/>.
    /// </summary>
    public class TouchScreenClassifyRequest
    {
        /// <summary>
        /// Raw bytes of the <c>.osr</c> replay file, base64-encoded. The
        /// replay must be from the osu! ruleset (ruleset_id = 0) — other
        /// rulesets don't carry the TD mod and will be rejected.
        /// </summary>
        [JsonProperty("replay_file")]
        public string ReplayFile { get; set; } = string.Empty;

        /// <summary>
        /// Full <c>.osu</c> beatmap text. The classifier needs hit-object
        /// positions and timings to know which inter-hit intervals to look
        /// at. Pass the same beatmap version the replay was recorded against
        /// (the md5 inside the .osr is informational only — we trust the
        /// caller's choice).
        /// </summary>
        [JsonProperty("beatmap_file")]
        public string BeatmapFile { get; set; } = string.Empty;

        /// <summary>
        /// Optional beatmap ID for logging. Not used in classification.
        /// </summary>
        [JsonProperty("beatmap_id")]
        public int? BeatmapId { get; set; }

        /// <summary>
        /// Optional score ID for logging. Not used in classification.
        /// </summary>
        [JsonProperty("score_id")]
        public long? ScoreId { get; set; }
    }

    /// <summary>
    /// Response body for <see cref="TouchScreenController.Classify"/>.
    /// </summary>
    public class TouchScreenClassifyResponse
    {
        [JsonProperty("style")]
        public string Style { get; set; } = "unknown";

        [JsonProperty("confidence")]
        public float Confidence { get; set; }

        [JsonProperty("metrics")]
        public IReadOnlyDictionary<string, double> Metrics { get; set; } = new Dictionary<string, double>();
    }

    /// <summary>
    /// Server-side endpoint that decides whether a TD-tagged osu! replay was
    /// played by tapping (FairTouchScreen — no pp penalty) or by drag-tap
    /// cheese (regular TouchScreen — pp penalty applies).
    /// </summary>
    /// <remarks>
    /// Lives only on the performance server. The client never sees this
    /// code — that's deliberate, because the heuristic's thresholds (in
    /// <see cref="TouchScreenClassifierConfig"/>) double as the anti-cheat
    /// surface. Shipping them in the client release would let a determined
    /// drag-tapper craft input that just barely clears the Tap bar.
    /// </remarks>
    [ApiController]
    [Route("touchscreen")]
    public class TouchScreenController : ControllerBase
    {
        private readonly IRulesetManager rulesetManager;

        public TouchScreenController(IRulesetManager rulesetManager)
        {
            this.rulesetManager = rulesetManager;
        }

        [HttpPost("classify")]
        [Consumes("application/json")]
        [Produces("application/json")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public ActionResult<TouchScreenClassifyResponse> Classify(
            [FromBody] TouchScreenClassifyRequest body)
        {
            if (string.IsNullOrWhiteSpace(body.ReplayFile))
                return BadRequest("replay_file is required (base64-encoded .osr bytes).");
            if (string.IsNullOrWhiteSpace(body.BeatmapFile))
                return BadRequest("beatmap_file is required (raw .osu text).");

            byte[] replayBytes;
            try
            {
                replayBytes = Convert.FromBase64String(body.ReplayFile);
            }
            catch (FormatException)
            {
                return BadRequest("replay_file is not valid base64.");
            }

            // Parse beatmap. We reuse ProcessorWorkingBeatmap so the legacy
            // decoder can apply mods and resolve hit object timing the same
            // way the pp endpoint does.
            ProcessorWorkingBeatmap workingBeatmap;
            try
            {
                workingBeatmap = new ProcessorWorkingBeatmap(body.BeatmapFile);
            }
            catch (Exception e)
            {
                return BadRequest($"Failed to parse beatmap_file: {e.Message}");
            }

            // Parse replay. The decoder converts frames per-ruleset; for
            // osu! that means we get OsuReplayFrames back.
            osu.Game.Scoring.Score score;
            try
            {
                using var replayStream = new MemoryStream(replayBytes);
                score = new ReplayLoader(rulesetManager).Parse(replayStream, workingBeatmap);
            }
            catch (Exception e)
            {
                return BadRequest($"Failed to parse replay_file: {e.Message}");
            }

            // Sanity check the ruleset — TD only exists in osu!standard.
            // For other rulesets we have nothing meaningful to say.
            if (score.ScoreInfo.Ruleset.ShortName != new OsuRuleset().RulesetInfo.ShortName)
            {
                return BadRequest(
                    $"Classifier only supports osu! ruleset replays (got '{score.ScoreInfo.Ruleset.ShortName}').");
            }

            var osuFrames = score.Replay.Frames.OfType<OsuReplayFrame>().ToList();
            if (osuFrames.Count == 0)
            {
                // Could happen for an empty replay (auto-fail at 0%). Return
                // Unknown rather than 400 — that way the caller persists the
                // verdict as "couldn't tell" and the score keeps the default
                // TD treatment, same as any other unparseable case.
                return Ok(new TouchScreenClassifyResponse { Style = "unknown", Confidence = 0f });
            }

            IBeatmap playable = workingBeatmap.GetPlayableBeatmap(
                score.ScoreInfo.Ruleset, score.ScoreInfo.Mods);

            TouchScreenAnalysisResult result = TouchScreenClassifier.Classify(osuFrames, playable);

            return Ok(new TouchScreenClassifyResponse
            {
                Style = result.Style switch
                {
                    TouchScreenPlayStyle.Tap => "tap",
                    TouchScreenPlayStyle.Drag => "drag",
                    TouchScreenPlayStyle.Mixed => "mixed",
                    _ => "unknown",
                },
                Confidence = result.Confidence,
                Metrics = result.Metrics,
            });
        }
    }
}
