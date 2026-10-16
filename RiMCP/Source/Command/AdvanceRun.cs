using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using RimWorld;
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
        // Label of a home map whose stored food is below the Low food threshold, or null.
        public string LowFood;
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
        public static readonly string[] Conditions = { "death", "downed", "injured", "mentalBreak", "hostiles", "lowFood", "threatLetter", "letter" };
        public static readonly string[] DefaultStopOn = { "death", "downed", "injured", "mentalBreak", "hostiles", "lowFood", "threatLetter" };

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
        public int Frames;
        public double FrameMsTotal;
        public double FrameMsMax;
        public bool Failed;
        public bool Revoked;

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

        public bool SameRequest(float hours, TimeSpeed speed, ICollection<string> stopOn)
        {
            return Hours == hours && Speed == speed && StopOn.SetEquals(stopOn);
        }

        public void RecordFrame(double milliseconds)
        {
            Frames++;
            FrameMsTotal += milliseconds;
            FrameMsMax = Math.Max(FrameMsMax, milliseconds);
        }

        // The run owns time until it ends: completing, stopping, or failing pauses the game. A pause
        // or speed change by someone else is left alone.
        public bool PausesGameAtEnd
        {
            get { return Failed || Revoked || Status == "stopped" || Status == "completed"; }
        }

        public void Revoke(string reason, int tick)
        {
            Revoked = true;
            End("interrupted", reason, tick);
        }

        public void Fail(string message, int tick)
        {
            Failed = true;
            End("interrupted", "error: " + message, tick);
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
            if (current.LowFood != null && previous.LowFood == null)
            {
                changes.Add(new AdvanceEvent { Kind = "lowFood", Tick = current.Tick, Detail = current.LowFood });
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

    // Owns advance runs. Everything here runs on the main thread; HTTP threads only wait on a
    // run's Ended event.
    internal static class AdvanceRunner
    {
        // Retries are recognised for this many recent runs of the loaded game.
        public const int MaxHistory = 32;

        // Runs of the loaded game, oldest first.
        private static readonly List<AdvanceRun> history = new List<AdvanceRun>();
        private static AdvanceRun latest;
        private static int nextId;
        private static List<Pawn> tracked = new List<Pawn>();
        // Summing stored food costs too much for every frame, so it is checked every 250 ticks and
        // carried between checks.
        private const int FoodCheckInterval = GenTicks.TickRareInterval;
        private static int foodCheckedTick = int.MinValue;
        private static string lowFood;

        // Replaceable so tests can simulate an observation failure.
        internal static Func<AdvanceObservation> Observer = Observe;

        public static AdvanceRun LatestInLoadedGame
        {
            get { return latest != null && ReferenceEquals(latest.Game, Current.Game) ? latest : null; }
        }

        public static AdvanceRun RunById(int id)
        {
            return history.FirstOrDefault(run => run.Id == id && ReferenceEquals(run.Game, Current.Game));
        }

        public static AdvanceRun Start(float hours, TimeSpeed speed, ICollection<string> stopOn, string requestId, out bool created)
        {
            history.RemoveAll(run => !ReferenceEquals(run.Game, Current.Game));
            AdvanceRun receipt = FindReceipt(history, Current.Game, requestId, hours, speed, stopOn);
            if (receipt != null)
            {
                created = false;
                return receipt;
            }
            if (latest != null && latest.Status == AdvanceRun.Running && ReferenceEquals(latest.Game, Current.Game))
            {
                throw new CommandException(409, "An advance is already running (run " + latest.Id + "). Use get_advance to wait for it, or set_game_speed to stop it.");
            }

            // Take the starting snapshot before unpausing, so a failure here leaves the game as it was.
            tracked = new List<Pawn>();
            foodCheckedTick = int.MinValue;
            AdvanceObservation baseline = Observe();
            TickManager ticks = Find.TickManager;
            ticks.CurTimeSpeed = speed;
            if (ticks.CurTimeSpeed != speed)
            {
                throw new CommandException(409, "RimWorld is not accepting time control right now.");
            }

            int duration = (int)Math.Ceiling(hours * GenDate.TicksPerHour);
            AdvanceRun run = new AdvanceRun(++nextId, requestId, Current.Game, speed, baseline.Tick, baseline.Tick + duration, hours, stopOn, baseline);
            history.Add(run);
            if (history.Count > MaxHistory)
            {
                history.RemoveAt(0);
            }
            latest = run;
            created = true;
            return run;
        }

        // A repeated requestId returns its run; the same id with different arguments is a client error.
        public static AdvanceRun FindReceipt(IEnumerable<AdvanceRun> runs, object game, string requestId, float hours, TimeSpeed speed, ICollection<string> stopOn)
        {
            if (requestId == null)
            {
                return null;
            }
            AdvanceRun run = runs.LastOrDefault(item => item.RequestId == requestId && ReferenceEquals(item.Game, game));
            if (run != null && !run.SameRequest(hours, speed, stopOn))
            {
                throw new CommandException(409, "requestId '" + requestId + "' was already used for run " + run.Id + " with different arguments.");
            }
            return run;
        }

        public static AdvanceRun Interrupt(string reason)
        {
            AdvanceRun run = latest;
            if (run == null || run.Status != AdvanceRun.Running)
            {
                return null;
            }
            run.End("interrupted", reason, Current.Game == null ? run.Previous.Tick : Find.TickManager.TicksGame);
            run.Ended.Set();
            return run;
        }

        // The agent lost time control while its run owned time: end the run and pause.
        public static AdvanceRun Revoke(string reason)
        {
            AdvanceRun run = latest;
            if (run == null || run.Status != AdvanceRun.Running)
            {
                return null;
            }
            bool loaded = Current.Game != null && ReferenceEquals(Current.Game, run.Game);
            run.Revoke(reason, loaded ? Find.TickManager.TicksGame : run.Previous.Tick);
            if (loaded)
            {
                Find.TickManager.Pause();
            }
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

            Stopwatch frame = Stopwatch.StartNew();
            try
            {
                run.Step(Observer());
            }
            catch (Exception ex)
            {
                run.Fail(ex.GetType().Name + ": " + ex.Message, run.Previous.Tick);
            }
            run.RecordFrame(frame.Elapsed.TotalMilliseconds);
            if (run.Status == AdvanceRun.Running)
            {
                return;
            }

            if (run.PausesGameAtEnd && Current.Game != null && ReferenceEquals(Current.Game, run.Game))
            {
                Find.TickManager.Pause();
            }
            run.Ended.Set();
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
                    observation.Threat = ThreatLabel(threat.Thing);
                }
            }
            if (observation.Tick < foodCheckedTick || observation.Tick >= foodCheckedTick + FoodCheckInterval)
            {
                foodCheckedTick = observation.Tick;
                Map low = Find.Maps.FirstOrDefault(FoodSummary.IsLow);
                lowFood = low == null ? null : "stored food on " + (low.Parent == null ? "map " + low.uniqueID : low.Parent.LabelCap) + " is below " + FoodSummary.LowPerColonist + " nutrition per colonist";
            }
            observation.LowFood = lowFood;
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
                    Label = letter.Label.ToString().StripTags(),
                    DefName = letter.def == null ? null : letter.def.defName,
                    Threat = letter.def != null && (letter.def == LetterDefOf.ThreatBig || letter.def == LetterDefOf.ThreatSmall)
                };
            }
            return observation;
        }

        // Labels carry rich-text markup, such as the coloured title after a raider's name.
        private static string ThreatLabel(Thing thing)
        {
            if (thing == null)
            {
                return "hostiles";
            }
            Pawn pawn = thing as Pawn;
            return (pawn != null ? pawn.LabelShortCap : thing.LabelCap).StripTags();
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
