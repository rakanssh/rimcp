using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Read;
using RiMCP.Util;
using UnityEngine;
using Verse;

namespace RiMCP.Command
{
    internal sealed class GizmoEntry
    {
        public string Id;
        public Verse.Command Command;
        public string Kind;
        public ActionOrigin Origin;
    }

    internal static class GizmoService
    {
        public static BridgeResponse ListGizmos(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.FromMap(request);
            Thing thing = Owner(context.Map, route["thingId"]);
            List<GizmoEntry> gizmos = Build(thing);
            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("thing", SummaryDto.Thing(thing)),
                Dto.Field("revision", Revision(thing, gizmos)),
                Dto.Field("gizmos", gizmos.Select(entry => Describe(thing, entry, context.Request.Detail != ReadDetail.Summary)).ToArray())));
        }

        public static BridgeResponse SetBuildingToggle(BridgeRequest request, RouteMatch route)
        {
            GizmoBody body = CommandUtil.ReadBody<GizmoBody>(request);
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            Require(body);
            if (!body.On.HasValue)
            {
                throw new CommandException(400, "Missing required field 'on'.");
            }
            CommandContext context = CommandUtil.ContextFor(body.MapId);
            CommandUtil.RequireMap(context);
            RequestKey key = RequestKey.For("set_building_toggle", requestId, context.Map, route["buildingId"], body.Revision, body.Gizmo, body.On);
            return CommandReceipts.Once(context, key, () =>
            {
                Building building = BuildingReadService.FindBuilding(context.Map, route["buildingId"]);
                if (building == null)
                {
                    throw new CommandException(404, "Building not found.");
                }
                GizmoEntry entry = Find(building, body);
                if (Tool(building, entry) != "set_building_toggle")
                {
                    throw new CommandException(409, "'" + Label(entry) + "' is not one of the game's building toggles; press it with use_gizmo.");
                }
                Dictionary<string, object> data = Dto.Obj(Dto.Field("building", SummaryDto.Thing(building)), Dto.Field("gizmo", Label(entry)));
                bool changed = false;
                Press.Execute(data, entry.Origin, "set_building_toggle", Label(entry), building, context.Map, LocalTargetInfo.Invalid, () => changed = SetToggle((Command_Toggle)entry.Command, body.On.Value, data));
                Pending(building, data);
                return new CommandResult(changed, data);
            });
        }

        public static BridgeResponse UseGizmo(BridgeRequest request, RouteMatch route)
        {
            GizmoBody body = CommandUtil.ReadBody<GizmoBody>(request);
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            Require(body);
            CommandContext context = CommandUtil.ContextFor(body.MapId);
            CommandUtil.RequireCurrentMap(context);
            RequestKey key = RequestKey.For("use_gizmo", requestId, context.Map, route["thingId"], body.Revision, body.Gizmo, body.On, body.TargetId, body.X, body.Z, body.DestinationX, body.DestinationZ);
            return CommandReceipts.Once(context, key, () => Use(context.Map, route["thingId"], body));
        }

        private static CommandResult Use(Map map, string thingId, GizmoBody body)
        {
            Thing thing = Owner(map, thingId);
            GizmoEntry entry = Find(thing, body);
            if (Tool(thing, entry) != "use_gizmo")
            {
                throw new CommandException(409, "'" + Label(entry) + "' is a building setting; set it with set_building_toggle.");
            }
            LocalTargetInfo target = Targets.Parse(map, body.TargetId, body.X, body.Z, "target");
            LocalTargetInfo destination = Targets.Parse(map, null, body.DestinationX, body.DestinationZ, "destination");
            Dictionary<string, object> data = Dto.Obj(
                Dto.Field("thing", SummaryDto.Thing(thing)),
                Dto.Field("gizmo", Label(entry)),
                Dto.Field("origin", entry.Origin.Mod));
            Verse.Command command = entry.Command;
            bool standard = GameActions.IsGame(GameActions.PressedBy(command).Assembly);
            if (entry.Kind == "toggle" && standard && !body.On.HasValue)
            {
                throw new CommandException(400, "'" + Label(entry) + "' is a toggle; give on: true or false.");
            }
            bool changed = true;
            LocalTargetInfo aim = target;
            Action press;
            if (entry.Kind == "toggle" && standard)
            {
                press = () => changed = SetToggle((Command_Toggle)command, body.On.Value, data);
            }
            else if (entry.Kind == "target" && standard)
            {
                // The target is given here, so targeting that starts after it is cancelled, not given it again.
                press = () => Aim(command, target, destination, map);
                data["target"] = Targets.Describe(target);
                aim = LocalTargetInfo.Invalid;
            }
            else
            {
                press = () => command.ProcessInput(new Event());
            }
            Press.Execute(data, entry.Origin, "use_gizmo", Label(entry), thing, map, aim, press);
            Pending(thing, data);
            Pawn pawn = thing as Pawn;
            if (pawn != null)
            {
                data["job"] = PawnActionService.PawnJob(pawn);
            }
            return new CommandResult(changed, data);
        }

        private static void Aim(Verse.Command command, LocalTargetInfo target, LocalTargetInfo destination, Map map)
        {
            Command_Target plain = command as Command_Target;
            if (plain != null)
            {
                Refuse(Targets.Check(plain.targetingParams, target, map));
                plain.action(target);
                return;
            }
            Command_VerbTarget verb = command as Command_VerbTarget;
            if (verb != null)
            {
                Refuse(Targets.Check(verb.verb, target, map));
                Targets.Give(verb.verb, target);
                return;
            }
            Ability ability = ((Command_Ability)command).Ability;
            if (!ability.def.targetRequired)
            {
                ability.QueueCastingJob(ability.pawn, LocalTargetInfo.Invalid);
                return;
            }
            ITargetingSource source = ability.verb;
            Refuse(Targets.Check(source, target, map));
            CompAbilityEffect_WithDest second = source.DestinationSelector as CompAbilityEffect_WithDest;
            if (source.DestinationSelector == null)
            {
                Targets.Give(source, target);
                return;
            }
            if (second == null)
            {
                Refuse("It asks for a second target, which RimMCP cannot check first for it yet.");
            }
            // The second target is checked from the first, as targeting mode does, and the first is
            // let go again if the second is refused.
            second.SetTarget(target);
            string refusal = destination.IsValid ? Targets.Check(second, destination, map) : "It asks for a destination too; give destinationX and destinationZ.";
            if (refusal != null)
            {
                second.SetTarget(LocalTargetInfo.Invalid);
                Refuse(refusal);
            }
            second.OrderForceTarget(destination);
        }

        private static bool SetToggle(Command_Toggle toggle, bool on, Dictionary<string, object> data)
        {
            bool was = toggle.isActive();
            if (was != on)
            {
                toggle.toggleAction();
            }
            bool now = toggle.isActive();
            data["on"] = now;
            if (was == on)
            {
                data["alreadySatisfied"] = true;
            }
            else if (now != on)
            {
                data["note"] = "The game did not change it.";
            }
            return was != now;
        }

        // A power switch changes only when a colonist flicks it.
        private static void Pending(Thing thing, Dictionary<string, object> data)
        {
            CompFlickable flick = thing.TryGetComp<CompFlickable>();
            if (flick != null && flick.WantsFlick())
            {
                data["pending"] = "flick";
                data["pendingNote"] = "The switch changes when a colonist flicks it; a flick job is waiting.";
            }
        }

        private static void Require(GizmoBody body)
        {
            if (string.IsNullOrEmpty(body.Gizmo) || string.IsNullOrEmpty(body.Revision))
            {
                throw new CommandException(400, "Give gizmo and revision, both from list_gizmos.");
            }
        }

        private static GizmoEntry Find(Thing thing, GizmoBody body)
        {
            List<GizmoEntry> gizmos = Build(thing);
            if (Revision(thing, gizmos) != body.Revision)
            {
                throw new CommandException(409, "The buttons changed since they were read; read them again with list_gizmos.");
            }
            GizmoEntry entry = gizmos.FirstOrDefault(each => each.Id == body.Gizmo);
            if (entry == null)
            {
                throw new CommandException(400, "No button '" + body.Gizmo + "'.");
            }
            if (entry.Command.Disabled)
            {
                throw new CommandException(409, "'" + Label(entry) + "' is unavailable" + (string.IsNullOrEmpty(entry.Command.disabledReason) ? "." : ": " + entry.Command.disabledReason.StripTags()));
            }
            if (entry.Origin.Unsupported != null)
            {
                throw new CommandException(409, "'" + Label(entry) + "' is not supported: " + entry.Origin.Unsupported);
            }
            return entry;
        }

        private static Thing Owner(Map map, string thingId)
        {
            Thing thing = Targets.Parse(map, thingId, null, null, "thing").Thing;
            if (thing == null)
            {
                throw new CommandException(404, "Thing not found.");
            }
            return thing;
        }

        // As the game shows them: with the thing selected.
        public static List<GizmoEntry> Build(Thing thing)
        {
            List<GizmoEntry> gizmos = new List<GizmoEntry>();
            foreach (Gizmo gizmo in Selection.With(thing, () => thing.GetGizmos().ToList()))
            {
                Verse.Command command = gizmo as Verse.Command;
                if (command == null || !command.Visible)
                {
                    continue;
                }
                gizmos.Add(new GizmoEntry { Id = (gizmos.Count + 1).ToString(), Command = command, Kind = Kind(command), Origin = GameActions.ForButton(command) });
            }
            foreach (GizmoEntry entry in gizmos.Where(each => each.Kind == "target" && each.Origin.Unsupported == null))
            {
                Command_Ability ability = entry.Command as Command_Ability;
                if (ability != null && ability.Ability.def.targetWorldCell)
                {
                    entry.Origin.Unsupported = "It asks for a world-map target, which agents cannot give yet.";
                }
            }
            return gizmos;
        }

        private static string Kind(Verse.Command command)
        {
            if (command is Command_Toggle)
            {
                return "toggle";
            }
            Command_Ability ability = command as Command_Ability;
            if (ability != null)
            {
                return ability.Ability.def.targetRequired ? "target" : "action";
            }
            return command is Command_Target || command is Command_VerbTarget ? "target" : "action";
        }

        // The game's toggles on buildings are settings; anything else is pressed with use_gizmo.
        private static string Tool(Thing thing, GizmoEntry entry)
        {
            return entry.Kind == "toggle" && thing is Building && !entry.Origin.IsMod ? "set_building_toggle" : "use_gizmo";
        }

        // Not a toggle's state, so setting one stays valid while it changes.
        private static string Revision(Thing thing, List<GizmoEntry> gizmos)
        {
            return Fingerprint.Of(GameContext.LoadId + "\n" + (thing.MapHeld == null ? "" : thing.MapHeld.uniqueID.ToString()) + "\n" + ReadUtil.ThingId(thing) + "\n" + string.Join("\n", gizmos.Select(entry => entry.Id + "\t" + entry.Command.GetType().FullName + "\t" + entry.Command.Label + "\t" + entry.Command.Disabled).ToArray()));
        }

        private static object Describe(Thing thing, GizmoEntry entry, bool full)
        {
            Command_Toggle toggle = entry.Command as Command_Toggle;
            return Dto.Obj(
                Dto.Field("id", entry.Id),
                Dto.Field("label", Label(entry)),
                Dto.Field("kind", entry.Kind),
                Dto.Field("on", toggle == null || toggle.isActive == null ? null : (object)toggle.isActive()),
                Dto.Field("takes", entry.Kind == "target" ? Takes(entry.Command) : null),
                Dto.Field("needsDestination", entry.Kind == "target" && Destination(entry.Command) ? (object)true : null),
                Dto.Field("disabledReason", entry.Command.Disabled ? (string.IsNullOrEmpty(entry.Command.disabledReason) ? "Unavailable." : entry.Command.disabledReason.StripTags()) : null),
                Dto.Field("origin", entry.Origin.Mod),
                Dto.Field("unsupported", entry.Origin.Unsupported),
                Dto.Field("tool", entry.Origin.Unsupported == null ? Tool(thing, entry) : null),
                Dto.Field("description", full ? entry.Command.Desc.StripTags() : null));
        }

        private static string[] Takes(Verse.Command command)
        {
            Command_Target plain = command as Command_Target;
            if (plain != null)
            {
                return Targets.Takes(plain.targetingParams);
            }
            Command_VerbTarget verb = command as Command_VerbTarget;
            if (verb != null)
            {
                return Targets.Takes(verb.verb.targetParams);
            }
            Command_Ability ability = command as Command_Ability;
            return ability == null ? null : Targets.Takes(ability.Ability.verb.targetParams);
        }

        private static bool Destination(Verse.Command command)
        {
            Command_Ability ability = command as Command_Ability;
            return ability != null && ability.Ability.verb.DestinationSelector != null;
        }

        private static string Label(GizmoEntry entry)
        {
            return (entry.Command.LabelCap ?? entry.Command.GetType().Name).StripTags();
        }

        private static void Refuse(string reason)
        {
            if (reason != null)
            {
                throw new CommandException(409, reason);
            }
        }

        [DataContract]
        private sealed class GizmoBody
        {
            [DataMember(Name = "mapId")]
            public string MapId { get; set; }

            [DataMember(Name = "gizmo")]
            public string Gizmo { get; set; }

            [DataMember(Name = "revision")]
            public string Revision { get; set; }

            [DataMember(Name = "on")]
            public bool? On { get; set; }

            [DataMember(Name = "targetId")]
            public string TargetId { get; set; }

            [DataMember(Name = "x")]
            public int? X { get; set; }

            [DataMember(Name = "z")]
            public int? Z { get; set; }

            [DataMember(Name = "destinationX")]
            public int? DestinationX { get; set; }

            [DataMember(Name = "destinationZ")]
            public int? DestinationZ { get; set; }

            [DataMember(Name = "requestId")]
            public string RequestId { get; set; }
        }
    }
}
