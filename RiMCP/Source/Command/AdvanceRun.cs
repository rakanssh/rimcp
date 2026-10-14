using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Read;
using Verse;
using Verse.AI;

namespace RiMCP.Command
{
    internal sealed class ColonistState
    {
        public string Id;
        public string Name;
        public bool Dead;
        public bool Downed;
        public bool Bleeding;
        public int Injuries;
        public string MentalState;
    }

    internal sealed class LetterState
    {
        public int Id;
        public string Label;
        public string DefName;
        public bool Threat;
    }

    internal sealed class AdvanceObservation
    {
        public object Game;
        public bool Playing;
        public int Tick;
        public TimeSpeed Speed;
        // Label of a visible active threat to the colony, or null.
        public string Threat;
        public readonly Dictionary<string, ColonistState> Colonists = new Dictionary<string, ColonistState>();
        public readonly Dictionary<int, LetterState> Letters = new Dictionary<int, LetterState>();
    }

    internal sealed class AdvanceEvent
    {
        public string Kind;
        public int Tick;
        public string PawnId;
        public string PawnName;
        public string Detail;
    }

    // One advance_time request. Step only compares observations, so the lifecycle can be tested
    // without a running game; AdvanceRunner gathers observations and applies side effects.
    internal sealed class AdvanceRun
    {
        public const string Running = "running";
        public const int MaxEvents = 50;
        // Also the order in which simultaneous events are reported.
        public static readonly string[] Conditions = { "death", "downed", "injured", "mentalBreak", "hostiles", "threatLetter", "letter" };
        public static readonly string[] DefaultStopOn = { "death", "downed", "injured", "mentalBreak", "hostiles", "threatLetter" };

        public readonly int Id;
        public readonly string RequestId;
        public readonly object Game;
        public readonly TimeSpeed Speed;
        public readonly int StartTick;
        public readonly int TargetTick;
        public readonly float Hours;
        public readonly HashSet<string> StopOn;
        public readonly List<AdvanceEvent> Events = new List<AdvanceEvent>();
        public readonly ManualResetEventSlim Ended = new ManualResetEventSlim(false);
        public int DroppedEvents;
        public string Status = Running;
        public string EndReason;
        public AdvanceEvent StoppedBy;
        public int EndTick = -1;
        public AdvanceObservation Previous;

        public AdvanceRun(int id, string requestId, object game, TimeSpeed speed, int startTick, int targetTick, float hours, IEnumerable<string> stopOn, AdvanceObservation baseline)
        {
            Id = id;
            RequestId = requestId;
            Game = game;
            Speed = speed;
            StartTick = startTick;
            TargetTick = targetTick;
            Hours = hours;
            StopOn = new HashSet<string>(stopOn);
            Previous = baseline;
        }

        public void Step(AdvanceObservation current)
        {
            if (Status != Running)
            {
                return;
            }
            if (!current.Playing || !ReferenceEquals(current.Game, Game))
            {
                End("interrupted", "gameUnloaded", Previous.Tick);
                return;
            }

            List<AdvanceEvent> changes = Detect(Previous, current);
            foreach (AdvanceEvent change in changes)
            {
                Record(change);
            }
            Previous = current;

            // A stop condition outranks reaching the target and a pause in the same frame: the
            // game pauses itself for some letters, and the agent needs the cause.
            AdvanceEvent stop = changes.FirstOrDefault(StopsRun);
            if (stop != null)
            {
                StoppedBy = stop;
                End("stopped", stop.Kind, current.Tick);
            }
            else if (current.Tick >= TargetTick)
            {
                End("completed", "completed", current.Tick);
            }
            else if (current.Speed != Speed)
            {
                End("interrupted", current.Speed == TimeSpeed.Paused ? "paused" : "speedChanged", current.Tick);
            }
        }

        public void End(string status, string reason, int tick)
        {
            Status = status;
            EndReason = reason;
            EndTick = tick;
        }

        private bool StopsRun(AdvanceEvent change)
        {
            return StopOn.Contains(change.Kind) || change.Kind == "threatLetter" && StopOn.Contains("letter");
        }

        private void Record(AdvanceEvent change)
        {
            if (Events.Count >= MaxEvents)
            {
                DroppedEvents++;
                return;
            }
            Events.Add(change);
        }

        // Changes since the previous frame. Comparing frames rather than the start means a colonist
        // who is rescued and downed again stops the run again.
        public static List<AdvanceEvent> Detect(AdvanceObservation previous, AdvanceObservation current)
        {
            List<AdvanceEvent> changes = new List<AdvanceEvent>();
            foreach (ColonistState now in current.Colonists.Values)
            {
                ColonistState before;
                if (!previous.Colonists.TryGetValue(now.Id, out before) || before.Dead)
                {
                    continue;
                }
                if (now.Dead)
                {
                    changes.Add(PawnEvent("death", current.Tick, now, "died"));
                    continue;
                }
                if (now.Downed && !before.Downed)
                {
                    changes.Add(PawnEvent("downed", current.Tick, now, "downed"));
                }
                if (now.Injuries > before.Injuries || now.Bleeding && !before.Bleeding)
                {
                    changes.Add(PawnEvent("injured", current.Tick, now, now.Bleeding ? "bleeding" : "new injury"));
                }
                if (now.MentalState != null && before.MentalState == null)
                {
                    changes.Add(PawnEvent("mentalBreak", current.Tick, now, now.MentalState));
                }
            }
            if (current.Threat != null && previous.Threat == null)
            {
                changes.Add(new AdvanceEvent { Kind = "hostiles", Tick = current.Tick, Detail = current.Threat });
            }
            foreach (LetterState letter in current.Letters.Values.OrderBy(letter => letter.Id))
            {
                if (!previous.Letters.ContainsKey(letter.Id))
                {
                    changes.Add(new AdvanceEvent { Kind = letter.Threat ? "threatLetter" : "letter", Tick = current.Tick, Detail = letter.Label });
                }
            }
            return changes.OrderBy(change => Array.IndexOf(Conditions, change.Kind)).ToList();
        }

        private static AdvanceEvent PawnEvent(string kind, int tick, ColonistState pawn, string detail)
        {
            return new AdvanceEvent { Kind = kind, Tick = tick, PawnId = pawn.Id, PawnName = pawn.Name, Detail = detail };
        }
    }

    // Owns the single advance run per session. Start, Update, and Interrupt run on the main thread;
    // WaitForEnd runs on HTTP threads and only touches the run's Ended event.
    internal static class AdvanceRunner
    {
        public const string Path = "/v1/time/advance";
        public const double MaxWaitSeconds = 30;

        private static volatile AdvanceRun latest;
        private static int nextId;
        private static List<Pawn> tracked = new List<Pawn>();

        public static AdvanceRun Latest
        {
            get { return latest; }
        }

        public static AdvanceRun Start(float hours, TimeSpeed speed, IEnumerable<string> stopOn, string requestId, out bool created)
        {
            AdvanceRun last = latest;
            if (requestId != null && last != null && last.RequestId == requestId && ReferenceEquals(last.Game, Current.Game))
            {
                created = false;
                return last;
            }
            if (last != null && last.Status == AdvanceRun.Running)
            {
                throw new CommandException(409, "An advance is already running (run " + last.Id + "). Use get_advance to wait for it, or set_game_speed to stop it.");
            }

            TickManager ticks = Find.TickManager;
            ticks.CurTimeSpeed = speed;
            if (ticks.CurTimeSpeed != speed)
            {
                throw new CommandException(409, "RimWorld is not accepting time control right now.");
            }

            tracked = new List<Pawn>();
            AdvanceObservation baseline = Observe();
            int duration = (int)Math.Ceiling(hours * GenDate.TicksPerHour);
            AdvanceRun run = new AdvanceRun(++nextId, requestId, Current.Game, speed, baseline.Tick, baseline.Tick + duration, hours, stopOn, baseline);
            latest = run;
            created = true;
            return run;
        }

        public static AdvanceRun Interrupt(string reason)
        {
            AdvanceRun run = latest;
            if (run == null || run.Status != AdvanceRun.Running)
            {
                return null;
            }
            run.End("interrupted", reason, Find.TickManager == null ? run.Previous.Tick : Find.TickManager.TicksGame);
            run.Ended.Set();
            return run;
        }

        public static void Update()
        {
            AdvanceRun run = latest;
            if (run == null || run.Status != AdvanceRun.Running)
            {
                return;
            }

            try
            {
                run.Step(Observe());
            }
            catch (Exception ex)
            {
                run.End("interrupted", "error: " + ex.GetType().Name + ": " + ex.Message, run.Previous.Tick);
            }
            if (run.Status == AdvanceRun.Running)
            {
                return;
            }

            // The agent's turn starts paused. A pause or speed change by someone else is left alone.
            if ((run.Status == "stopped" || run.Status == "completed") && ReferenceEquals(Current.Game, run.Game) && Find.TickManager != null)
            {
                Find.TickManager.Pause();
            }
            run.Ended.Set();
        }

        // Waits off the main thread so the game keeps running. Returns early when the run ends or
        // the game stops updating because it is in the background.
        public static void WaitForEnd(double seconds)
        {
            AdvanceRun run = latest;
            if (run == null)
            {
                return;
            }
            Stopwatch elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed.TotalSeconds < seconds && BridgeRuntime.MainThreadAvailable)
            {
                if (run.Ended.Wait(250))
                {
                    return;
                }
            }
        }

        public static double RequestedWaitSeconds(BridgeRequest request)
        {
            if (!string.Equals(request.Uri.AbsolutePath.TrimEnd('/'), Path, StringComparison.Ordinal))
            {
                return 0;
            }
            double seconds;
            string text = ReadRequest.FromUri(request.Uri).Get("waitSeconds");
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) || double.IsNaN(seconds))
            {
                return 0;
            }
            return Math.Max(0, Math.Min(MaxWaitSeconds, seconds));
        }

        private static AdvanceObservation Observe()
        {
            AdvanceObservation observation = new AdvanceObservation
            {
                Game = Current.Game,
                Playing = Current.Game != null && Current.ProgramState == ProgramState.Playing && Find.TickManager != null
            };
            if (!observation.Playing)
            {
                return observation;
            }

            observation.Tick = Find.TickManager.TicksGame;
            observation.Speed = Find.TickManager.CurTimeSpeed;
            List<Pawn> pawns = new List<Pawn>();
            foreach (Map map in Find.Maps)
            {
                pawns.AddRange(map.mapPawns.FreeColonistsSpawned);
                IAttackTarget threat;
                if (observation.Threat == null && GenHostility.AnyHostileActiveThreatTo(map, Faction.OfPlayer, out threat))
                {
                    observation.Threat = threat.Thing == null ? "hostiles" : threat.Thing.LabelCap.ToString();
                }
            }
            // Colonists who died since the last frame are no longer spawned colonists.
            pawns.AddRange(tracked.Where(pawn => pawn.Dead && !pawns.Contains(pawn)));
            tracked = pawns.Where(pawn => !pawn.Dead).ToList();

            foreach (Pawn pawn in pawns)
            {
                observation.Colonists[pawn.ThingID] = Colonist(pawn);
            }
            foreach (Letter letter in Find.LetterStack.LettersListForReading)
            {
                observation.Letters[letter.ID] = new LetterState
                {
                    Id = letter.ID,
                    Label = letter.Label.ToString(),
                    DefName = letter.def == null ? null : letter.def.defName,
                    Threat = letter.def != null && (letter.def == LetterDefOf.ThreatBig || letter.def == LetterDefOf.ThreatSmall)
                };
            }
            return observation;
        }

        private static ColonistState Colonist(Pawn pawn)
        {
            ColonistState state = new ColonistState
            {
                Id = pawn.ThingID,
                Name = ReadUtil.PawnName(pawn),
                Dead = pawn.Dead
            };
            if (pawn.Dead || pawn.health == null || pawn.health.hediffSet == null)
            {
                return state;
            }
            state.Downed = pawn.Downed;
            state.Bleeding = pawn.health.hediffSet.BleedRateTotal > 0f;
            // Fresh injuries only: scars are permanent, and diseases needing re-tending are not new harm.
            state.Injuries = pawn.health.hediffSet.hediffs.Count(hediff => hediff is Hediff_Injury && !hediff.IsPermanent());
            state.MentalState = pawn.MentalStateDef == null ? null : ReadUtil.DefLabel(pawn.MentalStateDef);
            return state;
        }
    }
}
