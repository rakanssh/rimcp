using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Read;
using RiMCP.Util;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RiMCP.Command
{
    internal sealed class MenuEntry
    {
        public string Id;
        public FloatMenuOption Option;
        public FloatMenuOptionProvider Provider;
        public ActionOrigin Origin;
    }

    internal static class PawnActionService
    {
        private static readonly FieldInfo GameProviders = typeof(FloatMenuMakerMap).GetField("providers", BindingFlags.Static | BindingFlags.NonPublic);

        public static BridgeResponse ListPawnActions(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.FromMap(request);
            Pawn pawn = MenuPawn(context.Map, route["pawnId"]);
            LocalTargetInfo target = Targets.Parse(context.Map, context.Request.Get("targetId"), context.Request.OptionalInt("x"), context.Request.OptionalInt("z"), "target");
            if (!target.IsValid)
            {
                throw new CommandException(400, "Give targetId, or x and z: what the pawn's menu is opened on.");
            }
            string unavailable;
            List<MenuEntry> menu = Build(pawn, target, context.Map, out unavailable);
            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("pawn", SummaryDto.Pawn(pawn)),
                Dto.Field("target", Targets.Describe(target)),
                Dto.Field("unavailable", unavailable),
                Dto.Field("revision", Revision(pawn, target, menu)),
                Dto.Field("options", menu.Select(Describe).ToArray())));
        }

        public static BridgeResponse ChoosePawnAction(BridgeRequest request, RouteMatch route)
        {
            ChoosePawnActionBody body = CommandUtil.ReadBody<ChoosePawnActionBody>(request);
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            if (string.IsNullOrEmpty(body.Option) || string.IsNullOrEmpty(body.Revision))
            {
                throw new CommandException(400, "Give option and revision, both from list_pawn_actions.");
            }
            CommandContext context = CommandUtil.ContextFor(body.MapId);
            CommandUtil.RequireCurrentMap(context);
            RequestKey key = RequestKey.For("choose_pawn_action", requestId, context.Map, route["pawnId"], body.TargetId, body.X, body.Z, body.Revision, body.Option, body.AimId, body.AimX, body.AimZ);
            return CommandReceipts.Once(context, key, () => Choose(context.Map, route["pawnId"], body));
        }

        private static CommandResult Choose(Map map, string pawnId, ChoosePawnActionBody body)
        {
            Pawn pawn = MenuPawn(map, pawnId);
            LocalTargetInfo target = Targets.Parse(map, body.TargetId, body.X, body.Z, "target");
            if (!target.IsValid)
            {
                throw new CommandException(400, "Give targetId, or x and z: the target the menu was read on.");
            }
            LocalTargetInfo aim = Targets.Parse(map, body.AimId, body.AimX, body.AimZ, "aim");
            string unavailable;
            List<MenuEntry> menu = Build(pawn, target, map, out unavailable);
            if (unavailable != null)
            {
                throw new CommandException(409, unavailable);
            }
            if (Revision(pawn, target, menu) != body.Revision)
            {
                throw new CommandException(409, "The menu changed since it was read; read it again with list_pawn_actions.");
            }
            MenuEntry entry = menu.FirstOrDefault(each => each.Id == body.Option);
            if (entry == null)
            {
                throw new CommandException(400, "The menu has no option '" + body.Option + "'.");
            }
            string label = entry.Option.Label.StripTags();
            if (entry.Option.Disabled || entry.Option.action == null)
            {
                throw new CommandException(409, "'" + label + "' is unavailable.");
            }
            if (entry.Origin.Unsupported != null)
            {
                throw new CommandException(409, "'" + label + "' is not supported: " + entry.Origin.Unsupported);
            }
            Dictionary<string, object> data = Dto.Obj(
                Dto.Field("pawn", SummaryDto.Pawn(pawn)),
                Dto.Field("chosen", label),
                Dto.Field("origin", entry.Origin.Mod));
            Press.Execute(data, entry.Origin, "choose_pawn_action", label, pawn, map, aim, () => entry.Option.Chosen(true, null));
            data["job"] = PawnJob(pawn);
            return new CommandResult(true, data);
        }

        private static Pawn MenuPawn(Map map, string pawnId)
        {
            Pawn pawn = PawnReadService.FindPawn(map, pawnId);
            if (pawn == null)
            {
                throw new CommandException(404, "Pawn not found.");
            }
            CommandUtil.RequirePlayerControlled(pawn);
            if (map != Find.CurrentMap)
            {
                throw new CommandException(409, "The menu is built on the map shown in game (map " + Find.CurrentMap.uniqueID + "). Switch to map " + map.uniqueID + " in game first.");
            }
            return pawn;
        }

        // As FloatMenuMakerMap builds it, each provider asked as the current one. A thing target gives
        // the options for that thing and for its cell.
        public static List<MenuEntry> Build(Pawn pawn, LocalTargetInfo target, Map map, out string unavailable)
        {
            List<MenuEntry> entries = new List<MenuEntry>();
            unavailable = null;
            AcceptanceReport gate = FloatMenuMakerMap.ShouldGenerateFloatMenuForPawn(pawn);
            if (!gate.Accepted)
            {
                unavailable = string.IsNullOrEmpty(gate.Reason) ? pawn.LabelShortCap + " cannot take orders right now." : gate.Reason.StripTags();
                return entries;
            }
            Vector3 click = target.HasThing ? target.Thing.DrawPos : target.Cell.ToVector3Shifted();
            FloatMenuContext context = new FloatMenuContext(new List<Pawn> { pawn }, click, map);
            List<FloatMenuOptionProvider> providers = GameProviders == null ? null : GameProviders.GetValue(null) as List<FloatMenuOptionProvider>;
            if (providers == null)
            {
                unavailable = "RimMCP could not read the game's menu providers.";
                return entries;
            }
            Pawn makingFor = FloatMenuMakerMap.makingFor;
            FloatMenuOptionProvider current = FloatMenuMakerMap.currentProvider;
            FloatMenuMakerMap.makingFor = pawn;
            try
            {
                foreach (FloatMenuOptionProvider provider in providers)
                {
                    try
                    {
                        FloatMenuMakerMap.currentProvider = provider;
                        if (!context.ValidSelectedPawns.Any() || !provider.Applies(context))
                        {
                            continue;
                        }
                        foreach (FloatMenuOption option in provider.GetOptions(context))
                        {
                            Add(entries, provider, option, null);
                        }
                        foreach (Thing clicked in context.ClickedThings)
                        {
                            if (target.HasThing && clicked != target.Thing || !provider.TargetThingValid(clicked, context))
                            {
                                continue;
                            }
                            Thing thing = clicked;
                            CompSelectProxy proxy;
                            if (thing.TryGetComp(out proxy) && proxy.thingToSelect != null)
                            {
                                thing = proxy.thingToSelect;
                            }
                            foreach (FloatMenuOption option in provider.GetOptionsFor(thing, context))
                            {
                                Add(entries, provider, option, thing);
                            }
                        }
                        foreach (Pawn clicked in context.ClickedPawns)
                        {
                            if (target.HasThing && clicked != target.Thing || !provider.TargetPawnValid(clicked, context))
                            {
                                continue;
                            }
                            foreach (FloatMenuOption option in provider.GetOptionsFor(clicked, context))
                            {
                                Add(entries, provider, option, clicked);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // The game logs a provider that fails and goes on without it; so does this.
                        Log.Warning("RimMCP: menu provider " + provider.GetType().Name + " failed: " + ex.Message);
                    }
                }
            }
            finally
            {
                FloatMenuMakerMap.makingFor = makingFor;
                FloatMenuMakerMap.currentProvider = current;
            }
            List<MenuEntry> ordered = entries.OrderByDescending(entry => entry.Option.Priority).ThenByDescending(entry => entry.Option.orderInPriority).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                ordered[i].Id = (i + 1).ToString();
            }
            return ordered;
        }

        private static void Add(List<MenuEntry> entries, FloatMenuOptionProvider provider, FloatMenuOption option, Thing thing)
        {
            if (option == null)
            {
                return;
            }
            if (thing != null)
            {
                if (option.iconThing == null)
                {
                    option.iconThing = thing;
                }
                option.targetsDespawned = !thing.Spawned;
            }
            entries.Add(new MenuEntry { Option = option, Provider = provider, Origin = GameActions.ForOption(provider, option, thing) });
        }

        private static string Revision(Pawn pawn, LocalTargetInfo target, List<MenuEntry> menu)
        {
            return Fingerprint.Of(GameContext.LoadId + "\n" + (pawn.MapHeld == null ? "" : pawn.MapHeld.uniqueID.ToString()) + "\n" + ReadUtil.ThingId(pawn) + "\n" + (target.HasThing ? ReadUtil.ThingId(target.Thing) : target.Cell.ToString()) + "\n" + string.Join("\n", menu.Select(entry =>
                entry.Id + "\t" + entry.Provider.GetType().FullName + "\t" + entry.Option.Label + "\t" + entry.Option.Disabled + "\t" +
                ReadUtil.ThingId(entry.Option.revalidateClickTarget ?? entry.Option.iconThing)).ToArray()));
        }

        private static object Describe(MenuEntry entry)
        {
            return Dto.Obj(
                Dto.Field("id", entry.Id),
                Dto.Field("text", entry.Option.Label.StripTags()),
                Dto.Field("disabled", entry.Option.Disabled || entry.Option.action == null ? (object)true : null),
                Dto.Field("origin", entry.Origin.Mod),
                Dto.Field("unsupported", entry.Origin.Unsupported));
        }

        public static object PawnJob(Pawn pawn)
        {
            Job job = pawn.CurJob;
            if (job == null)
            {
                return null;
            }
            return Dto.Obj(
                Dto.Field("def", job.def.defName),
                Dto.Field("report", job.GetReport(pawn).StripTags()),
                Dto.Field("queued", pawn.jobs != null && pawn.jobs.jobQueue != null && pawn.jobs.jobQueue.Count > 0 ? (object)pawn.jobs.jobQueue.Count : null));
        }

        [DataContract]
        private sealed class ChoosePawnActionBody
        {
            [DataMember(Name = "mapId")]
            public string MapId { get; set; }

            [DataMember(Name = "targetId")]
            public string TargetId { get; set; }

            [DataMember(Name = "x")]
            public int? X { get; set; }

            [DataMember(Name = "z")]
            public int? Z { get; set; }

            [DataMember(Name = "revision")]
            public string Revision { get; set; }

            [DataMember(Name = "option")]
            public string Option { get; set; }

            [DataMember(Name = "aimId")]
            public string AimId { get; set; }

            [DataMember(Name = "aimX")]
            public int? AimX { get; set; }

            [DataMember(Name = "aimZ")]
            public int? AimZ { get; set; }

            [DataMember(Name = "requestId")]
            public string RequestId { get; set; }
        }
    }
}
