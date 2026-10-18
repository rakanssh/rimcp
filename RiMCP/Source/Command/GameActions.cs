using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;

namespace RiMCP.Command
{
    internal sealed class ActionOrigin
    {
        // Null for the game's own content (Core and the DLCs).
        public string Mod;
        public string Type;
        public string Unsupported;

        public bool IsMod
        {
            get { return Mod != null; }
        }
    }

    // The game's own right-click options and buttons, audited for 1.6 and 1.7: those that go on to a
    // window or a target RimMCP cannot check first are refused. Anything a mod adds is mod content.
    internal static class GameActions
    {
        private static readonly Assembly Game = typeof(Pawn).Assembly;

        private const string AsksTarget = "It asks for a target, which RimMCP cannot check first yet.";
        private const string RitualWindow = "It opens the ritual window, which agents cannot use yet.";

        // Every provider in the game, so one added in an update is noticed. The generic ones (a
        // thing's, a zone's, or a lord's own options, and work givers) are judged by Origins.
        public static readonly Dictionary<string, string> Providers = new Dictionary<string, string>
        {
            { "FloatMenuOptionProvider_Arrest", null },
            { "FloatMenuOptionProvider_BringBabyToSafety", null },
            { "FloatMenuOptionProvider_CaptureEntity", null },
            { "FloatMenuOptionProvider_CapturePawn", null },
            { "FloatMenuOptionProvider_CarryDeathrestingToCasket", null },
            { "FloatMenuOptionProvider_CarryMechToCharger", null },
            { "FloatMenuOptionProvider_CarryPawn", null },
            { "FloatMenuOptionProvider_CarryPawnToExit", null },
            { "FloatMenuOptionProvider_CarryToBiosculpterPod", null },
            { "FloatMenuOptionProvider_CarryToCryptosleepCasket", null },
            { "FloatMenuOptionProvider_CarryToShuttle", null },
            { "FloatMenuOptionProvider_CarryingPawn", null },
            { "FloatMenuOptionProvider_Childcare", null },
            { "FloatMenuOptionProvider_CleanRoom", null },
            { "FloatMenuOptionProvider_Deathrest", null },
            { "FloatMenuOptionProvider_DraftedAttack", null },
            { "FloatMenuOptionProvider_DraftedMove", null },
            { "FloatMenuOptionProvider_DraftedRepair", null },
            { "FloatMenuOptionProvider_DraftedTend", null },
            { "FloatMenuOptionProvider_DressOtherPawn", "It asks for the apparel to put on, which RimMCP cannot check first yet." },
            { "FloatMenuOptionProvider_DropEquipment", null },
            { "FloatMenuOptionProvider_EnterMapPortal", null },
            { "FloatMenuOptionProvider_Equip", null },
            { "FloatMenuOptionProvider_ExtinguishFires", null },
            { "FloatMenuOptionProvider_FromLord", null },
            { "FloatMenuOptionProvider_FromThing", null },
            { "FloatMenuOptionProvider_FromZone", null },
            { "FloatMenuOptionProvider_GhoulRest", null },
            { "FloatMenuOptionProvider_HackAncientTerminal", null },
            { "FloatMenuOptionProvider_HandleCorpse", null },
            { "FloatMenuOptionProvider_Ingest", null },
            { "FloatMenuOptionProvider_InvokeArchotech", null },
            { "FloatMenuOptionProvider_LoadCaravan", null },
            { "FloatMenuOptionProvider_LoadOntoPackAnimal", null },
            { "FloatMenuOptionProvider_Mechanitor", null },
            { "FloatMenuOptionProvider_OfferHelp", null },
            { "FloatMenuOptionProvider_OpenThing", null },
            { "FloatMenuOptionProvider_PickUpItem", null },
            { "FloatMenuOptionProvider_PrisonerBloodfeed", null },
            { "FloatMenuOptionProvider_PutOutFireOnPawn", null },
            { "FloatMenuOptionProvider_Relic", null },
            { "FloatMenuOptionProvider_Reload", null },
            { "FloatMenuOptionProvider_RemoveMechlink", null },
            { "FloatMenuOptionProvider_RescuePawn", null },
            { "FloatMenuOptionProvider_ReturnSlaveToBed", null },
            { "FloatMenuOptionProvider_Romance", null },
            { "FloatMenuOptionProvider_StartRitual", RitualWindow },
            { "FloatMenuOptionProvider_Strip", null },
            { "FloatMenuOptionProvider_Trade", "Trading opens the trade window when the pawn reaches the trader; agents cannot trade yet." },
            { "FloatMenuOptionProvider_TransferEntity", "It asks for a holding platform, which RimMCP cannot check first yet." },
            { "FloatMenuOptionProvider_Wear", null },
            { "FloatMenuOptionProvider_WorkGivers", null },
            { "FloatMenuOptionProvider_Xenogerm", null }
        };

        // By the type whose code an option runs, or a base type of one, and for a thing's own options
        // the thing clicked and its components. A comms console's calls run in the faction or ship called.
        private static readonly Dictionary<string, Func<Thing, string>> Origins = new Dictionary<string, Func<Thing, string>>
        {
            { "Building_CommsConsole", thing => CommsConsole },
            { "Faction", thing => CommsConsole },
            { "PassingShip", thing => CommsConsole },
            { "TradeShip", thing => CommsConsole },
            { "Building_StylingStation", thing => "The styling station opens its window when the pawn reaches it; agents cannot use it yet." },
            { "CompPilotConsole", thing => "Piloting opens the gravship's controls when the pawn reaches the console; agents cannot pilot yet." },
            { "Building_VoidMonolith", thing => Find.Anomaly != null && Find.Anomaly.Level > 0 ? "Activating the monolith asks for confirmation when the pawn reaches it; agents cannot answer that yet." : null },
            { "Building_CryptosleepCasket", thing => thing != null && thing.def.building != null && !thing.def.building.isPlayerEjectable && thing.Map != null && thing.Map.mapPawns.FreeColonistsSpawnedOrInPlayerEjectablePodsCount <= 1
                ? "The game asks for confirmation when the last colonist reaches this casket; agents cannot answer that yet." : null },
            { "CompPsylinkable", thing => RitualWindow },
            { "LordToil_BestowingCeremony_Wait", thing => RitualWindow },
            { "CompInteractableRocketswarmLauncher", thing => AsksTarget },
            { "CompNociosphere", thing => AsksTarget },
            { "CompPushable", thing => AsksTarget }
        };

        private const string CommsConsole = "The comms console opens its conversation when the pawn reaches it; agents cannot use it yet.";

        public static readonly Dictionary<string, string> ButtonTypes = new Dictionary<string, string>
        {
            { "Command_Action", null },
            { "Command_ActionWithCooldown", null },
            { "Command_ActionWithLimitedUseCount", null },
            { "Command_ColorIcon", null },
            { "Command_GroupedTempChange", null },
            { "Command_SelectStorage", null },
            { "Command_PsychicRitual", null },
            { "Command_Toggle", null },
            { "Command_Hide", null },
            { "Command_Hide_Plans", null },
            { "Command_Hide_ZoneFishing", null },
            { "Command_Hide_ZoneGrow", null },
            { "Command_Hide_ZoneStockpile", null },
            { "Command_Target", null },
            { "Command_VerbTarget", null },
            { "Command_VerbOwner", null },
            { "Command_Ability", null },
            { "Command_Psycast", null },
            { "Command_AbilitySpeech", null },
            { "Command_AbilityTrial", null },
            { "Command_CallBossgroup", null },
            { "Command_SetPlantToGrow", null },
            { "Command_SetTargetFuelLevel", null },
            { "Command_SetBedOwnerType", null },
            { "Command_SetNeuralSuperchargerAutoUse", null },
            { "Command_BestowerCeremony", RitualWindow },
            { "Command_Ritual", RitualWindow },
            { "Command_LoadToTransporter", "It opens the loading window, which agents cannot use yet." },
            { "Command_Settle", "Settling is chosen on the world map, which agents cannot use yet." }
        };

        // By any type whose code a press runs, or a base type of one: found by following buttons'
        // callbacks into the helpers they call, and ability parts into the windows they open.
        private static readonly Dictionary<string, string> ButtonOrigins = new Dictionary<string, string>
        {
            { "CompAbilityEffect_StartRitual", RitualWindow },
            { "Building_GravEngine", AsksTarget },
            { "Building_OutfitStand", AsksTarget },
            { "Building_VoidMonolith", AsksTarget },
            { "CompHackable", AsksTarget },
            { "CompInteractable", AsksTarget },
            { "CompInteractable_GravshipShieldGenerator", AsksTarget },
            { "CompPlantable", AsksTarget },
            { "CompSealable", AsksTarget },
            { "CompUsable", AsksTarget },
            { "ShipJob_Wait", WorldTarget },
            { "CompLaunchable", WorldTarget },
            { "Caravan", WorldTarget },
            { "Settlement", WorldTarget },
            { "WorldGizmoUtility", WorldTarget },
            { "AnomalyUtility", "It opens the entity codex, which agents cannot use." },
            { "FactionGiftUtility", "It opens the gift window, which agents cannot use yet." },
            { "CompAssignableToPawn", "It opens the owner window; use assign_owner instead." },
            { "BuildCopyCommandUtility", "It starts placing a copy on the player's screen; place copies with place_blueprint." }
        };

        private const string WorldTarget = "It goes on to the world map, which agents cannot use yet.";
        private const string DevelopmentButton = "A development-mode tool, not part of play.";

        private static bool DevelopmentOnly(string label)
        {
            return label != null && label.StartsWith("DEV:", StringComparison.OrdinalIgnoreCase);
        }

        public static string OptionOriginReason(string type, Thing clicked)
        {
            Func<Thing, string> check;
            return Origins.TryGetValue(type, out check) ? check(clicked) : null;
        }

        // Royal permits go on to a target or the world map.
        public static string ButtonOriginReason(string type)
        {
            string reason;
            if (ButtonOrigins.TryGetValue(type, out reason))
            {
                return reason;
            }
            return type.StartsWith("RoyalTitlePermitWorker", StringComparison.Ordinal) ? AsksTarget : null;
        }

        public static ActionOrigin ForOption(FloatMenuOptionProvider provider, FloatMenuOption option, Thing clicked)
        {
            Type type = OriginType(option.action) ?? provider.GetType();
            ActionOrigin origin = Of(new[] { type, provider.GetType() });
            if (DevelopmentOnly(option.Label))
            {
                origin.Unsupported = DevelopmentButton;
                return origin;
            }
            if (origin.IsMod)
            {
                return origin;
            }
            string reason;
            if (Providers.TryGetValue(provider.GetType().Name, out reason) && reason != null)
            {
                origin.Unsupported = reason;
                return origin;
            }
            IEnumerable<Type> code = provider is FloatMenuOptionProvider_FromThing && clicked != null ? new[] { type }.Concat(Owners(clicked)) : new[] { type };
            origin.Unsupported = code.SelectMany(Lineage).Select(each => OptionOriginReason(each.Name, clicked)).FirstOrDefault(found => found != null);
            return origin;
        }

        public static ActionOrigin ForButton(Verse.Command command)
        {
            Command_Ability button = command as Command_Ability;
            return ForButton(command, button == null ? null : button.Ability);
        }

        // Tests give the ability apart: a Command_Ability's static fields need the game's textures.
        internal static ActionOrigin ForButton(Verse.Command command, Ability ability)
        {
            Type type = OriginType(Callback(command)) ?? command.GetType();
            List<Type> code = CodeTypes(command, type, ability).Where(each => each != null).ToList();
            ActionOrigin origin = Of(code);
            if (DevelopmentOnly(command.Label))
            {
                origin.Unsupported = DevelopmentButton;
                return origin;
            }
            if (origin.IsMod)
            {
                return origin;
            }
            if (command is Designator)
            {
                origin.Unsupported = command is Designator_Build || command is Designator_Dropdown
                    ? "It starts placing a building on the player's screen; place it with place_blueprint."
                    : command is Designator_Install
                        ? "It starts placing the building somewhere else on the player's screen; agents cannot reinstall yet."
                        : "A designator button: use designate.";
                return origin;
            }
            string reason;
            if (!ButtonTypes.TryGetValue(command.GetType().Name, out reason))
            {
                reason = "RimMCP does not know this kind of button yet.";
            }
            origin.Unsupported = reason ?? code.SelectMany(Lineage).Select(each => ButtonOriginReason(each.Name)).FirstOrDefault(found => found != null);
            return origin;
        }

        public static Delegate Callback(Verse.Command command)
        {
            Command_Action action = command as Command_Action;
            if (action != null)
            {
                return action.action;
            }
            Command_Toggle toggle = command as Command_Toggle;
            if (toggle != null)
            {
                return toggle.toggleAction;
            }
            Command_Target target = command as Command_Target;
            return target == null ? null : target.action;
        }

        private static IEnumerable<Type> CodeTypes(Verse.Command command, Type callback, Ability ability)
        {
            yield return callback;
            yield return PressedBy(command);
            Command_Target target = command as Command_Target;
            if (target != null && target.targetingParams != null && target.targetingParams.validator != null)
            {
                yield return OriginType(target.targetingParams.validator);
            }
            Command_VerbTarget verb = command as Command_VerbTarget;
            if (verb != null && verb.verb != null)
            {
                yield return verb.verb.GetType();
            }
            if (ability != null)
            {
                yield return ability.GetType();
                if (ability.verb != null)
                {
                    yield return ability.verb.GetType();
                }
                if (ability.comps != null)
                {
                    foreach (AbilityComp comp in ability.comps)
                    {
                        yield return comp.GetType();
                    }
                }
            }
        }

        private static IEnumerable<Type> Owners(Thing thing)
        {
            yield return thing.GetType();
            ThingWithComps owner = thing as ThingWithComps;
            if (owner != null && owner.AllComps != null)
            {
                foreach (ThingComp comp in owner.AllComps)
                {
                    yield return comp.GetType();
                }
            }
        }

        private static IEnumerable<Type> Lineage(Type type)
        {
            for (Type each = type; each != null && each != typeof(object); each = each.BaseType)
            {
                yield return each;
            }
        }

        public static Type PressedBy(Verse.Command command)
        {
            MethodInfo method = command.GetType().GetMethod("ProcessInput", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(UnityEngine.Event) }, null);
            return method == null ? command.GetType() : method.DeclaringType;
        }

        // The outermost type around a lambda's compiler-made class.
        public static Type OriginType(Delegate callback)
        {
            if (callback == null)
            {
                return null;
            }
            Type type = callback.Method.DeclaringType;
            while (type != null && type.DeclaringType != null)
            {
                type = type.DeclaringType;
            }
            return type;
        }

        // RimMCP's own code counts, such as the confirmation it opens before accepting a quest.
        public static bool IsGame(Assembly assembly)
        {
            return assembly == Game || assembly == typeof(GameActions).Assembly;
        }

        public static ActionOrigin Of(IEnumerable<Type> code)
        {
            List<Type> types = code.Where(type => type != null).ToList();
            Type mod = types.FirstOrDefault(type => !IsGame(type.Assembly));
            return new ActionOrigin { Type = types.Count == 0 ? null : types[0].Name, Mod = mod == null ? null : ModName(mod.Assembly) };
        }

        public static string ModName(Assembly assembly)
        {
            ModContentPack pack = LoadedModManager.RunningModsListForReading.FirstOrDefault(mod => mod.assemblies != null && mod.assemblies.loadedAssemblies.Contains(assembly));
            return pack != null ? pack.Name : assembly.GetName().Name;
        }
    }
}
