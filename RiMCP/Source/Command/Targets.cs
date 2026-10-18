using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using RimWorld.Planet;
using RiMCP.Read;
using Verse;

namespace RiMCP.Command
{
    internal static class Targets
    {
        private static readonly FieldInfo LiveMessages = typeof(Messages).GetField("liveMessages", BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly FieldInfo TargeterAction = typeof(Targeter).GetField("action", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo TargeterParams = typeof(Targeter).GetField("targetParams", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo TargeterValidator = typeof(Targeter).GetField("targetValidator", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo ForcedTarget = typeof(Building_TurretGun).GetField("forcedTarget", BindingFlags.Instance | BindingFlags.NonPublic);

        // Replaceable for tests, which have no targeter.
        internal static Func<bool> Active = () =>
        {
            Targeter map = MapTargeting();
            WorldTargeter world = WorldTargeting();
            return map != null && map.IsTargeting || world != null && world.IsTargeting;
        };

        internal static Action Cancel = () =>
        {
            WorldTargeter world = WorldTargeting();
            if (world != null && world.IsTargeting)
            {
                world.StopTargeting();
            }
            Targeter map = MapTargeting();
            if (map != null && map.IsTargeting)
            {
                map.StopTargeting();
            }
        };

        public static LocalTargetInfo Parse(Map map, string thingId, int? x, int? z, string field)
        {
            if (!string.IsNullOrWhiteSpace(thingId))
            {
                if (x.HasValue || z.HasValue)
                {
                    throw new CommandException(400, "Give " + field + "Id or a cell, not both.");
                }
                Thing thing = map.listerThings.AllThings.FirstOrDefault(each => (each.ThingID == thingId || each.GetUniqueLoadID() == thingId) && each.Spawned && !each.Position.Fogged(map));
                if (thing == null)
                {
                    throw new CommandException(404, "No thing '" + thingId + "' on this map.");
                }
                return thing;
            }
            if (x.HasValue != z.HasValue)
            {
                throw new CommandException(400, "Give both x and z for a cell.");
            }
            if (!x.HasValue)
            {
                return LocalTargetInfo.Invalid;
            }
            IntVec3 cell = new IntVec3(x.Value, 0, z.Value);
            if (!cell.InBounds(map))
            {
                throw new CommandException(400, "That cell is off the map.");
            }
            if (cell.Fogged(map))
            {
                throw new CommandException(409, "That cell is unexplored.");
            }
            return cell;
        }

        public static object Describe(LocalTargetInfo target)
        {
            if (!target.IsValid)
            {
                return null;
            }
            if (target.HasThing)
            {
                return Dto.Obj(Dto.Field("id", ReadUtil.ThingId(target.Thing)), Dto.Field("label", target.Thing.LabelShortCap.StripTags()));
            }
            return ReadUtil.Cell(target.Cell);
        }

        public static string[] Takes(TargetingParameters parameters)
        {
            if (parameters == null)
            {
                return null;
            }
            List<string> takes = new List<string>();
            if (parameters.canTargetPawns || parameters.canTargetHumans || parameters.canTargetAnimals || parameters.canTargetMechs)
            {
                takes.Add("pawns");
            }
            if (parameters.canTargetBuildings)
            {
                takes.Add("buildings");
            }
            if (parameters.canTargetItems)
            {
                takes.Add("items");
            }
            if (parameters.canTargetPlants)
            {
                takes.Add("plants");
            }
            if (parameters.canTargetLocations)
            {
                takes.Add("cells");
            }
            return takes.ToArray();
        }

        public static string Check(TargetingParameters parameters, LocalTargetInfo target, Map map)
        {
            if (!target.IsValid)
            {
                return "It asks for a target (" + string.Join(", ", Takes(parameters) ?? new string[0]) + "); give one.";
            }
            return parameters != null && !parameters.CanTarget(target.ToTargetInfo(map)) ? "That is not something it can target." : null;
        }

        public static string Check(ITargetingSource source, LocalTargetInfo target, Map map)
        {
            if (!target.IsValid)
            {
                return "It asks for a target (" + string.Join(", ", Takes(source.targetParams) ?? new string[0]) + "); give one.";
            }
            if (source.targetParams != null && !source.targetParams.CanTarget(target.ToTargetInfo(map), source))
            {
                return "That is not something it can target.";
            }
            if (target.HasThing && target.Thing == source.Caster && (source.targetParams == null || !source.targetParams.canTargetSelf))
            {
                return "It cannot target itself.";
            }
            string message;
            if (!Captured(() => source.ValidateTarget(target, true), out message))
            {
                return message ?? "The game refuses that target.";
            }
            // Targeting mode takes a target other than a pawn only if it can be hit now.
            if (!(target.Thing is Pawn) && !source.IsMeleeAttack && !source.CanHitTarget(target))
            {
                return "It cannot hit that from where it is: out of range or out of sight.";
            }
            return null;
        }

        public static void Give(ITargetingSource source, LocalTargetInfo target)
        {
            if (source.CasterIsPawn)
            {
                source.OrderForceTarget(target);
                return;
            }
            Building_Turret turret = source.Caster as Building_Turret;
            if (turret == null)
            {
                throw new CommandException(409, "RimMCP cannot give this kind of target order.");
            }
            // The turret checks its own range and only says why it refuses.
            string message;
            if (!Captured(() =>
            {
                turret.OrderAttack(target);
                return ForcedTarget == null || !(turret is Building_TurretGun) || (LocalTargetInfo)ForcedTarget.GetValue(turret) == target;
            }, out message))
            {
                throw new CommandException(409, message ?? "The turret did not take the target.");
            }
        }

        // Gives the aim to targeting a press started, or cancels it as Escape does. Null if none started.
        public static Dictionary<string, object> Resolve(LocalTargetInfo aim, Map map)
        {
            WorldTargeter world = WorldTargeting();
            if (world != null && world.IsTargeting)
            {
                world.StopTargeting();
                return Dto.Obj(Dto.Field("targeting", "cancelled"), Dto.Field("reason", "It asked for a world-map target, which agents cannot give yet; targeting was cancelled as Escape does."));
            }
            Targeter targeter = MapTargeting();
            if (targeter == null || !targeter.IsTargeting)
            {
                return null;
            }
            try
            {
                ITargetingSource source = targeter.targetingSource;
                Action<LocalTargetInfo> action = TargeterAction == null ? null : TargeterAction.GetValue(targeter) as Action<LocalTargetInfo>;
                TargetingParameters parameters = source != null ? source.targetParams : TargeterParams == null ? null : TargeterParams.GetValue(targeter) as TargetingParameters;
                Func<LocalTargetInfo, bool> validator = TargeterValidator == null ? null : TargeterValidator.GetValue(targeter) as Func<LocalTargetInfo, bool>;
                string refusal = source != null
                    ? Check(source, aim, map) ?? (source.DestinationSelector != null ? "It asks for a second target, which agents cannot give for it yet." : null)
                    : action == null ? "It started targeting that RimMCP cannot read."
                    : Check(parameters, aim, map) ?? (validator != null && !validator(aim) ? "The game refuses that target." : null);
                if (refusal != null)
                {
                    return Dto.Obj(Dto.Field("targeting", "cancelled"), Dto.Field("reason", refusal + " Targeting was cancelled as Escape does."));
                }
                if (source != null)
                {
                    Give(source, aim);
                }
                else
                {
                    action(aim);
                }
                return Dto.Obj(Dto.Field("targeting", "given"), Dto.Field("target", Describe(aim)));
            }
            finally
            {
                if (targeter.IsTargeting)
                {
                    targeter.StopTargeting();
                }
            }
        }

        private static Targeter MapTargeting()
        {
            UIRoot_Play root = Find.UIRoot as UIRoot_Play;
            return root == null || root.mapUI == null ? null : root.mapUI.targeter;
        }

        private static WorldTargeter WorldTargeting()
        {
            return Current.Game == null || Find.World == null || Find.World.UI == null ? null : Find.WorldTargeter;
        }

        // A refusal the game shows as a message, taken as the reason.
        private static bool Captured(Func<bool> check, out string message)
        {
            IList<Message> live = LiveMessages == null ? null : LiveMessages.GetValue(null) as IList<Message>;
            int before = live == null ? 0 : live.Count;
            Message last = live == null || before == 0 ? null : live[before - 1];
            bool passed = check();
            message = null;
            if (!passed && live != null && live.Count > 0 && (live.Count > before || live[live.Count - 1] != last))
            {
                message = live[live.Count - 1].text.StripTags();
            }
            return passed;
        }
    }
}
