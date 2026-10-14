using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Read;
using Verse;

namespace RiMCP.Command
{
    internal static class TimeCommandService
    {
        private const float MinHours = 0.1f;
        private const float MaxHours = 24f;
        private const int MaxRequestIdLength = 100;
        private const double MaxWaitSeconds = 30;

        public static BridgeResponse SetGameSpeed(BridgeRequest request, RouteMatch route)
        {
            SetGameSpeedBody body = CommandUtil.ReadBody<SetGameSpeedBody>(request);
            TimeSpeed speed = ParseSpeed(body.Speed, true);
            TickManager ticks = RequireTickManager();

            TimeSpeed previous = ticks.CurTimeSpeed;
            // Pause through the game's toggle so the player's spacebar resumes the previous speed.
            if (speed == TimeSpeed.Paused)
            {
                ticks.Pause();
            }
            else
            {
                ticks.CurTimeSpeed = speed;
            }
            if (ticks.CurTimeSpeed != speed)
            {
                throw new CommandException(409, "RimWorld is not accepting time control right now.");
            }

            // Changing speed takes manual control, so it ends any running advance.
            AdvanceRun ended = AdvanceRunner.Interrupt("set_game_speed");
            Dictionary<string, object> data = Dto.Obj(
                Dto.Field("previousSpeed", previous.ToString()),
                Dto.Field("speed", ticks.CurTimeSpeed.ToString()));
            if (ended != null)
            {
                data["endedAdvance"] = SerializeRun(ended);
            }
            return CommandEnvelope.Ok(CommandUtil.ContextFor(null), previous != speed || ended != null, data);
        }

        public static BridgeResponse StartAdvance(BridgeRequest request, RouteMatch route)
        {
            AdvanceBody body = CommandUtil.ReadBody<AdvanceBody>(request);
            if (!body.Hours.HasValue)
            {
                throw new CommandException(400, "Missing required field 'hours'.");
            }
            float hours = body.Hours.Value;
            if (float.IsNaN(hours) || hours < MinHours || hours > MaxHours)
            {
                throw new CommandException(400, "Field 'hours' must be from " + MinHours + " to " + MaxHours + ".");
            }
            TimeSpeed speed = string.IsNullOrWhiteSpace(body.Speed) ? TimeSpeed.Superfast : ParseSpeed(body.Speed, false);
            string[] stopOn = body.StopOn == null ? AdvanceRun.DefaultStopOn : body.StopOn.Select(ParseCondition).Distinct().ToArray();
            if (body.RequestId != null && (body.RequestId.Trim().Length == 0 || body.RequestId.Length > MaxRequestIdLength))
            {
                throw new CommandException(400, "Field 'requestId' must be 1 to " + MaxRequestIdLength + " characters.");
            }
            RequireTickManager();

            bool created;
            AdvanceRun run = AdvanceRunner.Start(hours, speed, stopOn, body.RequestId, out created);
            Func<BridgeResponse> reply = () => CommandEnvelope.Ok(CommandUtil.ContextFor(null), created, SerializeRun(run));
            return WithWait(request, run, reply);
        }

        public static BridgeResponse GetAdvance(BridgeRequest request, RouteMatch route)
        {
            ReadRequest query = ReadRequest.FromUri(request.Uri);
            AdvanceRun run = AdvanceRunner.LatestInLoadedGame;
            string runIdText = query.Get("runId");
            if (!string.IsNullOrWhiteSpace(runIdText))
            {
                int runId;
                run = int.TryParse(runIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out runId) ? AdvanceRunner.RunById(runId) : null;
                if (run == null)
                {
                    return BridgeResponse.Error(404, "No advance run " + runIdText + " in the loaded game. The last " + AdvanceRunner.MaxHistory + " runs are kept.");
                }
            }
            AdvanceRun bound = run;
            return WithWait(request, run, () => AdvanceStatus(request, bound));
        }

        // A wait changes when the reply arrives, not what it describes: the reply is rebuilt for the
        // same run after the bridge waits for it off the main thread.
        private static BridgeResponse WithWait(BridgeRequest request, AdvanceRun run, Func<BridgeResponse> reply)
        {
            BridgeResponse response = reply();
            double seconds = WaitSeconds(request);
            if (seconds > 0 && run != null && run.Status == AdvanceRun.Running)
            {
                response.Deferred = new DeferredReply(run.Ended, seconds, reply);
            }
            return response;
        }

        private static BridgeResponse AdvanceStatus(BridgeRequest request, AdvanceRun run)
        {
            TickManager ticks = Current.Game == null ? null : Find.TickManager;
            return ReadEnvelope.Ok(ReadContext.From(request), Dto.Obj(
                Dto.Field("time", ticks == null ? null : Dto.Obj(
                    Dto.Field("tick", ticks.TicksGame),
                    Dto.Field("speed", ticks.CurTimeSpeed.ToString()),
                    Dto.Field("paused", ticks.Paused),
                    Dto.Field("forcedNormalSpeed", ticks.slower.ForcedNormalSpeed))),
                Dto.Field("run", run == null ? null : SerializeRun(run))));
        }

        private static double WaitSeconds(BridgeRequest request)
        {
            double seconds;
            string text = ReadRequest.FromUri(request.Uri).Get("waitSeconds");
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) || double.IsNaN(seconds))
            {
                return 0;
            }
            return Math.Max(0, Math.Min(MaxWaitSeconds, seconds));
        }

        public static object SerializeRun(AdvanceRun run)
        {
            bool running = run.Status == AdvanceRun.Running;
            int tick = running && Current.Game != null && ReferenceEquals(Current.Game, run.Game)
                ? Find.TickManager.TicksGame
                : run.EndTick;
            Dictionary<string, object> dto = Dto.Obj(
                Dto.Field("id", run.Id),
                Dto.Field("requestId", run.RequestId),
                Dto.Field("status", run.Status),
                Dto.Field("speed", run.Speed.ToString()),
                Dto.Field("hours", run.Hours),
                Dto.Field("hoursElapsed", Math.Round((double)(tick - run.StartTick) / GenDate.TicksPerHour, 2)),
                Dto.Field("startTick", run.StartTick),
                Dto.Field("targetTick", run.TargetTick),
                Dto.Field("tick", tick),
                Dto.Field("stopOn", AdvanceRun.Conditions.Where(run.StopOn.Contains).ToArray()),
                Dto.Field("endReason", running ? null : run.EndReason),
                Dto.Field("stoppedBy", run.StoppedBy == null ? null : SerializeEvent(run.StoppedBy)),
                Dto.Field("events", run.Events.Select(SerializeEvent).ToArray()));
            if (run.DroppedEvents > 0)
            {
                dto["droppedEvents"] = run.DroppedEvents;
            }
            if (run.Frames > 0)
            {
                // Main-thread cost of checking stop conditions each frame, which request timing misses.
                dto["observerMs"] = Dto.Obj(
                    Dto.Field("average", Math.Round(run.FrameMsTotal / run.Frames, 3)),
                    Dto.Field("max", Math.Round(run.FrameMsMax, 3)));
            }
            return dto;
        }

        private static object SerializeEvent(AdvanceEvent change)
        {
            return Dto.Obj(
                Dto.Field("kind", change.Kind),
                Dto.Field("tick", change.Tick),
                Dto.Field("pawn", change.PawnId == null ? null : Dto.Obj(
                    Dto.Field("id", change.PawnId),
                    Dto.Field("name", change.PawnName))),
                Dto.Field("detail", change.Detail));
        }

        private static TickManager RequireTickManager()
        {
            if (Current.Game == null || Current.ProgramState != ProgramState.Playing || Find.TickManager == null)
            {
                throw new CommandException(409, "No game is loaded.");
            }
            return Find.TickManager;
        }

        private static TimeSpeed ParseSpeed(string value, bool allowPaused)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new CommandException(400, "Missing required field 'speed'.");
            }
            string[] names = allowPaused
                ? new[] { "Paused", "Normal", "Fast", "Superfast", "Ultrafast" }
                : new[] { "Normal", "Fast", "Superfast", "Ultrafast" };
            string name = names.FirstOrDefault(candidate => string.Equals(candidate, value.Trim(), StringComparison.OrdinalIgnoreCase));
            if (name == null)
            {
                throw new CommandException(400, "Speed must be one of: " + string.Join(", ", names) + ".");
            }
            TimeSpeed speed = (TimeSpeed)Enum.Parse(typeof(TimeSpeed), name);
            // TimeControls only offers Ultrafast in development mode.
            if (speed == TimeSpeed.Ultrafast && !Prefs.DevMode)
            {
                throw new CommandException(409, "Ultrafast is only available in development mode.");
            }
            return speed;
        }

        private static string ParseCondition(string value)
        {
            string condition = AdvanceRun.Conditions.FirstOrDefault(candidate => string.Equals(candidate, value == null ? null : value.Trim(), StringComparison.OrdinalIgnoreCase));
            if (condition == null)
            {
                throw new CommandException(400, "Unknown stopOn condition '" + value + "'. Use: " + string.Join(", ", AdvanceRun.Conditions) + ".");
            }
            return condition;
        }

        [DataContract]
        private sealed class SetGameSpeedBody
        {
            [DataMember(Name = "speed")]
            public string Speed { get; set; }
        }

        [DataContract]
        private sealed class AdvanceBody
        {
            [DataMember(Name = "hours")]
            public float? Hours { get; set; }

            [DataMember(Name = "speed")]
            public string Speed { get; set; }

            [DataMember(Name = "stopOn")]
            public string[] StopOn { get; set; }

            [DataMember(Name = "requestId")]
            public string RequestId { get; set; }
        }
    }
}
