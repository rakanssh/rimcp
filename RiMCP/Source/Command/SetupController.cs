using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RiMCP.Command
{
    // Drives the game's own new-game screens, visibly. The screens keep their choices in their own
    // fields (the world page's settings, the site page's selected tile, the ideoligion page's
    // selection), so choices are written to the open screen and committed through its own Next, as
    // a click does. A revision counts every change to the setup, by an agent or the player, and a
    // fingerprint of the setup's state shows changes made by anyone else. Main thread only.
    internal static class SetupController
    {
        public const string Scenario = "scenario";
        public const string Storyteller = "storyteller";
        public const string World = "world";
        public const string Site = "site";
        public const string Ideoligion = "ideoligion";
        public const string CustomIdeoligion = "customIdeoligion";
        public const string Pawns = "pawns";
        public const string Unsupported = "unsupported";

        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Dictionary<string, FieldInfo> fieldCache = new Dictionary<string, FieldInfo>();

        private static int revision;
        private static string recorded;
        private static bool lastChangeExternal;

        public static int Revision
        {
            get { return revision; }
        }

        public static bool LastChangeExternal
        {
            get { return lastChangeExternal; }
        }

        // The open setup screen, or null outside setup.
        public static Page OpenPage
        {
            get { return Find.WindowStack == null ? null : Find.WindowStack.Windows.OfType<Page>().LastOrDefault(); }
        }

        public static string StageOf(Page page)
        {
            if (page == null)
            {
                return null;
            }
            if (page is Page_SelectScenario)
            {
                return Scenario;
            }
            if (page is Page_SelectStoryteller)
            {
                return Storyteller;
            }
            if (page is Page_CreateWorldParams)
            {
                return World;
            }
            if (page is Page_SelectStartingSite)
            {
                return Site;
            }
            if (page is Page_ChooseIdeoPreset)
            {
                return Ideoligion;
            }
            if (page is Page_ConfigureIdeo)
            {
                return CustomIdeoligion;
            }
            if (page is Page_ConfigureStartingPawns)
            {
                return Pawns;
            }
            return Unsupported;
        }

        // The open screen when it is the stage a command acts on.
        public static T Require<T>(string stage) where T : Page
        {
            Page page = OpenPage;
            T typed = page as T;
            if (typed == null)
            {
                throw new CommandException(409, page == null
                    ? "No new game is being set up. Start one with start_setup."
                    : "Setup is at the " + StageOf(page) + " screen, not " + stage + ". get_setup shows where it is.");
            }
            return typed;
        }

        // Records a change the agent made: the revision moves on, and the new state is its own.
        public static int Changed()
        {
            revision++;
            lastChangeExternal = false;
            recorded = Fingerprint();
            return revision;
        }

        // Compares the setup with what was last recorded; anything different was changed by someone
        // else, such as the player, and moves the revision on as an external change.
        public static void Observe()
        {
            string now = Fingerprint();
            if (now == recorded)
            {
                return;
            }
            if (recorded != null)
            {
                revision++;
                lastChangeExternal = true;
            }
            recorded = now;
        }

        // Before a command acts: the setup must still be at the revision the agent last read.
        public static void RequireRevision(int? expected)
        {
            Observe();
            if (!expected.HasValue)
            {
                throw new CommandException(400, "Field 'expectedRevision' is required; get_setup gives the current revision (" + revision + ").");
            }
            if (expected.Value != revision)
            {
                throw new CommandException(409, "The setup changed since revision " + expected.Value + (lastChangeExternal ? ", outside this agent (such as by the player)" : "") + "; it is now at revision " + revision + ". Read get_setup and try again.");
            }
        }

        // Before an operation's next step: anything changed outside it since its own last change
        // stops it, with the setup as it now is.
        public static void Continue(Operation operation, int own)
        {
            Observe();
            if (revision != own)
            {
                throw new OperationEnded(Operation.Interrupted, "The setup was changed outside this operation (such as by the player) before its next step, so it stopped. get_setup shows the setup as it is.");
            }
        }

        public static void Update()
        {
            if (OpenPage != null)
            {
                Observe();
            }
        }

        // A screen's own can-proceed check, as its Next button runs it. Some screens act in it: the
        // world screen starts generating there.
        public static bool CanNext(Page page)
        {
            return (bool)Method(page, "CanDoNext").Invoke(page, null);
        }

        public static void DoNext(Page page)
        {
            Method(page, "DoNext").Invoke(page, null);
        }

        public static bool TryBack(Page page)
        {
            if (!(bool)Method(page, "CanDoBack").Invoke(page, null))
            {
                return false;
            }
            Method(page, "DoBack").Invoke(page, null);
            return true;
        }

        public static object Get(object target, string field)
        {
            return Field(target.GetType(), field).GetValue(target);
        }

        public static void Set(object target, string field, object value)
        {
            Field(target.GetType(), field).SetValue(target, value);
        }

        // A screen's field by name. A game version that renamed it cannot be driven: the error says
        // so, and the player carries on in game.
        private static FieldInfo Field(Type type, string name)
        {
            string key = type.FullName + "." + name;
            FieldInfo field;
            if (!fieldCache.TryGetValue(key, out field))
            {
                for (Type current = type; current != null && field == null; current = current.BaseType)
                {
                    field = current.GetField(name, Fields | BindingFlags.DeclaredOnly);
                }
                fieldCache[key] = field;
            }
            if (field == null)
            {
                throw new CommandException(501, "This game version's " + type.Name + " has no " + name + ", so RimMCP cannot drive this screen; continue it in game.");
            }
            return field;
        }

        private static MethodInfo Method(Page page, string name)
        {
            for (Type current = page.GetType(); current != null; current = current.BaseType)
            {
                MethodInfo method = current.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
                if (method != null)
                {
                    return method;
                }
            }
            throw new CommandException(501, "This game version's " + page.GetType().Name + " has no " + name + ", so RimMCP cannot drive this screen; continue it in game.");
        }

        // Everything a setup command reads or writes, so a change by anyone shows as a difference.
        private static string Fingerprint()
        {
            Page page = OpenPage;
            if (page == null)
            {
                return "none";
            }
            List<string> parts = new List<string> { StageOf(page), RuntimeHelpers.GetHashCode(page).ToString() };
            GameInitData init = Current.Game == null ? null : Current.Game.InitData;
            try
            {
                if (page is Page_SelectStoryteller)
                {
                    parts.Add(Name(Get(page, "storyteller")));
                    parts.Add(Name(Get(page, "difficulty")));
                    parts.Add(init == null ? "-" : init.permadeathChosen + "/" + init.permadeath);
                }
                else if (page is Page_CreateWorldParams)
                {
                    foreach (string field in new[] { "seedString", "planetCoverage", "rainfall", "temperature", "population", "landmarkDensity", "pollution" })
                    {
                        parts.Add(Convert.ToString(Get(page, field)));
                    }
                    List<FactionDef> factions = Get(page, "factions") as List<FactionDef>;
                    parts.Add(factions == null ? "-" : string.Join(",", factions.Select(def => def.defName).ToArray()));
                }
                else if (page is Page_SelectStartingSite)
                {
                    parts.Add(Find.WorldInterface == null ? "-" : Find.WorldInterface.SelectedTile.ToString());
                    parts.Add(init == null ? "-" : init.mapSize + "/" + init.startingSeason);
                }
                else if (page is Page_ChooseIdeoPreset)
                {
                    parts.Add(Convert.ToString(Get(page, "presetSelection")));
                    parts.Add(Name(Get(page, "selectedIdeo")));
                    parts.Add(Name(Get(page, "selectedStructure")));
                }
                else if (page is Page_ConfigureIdeo)
                {
                    parts.Add(IdeoEditor.Fingerprint(((Page_ConfigureIdeo)page).ideo));
                    parts.Add(Find.WindowStack.IsOpen(typeof(Dialog_ChooseMemes)).ToString());
                }
                else if (page is Page_ConfigureStartingPawns && init != null)
                {
                    parts.Add(init.startingPawnCount.ToString());
                    parts.AddRange(init.startingAndOptionalPawns.Select(pawn => pawn == null ? "-" : pawn.ThingID + ":" + pawn.Name));
                }
            }
            catch (CommandException)
            {
                parts.Add("unreadable");
            }
            return string.Join("|", parts.ToArray());
        }

        private static string Name(object def)
        {
            Def typed = def as Def;
            return typed == null ? "-" : typed.defName;
        }
    }
}
