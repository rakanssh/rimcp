using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using RimWorld;
using RiMCP.Read;
using Verse;

namespace RiMCP.Command
{
    // One option an Add button on the custom ideoligion screen offers: a precept def, with the ritual
    // pattern, thing, weapon pair, or xenotype the option fills it with. Reason is why the screen
    // shows it but will not take it.
    internal sealed class PreceptOption
    {
        public string Section;
        public PreceptDef Def;
        public RitualPatternDef Pattern;
        public ThingDef Thing;
        public WeaponClassPairDef Weapons;
        public XenotypeDef Xenotype;
        public string Label;
        public string Reason;

        public object ToDto()
        {
            return Dto.Obj(
                Dto.Field("section", Section),
                Dto.Field("precept", Def.defName),
                Dto.Field("pattern", Pattern == null ? null : Pattern.defName),
                Dto.Field("thing", Thing == null ? null : Thing.defName),
                Dto.Field("weapons", Weapons == null ? null : Weapons.defName),
                Dto.Field("xenotype", Xenotype == null ? null : Xenotype.defName),
                Dto.Field("label", Label),
                Dto.Field("reason", Reason));
        }
    }

    // A custom ideoligion on the setup's custom ideoligion screen, edited as the screen's controls
    // edit it and with their checks: the meme dialog's own checks for memes, and for precepts the
    // options the screen's Add buttons and precept boxes offer (IdeoUIUtility.AddPrecept,
    // Precept.DrawPreceptBox). Main thread only.
    internal static class IdeoEditor
    {
        public const int MaxSymbolLength = 40;
        public const int MaxPreceptNameLength = 32;
        private const int MaxRituals = 6;
        private const IdeoEditMode EditMode = IdeoEditMode.GameStart;
        private static readonly Regex ValidName = new Regex("^[\\p{L}0-9 '\\-]*$");
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        // The screen's precept sections, in its order.
        public static readonly string[] Sections = { "precepts", "roles", "rituals", "buildings", "relics", "weapons", "animals", "xenotypes", "apparel" };

        public static string SectionOf(PreceptDef def)
        {
            Type type = def.preceptClass;
            if (type == typeof(Precept))
            {
                return "precepts";
            }
            if (typeof(Precept_Role).IsAssignableFrom(type))
            {
                return "roles";
            }
            if (type == typeof(Precept_Ritual))
            {
                return "rituals";
            }
            if (type == typeof(Precept_Building) || type == typeof(Precept_RitualSeat))
            {
                return "buildings";
            }
            if (type == typeof(Precept_Relic))
            {
                return "relics";
            }
            if (type == typeof(Precept_Weapon))
            {
                return "weapons";
            }
            if (type == typeof(Precept_Animal))
            {
                return "animals";
            }
            if (type == typeof(Precept_Xenotype))
            {
                return ModsConfig.BiotechActive ? "xenotypes" : null;
            }
            if (type == typeof(Precept_Apparel))
            {
                return "apparel";
            }
            return null;
        }

        // The precept's label as its box's tooltip titles it, on one line.
        public static string Label(Precept precept)
        {
            return precept.TipLabel.StripTags().Split('\n')[0].Trim();
        }

        public static bool ValidSymbol(string text, int maxLength)
        {
            return text != null && text.Length <= maxLength && ValidName.IsMatch(text);
        }

        // ---- Memes ----

        // Problems the meme dialog would report for these memes, with its reasons; none means its
        // accept button takes them. A new ideoligion is checked as the dialogs it opens first.
        public static List<string> CheckMemes(Ideo ideo, MemeDef structure, List<MemeDef> normal, bool initial)
        {
            List<string> problems = new List<string>();
            List<MemeDef> wanted = new List<MemeDef> { structure };
            wanted.AddRange(normal);
            if (structure.category != MemeCategory.Structure)
            {
                problems.Add(structure.defName + " is not a structure meme; give it in memes.");
            }
            foreach (MemeDef meme in normal.Where(meme => meme.category != MemeCategory.Normal))
            {
                problems.Add(meme.defName + " is a structure meme; give it as structure.");
            }
            if (problems.Count > 0)
            {
                return problems;
            }
            Dialog_ChooseMemes structures = MemeDialog(ideo, MemeCategory.Structure, initial, wanted);
            Dialog_ChooseMemes normals = MemeDialog(ideo, MemeCategory.Normal, initial, wanted);
            foreach (MemeDef meme in wanted.Where(meme => !ideo.memes.Contains(meme)))
            {
                if (!(bool)Call(meme.category == MemeCategory.Structure ? structures : normals, "CanUseMeme", meme))
                {
                    problems.Add(meme.defName + " " + WhyNotUsable(ideo, meme) + ".");
                }
            }
            foreach (MemeDef meme in ideo.memes.Where(meme => !wanted.Contains(meme)))
            {
                AcceptanceReport removable = (AcceptanceReport)Call(meme.category == MemeCategory.Structure ? structures : normals, "CanRemoveMeme", meme);
                if (!removable.Accepted)
                {
                    problems.Add(string.IsNullOrWhiteSpace(removable.Reason) ? meme.defName + " cannot be removed." : removable.Reason);
                }
            }
            Pair<MemeDef, MemeDef> clash = (Pair<MemeDef, MemeDef>)Call(normals, "GetFirstIncompatibleMemePair");
            if (clash != default(Pair<MemeDef, MemeDef>))
            {
                problems.Add("MessageIncompatibleMemes".Translate(clash.First, clash.Second).Resolve());
            }
            IntRange range = (IntRange)Property(normals, "MemeCountRangeAbsolute");
            if (normal.Count < range.min)
            {
                problems.Add("MessageNotEnoughMemes".Translate(range.min).Resolve());
            }
            if (normal.Count > range.max)
            {
                problems.Add("MessageTooManyMemes".Translate(range.max).Resolve());
            }
            return problems;
        }

        // Changes the memes of an ideoligion that has them, as the meme dialog's accept does without
        // its warning that precepts are regenerated (the agent asked for the change).
        public static void ApplyMemes(Ideo ideo, MemeDef structure, List<MemeDef> normal)
        {
            List<MemeDef> wanted = new List<MemeDef> { structure };
            wanted.AddRange(normal);
            bool structureChanged = !ideo.memes.Contains(structure);
            Call(MemeDialog(ideo, structureChanged ? MemeCategory.Structure : MemeCategory.Normal, false, wanted), "DoAcceptChanges");
        }

        // Answers a meme dialog the game opened, as its accept button does. Null when it closed.
        public static string AnswerMemeDialog(Dialog_ChooseMemes dialog, List<MemeDef> memes)
        {
            List<MemeDef> chosen = (List<MemeDef>)Field(dialog, "newMemes");
            chosen.Clear();
            chosen.AddRange(memes);
            Call(dialog, "TryAccept");
            return Find.WindowStack.IsOpen(dialog) ? "The meme dialog did not accept these memes; see the message in game." : null;
        }

        private static Dialog_ChooseMemes MemeDialog(Ideo ideo, MemeCategory category, bool initial, List<MemeDef> memes)
        {
            Dialog_ChooseMemes dialog = new Dialog_ChooseMemes(ideo, category, initial);
            List<MemeDef> chosen = (List<MemeDef>)Field(dialog, "newMemes");
            chosen.Clear();
            chosen.AddRange(memes);
            return dialog;
        }

        // The meme dialog says only that a meme is not offered; this says which of its rules applies.
        private static string WhyNotUsable(Ideo ideo, MemeDef meme)
        {
            if (meme.hiddenInChooseMemes)
            {
                return "is not offered by the meme dialog";
            }
            if (ideo.Fluid && meme.category == MemeCategory.Normal && !IdeoUtility.IsMemeAllowedForInitialFluidIdeo(meme))
            {
                return "cannot be a new fluid ideoligion's meme";
            }
            return "is not allowed for a faction with this ideoligion";
        }

        // Every meme the dialogs offer, with whether it can be added to the ideoligion as it is, or
        // why not, and for its memes whether they can be removed.
        public static object[] MemeCatalog(Ideo ideo)
        {
            Dialog_ChooseMemes structures = MemeDialog(ideo, MemeCategory.Structure, false, ideo.memes);
            Dialog_ChooseMemes normals = MemeDialog(ideo, MemeCategory.Normal, false, ideo.memes);
            return DefDatabase<MemeDef>.AllDefs.Where(meme => !meme.hiddenInChooseMemes && (meme.category == MemeCategory.Structure || meme.category == MemeCategory.Normal))
                .OrderBy(meme => meme.category).ThenBy(meme => meme.defName)
                .Select(meme =>
                {
                    Dialog_ChooseMemes dialog = meme.category == MemeCategory.Structure ? structures : normals;
                    string reason = null;
                    if (ideo.memes.Contains(meme))
                    {
                        AcceptanceReport removable = (AcceptanceReport)Call(dialog, "CanRemoveMeme", meme);
                        reason = removable.Accepted ? null : (string.IsNullOrWhiteSpace(removable.Reason) ? "cannot be removed" : removable.Reason);
                        return (object)Dto.Obj(
                            Dto.Field("meme", meme.defName),
                            Dto.Field("category", meme.category.ToString()),
                            Dto.Field("label", meme.LabelCap.ToString()),
                            Dto.Field("chosen", true),
                            Dto.Field("cannotRemove", reason));
                    }
                    if (!(bool)Call(dialog, "CanUseMeme", meme))
                    {
                        reason = WhyNotUsable(ideo, meme);
                    }
                    else if (meme.category == MemeCategory.Normal)
                    {
                        MemeDef clash = ideo.memes.FirstOrDefault(other => other.exclusionTags.Any(meme.exclusionTags.Contains));
                        reason = clash == null ? null : "conflicts with " + clash.defName;
                    }
                    return (object)Dto.Obj(
                        Dto.Field("meme", meme.defName),
                        Dto.Field("category", meme.category.ToString()),
                        Dto.Field("label", meme.LabelCap.ToString()),
                        Dto.Field("reason", reason));
                }).ToArray();
        }

        // ---- Adding precepts ----

        // The options every Add button offers, as IdeoUIUtility.AddPrecept builds them.
        public static List<PreceptOption> AddOptions(Ideo ideo)
        {
            List<PreceptOption> all = new List<PreceptOption>();
            foreach (string section in Sections)
            {
                all.AddRange(AddOptions(ideo, section));
            }
            return all;
        }

        private static List<PreceptOption> AddOptions(Ideo ideo, string section)
        {
            List<PreceptOption> options = new List<PreceptOption>();
            List<PreceptDef> defs = DefDatabase<PreceptDef>.AllDefs.Where(def => SectionOf(def) == section).ToList();
            if (defs.Count == 0)
            {
                return options;
            }
            bool ritualsListed = defs.Any(def => def.preceptClass == typeof(Precept_Ritual) && def.visible && IdeoUIUtility.CanListPrecept(ideo, def, EditMode).Accepted);
            int rituals = ideo.PreceptsListForReading.Count(precept => precept is Precept_Ritual && precept.def.visible);
            HashSet<RitualPatternDef> patterns = new HashSet<RitualPatternDef>();
            List<ThingDef> used = ideo.PreceptsListForReading.OfType<Precept_ThingDef>().Select(precept => precept.ThingDef).ToList();
            foreach (PreceptDef def in defs)
            {
                AcceptanceReport listed = IdeoUIUtility.CanListPrecept(ideo, def, EditMode);
                if (!listed.Accepted && string.IsNullOrWhiteSpace(listed.Reason))
                {
                    continue;
                }
                string listedReason = listed.Accepted ? null : listed.Reason;
                int count = ideo.GetPreceptCountOfDef(def);
                int max = def.maxCount;
                if (def.preceptInstanceCountCurve != null)
                {
                    max = Math.Max(max, (int)Math.Round(def.preceptInstanceCountCurve.Last().y));
                }
                if (count < max || def.ignoreLimitsInEditMode)
                {
                    List<ThingDef> allowed = def.useChoicesFromBuildingDefs ? def.Worker.ThingDefsForIdeo(ideo, null).Select(chance => chance.def).ToList() : null;
                    if (allowed == null || allowed.Count == 0)
                    {
                        if (def.preceptClass == typeof(Precept_Weapon))
                        {
                            AddWeaponOptions(ideo, def, section, listedReason, options);
                        }
                        else if (def.preceptClass == typeof(Precept_Xenotype))
                        {
                            AddXenotypeOptions(ideo, def, section, listedReason, options);
                        }
                        else
                        {
                            AddOption(ideo, def, null, section, listedReason, patterns, options);
                        }
                    }
                    else
                    {
                        AddThingOptions(ideo, def, section, listedReason, allowed, used, options);
                    }
                    string group = def.ritualPatternBase == null ? null : def.ritualPatternBase.patternGroupTag;
                    if (!string.IsNullOrEmpty(group))
                    {
                        foreach (RitualPatternDef pattern in DefDatabase<RitualPatternDef>.AllDefs.Where(pattern => pattern.patternGroupTag == group && pattern != def.ritualPatternBase))
                        {
                            AddOption(ideo, def, pattern, section, listedReason, patterns, options);
                        }
                    }
                }
                else if (count >= max && def.issue != null && def.issue.allowMultiplePrecepts && def.preceptClass != typeof(Precept_Relic))
                {
                    options.Add(new PreceptOption { Section = section, Def = def, Label = def.LabelCap, Reason = "MaxPreceptCount".Translate(max).Resolve() });
                }
            }
            if (section == "rituals")
            {
                if (rituals < MaxRituals)
                {
                    foreach (MemeDef meme in ideo.memes.Where(meme => !meme.replacementPatterns.NullOrEmpty()))
                    {
                        foreach (PreceptDef def in defs.Where(def => def.ritualPatternBase != null && !def.ritualPatternBase.tags.NullOrEmpty() && meme.replaceRitualsWithTags.Any(def.ritualPatternBase.tags.Contains) && !def.classicModeOnly))
                        {
                            foreach (RitualPatternDef pattern in meme.replacementPatterns.Where(pattern => pattern.CanFactionUse(IdeoUIUtility.FactionForRandomization(ideo))))
                            {
                                AddOption(ideo, def, pattern, section, null, patterns, options);
                            }
                        }
                    }
                }
                else if (ritualsListed)
                {
                    options.Clear();
                }
            }
            return options;
        }

        // IdeoUIUtility.AddPreceptOption.
        private static void AddOption(Ideo ideo, PreceptDef def, RitualPatternDef patternDef, string section, string listedReason, HashSet<RitualPatternDef> patterns, List<PreceptOption> options)
        {
            if (!def.visibleOnAddFloatMenu)
            {
                return;
            }
            RitualPatternDef pattern = patternDef ?? def.ritualPatternBase;
            if (pattern != null && (!pattern.CanFactionUse(IdeoUIUtility.FactionForRandomization(ideo)) || patterns.Contains(pattern)))
            {
                return;
            }
            // The native check adds to a scratch list that its own caller clears first.
            System.Collections.IList scratch = typeof(IdeoUIUtility).GetField("tempRequiredMemes", Members)?.GetValue(null) as System.Collections.IList;
            if (scratch != null)
            {
                scratch.Clear();
            }
            AcceptanceReport addable = (AcceptanceReport)CallStatic(typeof(IdeoUIUtility), "CanAddPrecept", def, pattern, ideo);
            string reason = null;
            if (!addable.Accepted)
            {
                reason = string.IsNullOrWhiteSpace(addable.Reason) ? "cannot be added" : addable.Reason;
            }
            else if (def.issue != null && ideo.HasMaxPreceptsForIssue(def.issue))
            {
                return;
            }
            string label = patternDef != null && !string.IsNullOrEmpty(patternDef.shortDescOverride) ? patternDef.shortDescOverride.CapitalizeFirst() : def.LabelCap.ToString();
            if (def.preceptClass == typeof(Precept) || (def.issue != null && def.issue.forceWriteLabelInPreceptFloatMenuOption))
            {
                label = def.issue.LabelCap + ": " + label;
            }
            options.Add(new PreceptOption { Section = section, Def = def, Pattern = pattern, Label = label, Reason = reason ?? listedReason });
            if (pattern != null)
            {
                patterns.Add(pattern);
            }
        }

        private static void AddThingOptions(Ideo ideo, PreceptDef def, string section, string listedReason, List<ThingDef> allowed, List<ThingDef> used, List<PreceptOption> options)
        {
            List<ThingDef> things = def.Worker.ThingDefs
                .OrderBy(chance => def.Worker.GetThingOrder(chance)).ThenByDescending(chance => chance.chance).ThenBy(chance => chance.def.LabelCap.ToString())
                .Select(chance => chance.def).ToList();
            if (def.preceptClass == typeof(Precept_Building))
            {
                foreach (ThingDef building in ideo.memes.Where(meme => !meme.consumableBuildings.NullOrEmpty()).SelectMany(meme => meme.consumableBuildings))
                {
                    if (!allowed.Contains(building))
                    {
                        allowed.Add(building);
                    }
                    if (!things.Contains(building))
                    {
                        things.Add(building);
                    }
                }
            }
            foreach (ThingDef thing in things)
            {
                if (!def.canUseAlreadyUsedThingDef && used.Contains(thing))
                {
                    continue;
                }
                string label = def.preceptClass == typeof(Precept_Apparel) ? thing.LabelCap + ": " + def.LabelCap : thing.LabelCap.ToString();
                if (def.Worker.ShouldSkipThing(ideo, thing) || !allowed.Contains(thing))
                {
                    AcceptanceReport usable = def.Worker.CanUse(thing, ideo, null);
                    if (!usable.Accepted)
                    {
                        options.Add(new PreceptOption { Section = section, Def = def, Thing = thing, Label = label, Reason = string.IsNullOrWhiteSpace(usable.Reason) ? "cannot be used" : usable.Reason });
                    }
                    continue;
                }
                options.Add(new PreceptOption { Section = section, Def = def, Thing = thing, Label = label, Reason = listedReason });
            }
        }

        private static void AddWeaponOptions(Ideo ideo, PreceptDef def, string section, string listedReason, List<PreceptOption> options)
        {
            foreach (WeaponClassPairDef pair in DefDatabase<WeaponClassPairDef>.AllDefs)
            {
                bool taken = ideo.PreceptsListForReading.OfType<Precept_Weapon>().Any(precept =>
                    (precept.noble == pair.first && precept.despised == pair.second) || (precept.noble == pair.second && precept.despised == pair.first));
                if (!taken)
                {
                    options.Add(new PreceptOption { Section = section, Def = def, Weapons = pair, Label = pair.first.LabelCap + " / " + pair.second.LabelCap, Reason = listedReason });
                }
            }
        }

        private static void AddXenotypeOptions(Ideo ideo, PreceptDef def, string section, string listedReason, List<PreceptOption> options)
        {
            foreach (XenotypeDef xenotype in DefDatabase<XenotypeDef>.AllDefs.OrderBy(xenotype => -xenotype.displayPriority))
            {
                if (!ideo.PreceptsListForReading.OfType<Precept_Xenotype>().Any(precept => precept.xenotype == xenotype))
                {
                    options.Add(new PreceptOption { Section = section, Def = def, Xenotype = xenotype, Label = xenotype.LabelCap, Reason = listedReason });
                }
            }
        }

        // Adds a precept as the option's menu entry does.
        public static Precept Add(Ideo ideo, PreceptOption option)
        {
            Precept precept = PreceptMaker.MakePrecept(option.Def);
            if (option.Weapons != null)
            {
                Precept_Weapon weapon = (Precept_Weapon)precept;
                weapon.noble = option.Weapons.first;
                weapon.despised = option.Weapons.second;
                ideo.AddPrecept(weapon);
                weapon.Init(ideo);
            }
            else if (option.Xenotype != null)
            {
                ((Precept_Xenotype)precept).xenotype = option.Xenotype;
                ideo.AddPrecept(precept, true);
            }
            else if (option.Thing != null)
            {
                Precept_Apparel apparel = precept as Precept_Apparel;
                if (apparel != null)
                {
                    apparel.apparelDef = option.Thing;
                }
                ideo.AddPrecept(precept, true);
                Precept_ThingDef thing = precept as Precept_ThingDef;
                if (thing != null)
                {
                    thing.ThingDef = option.Thing;
                    thing.RegenerateName();
                }
            }
            else
            {
                ideo.AddPrecept(precept, true, null, option.Pattern ?? option.Def.ritualPatternBase);
            }
            ideo.anyPreceptEdited = true;
            return precept;
        }

        // ---- A precept's box ----

        // Remove, as the box offers it.
        public static string WhyNotRemovable(Ideo ideo, Precept precept)
        {
            if (!precept.def.canRemoveInUI || (precept.def.issue != null && precept.def.issue.HasDefaultPrecept))
            {
                return precept.def.preceptClass == typeof(Precept) ? "The screen offers no Remove for this precept; replace it with another option for its issue." : "The screen offers no Remove for this precept.";
            }
            MemeDef meme = ideo.GetMemeThatRequiresPrecept(precept.def);
            return meme == null ? null : ("CannotRemove".Translate() + ": " + "RequiredByMeme".Translate(meme.label)).Resolve();
        }

        // The other options for a precept's issue, as its box lists them; only for precepts by issue.
        public static List<PreceptOption> Replacements(Ideo ideo, Precept precept)
        {
            List<PreceptOption> options = new List<PreceptOption>();
            if (precept.GetType() != typeof(Precept) || precept.CanRegenerate || precept.def.issue == null)
            {
                return options;
            }
            foreach (PreceptDef def in DefDatabase<PreceptDef>.AllDefs.Where(def => def != precept.def && def.issue == precept.def.issue).OrderBy(def => def.displayOrderInIssue))
            {
                AcceptanceReport listed = IdeoUIUtility.CanListPrecept(ideo, def, EditMode);
                if (listed.Accepted || !string.IsNullOrWhiteSpace(listed.Reason))
                {
                    options.Add(new PreceptOption { Section = "precepts", Def = def, Label = def.LabelCap, Reason = listed.Accepted ? null : listed.Reason });
                }
            }
            return options;
        }

        public static Precept Replace(Ideo ideo, Precept precept, PreceptDef with)
        {
            Precept replacement = PreceptMaker.MakePrecept(with);
            ideo.AddPrecept(replacement, true);
            ideo.RemovePrecept(precept, true);
            ideo.anyPreceptEdited = true;
            return replacement;
        }

        public static void Regenerate(Ideo ideo, Precept precept)
        {
            precept.Regenerate(ideo, null);
            ideo.anyPreceptEdited = true;
        }

        // The precept dialog's name field: the name, and for the leader the male title it doubles as.
        public static void Rename(Ideo ideo, Precept precept, string name)
        {
            precept.SetName(name);
            if (precept.def.leaderRole)
            {
                ideo.leaderTitleMale = name;
            }
            foreach (Precept other in ideo.PreceptsListForReading)
            {
                other.ClearTipCache();
            }
        }

        // ---- Culture, styles, randomizing ----

        // The culture icon's menu.
        public static void SetCulture(Ideo ideo, CultureDef culture)
        {
            ideo.culture = culture;
            ideo.foundation.RandomizeStyles();
            ideo.style.RecalculateAvailableStyleItems();
            IdeoFoundation_Deity deity = ideo.foundation as IdeoFoundation_Deity;
            if (deity != null)
            {
                deity.GenerateDeities();
            }
            ideo.RegenerateDescription(true);
        }

        public static IEnumerable<StyleCategoryDef> Styles()
        {
            return DefDatabase<StyleCategoryDef>.AllDefs.Where(style => !style.fixedIdeoOnly);
        }

        // The style icons: up to three, first the most dominant.
        public static void SetStyles(Ideo ideo, List<StyleCategoryDef> styles)
        {
            ideo.thingStyleCategories.Clear();
            for (int i = 0; i < styles.Count; i++)
            {
                ideo.thingStyleCategories.Add(new ThingStyleCategoryWithPriority(styles[i], 3 - i));
            }
            ideo.SortStyleCategories();
            ideo.style.RecalculateAvailableStyleItems();
        }

        // The screen's Randomize all, and Randomize precepts.
        public static void Randomize(Ideo ideo, bool all)
        {
            if (all)
            {
                ideo.foundation.Init(new IdeoGenerationParms(IdeoUIUtility.FactionForRandomization(ideo), false, null, null, null, false, false, ideo.Fluid));
                return;
            }
            ideo.foundation.RandomizePrecepts(true, new IdeoGenerationParms(IdeoUIUtility.FactionForRandomization(ideo)));
            ideo.RegenerateDescription();
            ideo.anyPreceptEdited = false;
        }

        // ---- Leaving the screen ----

        // What stops the screen's Next, with its own message (Page_ConfigureIdeo.CanDoNext).
        public static string Blocker(Ideo ideo)
        {
            if (ideo.name.NullOrEmpty())
            {
                return "MessageIdeoNameCantBeEmpty".Translate().Resolve();
            }
            Pair<Precept, Precept> pair = ideo.FirstIncompatiblePreceptPair();
            if (pair != default(Pair<Precept, Precept>))
            {
                return "MessageIdeoIncompatiblePrecepts".Translate(pair.First.Label.Named("PRECEPT1"), pair.Second.Label.Named("PRECEPT2")).CapitalizeFirst().Resolve();
            }
            Tuple<Precept_Ritual, List<string>> ritual = ideo.FirstRitualMissingTarget();
            if (ritual != null)
            {
                return "MessageRitualMissingTarget".Translate(ritual.Item1.LabelCap.Named("PRECEPT")).Resolve() + ": " + ritual.Item2.ToCommaList().CapitalizeFirst() + ".";
            }
            Precept_Building building = ideo.FirstConsumableBuildingMissingRitual();
            if (building != null)
            {
                return "MessageBuildingMissingRitual".Translate(building.LabelCap.Named("PRECEPT")).Resolve();
            }
            return null;
        }

        // ---- Reading ----

        public static object Describe(Ideo ideo, bool withOptions)
        {
            Precept warned = ideo.FirstPreceptWithWarning();
            string warning = null;
            if (warned != null)
            {
                string shortText;
                string description;
                warned.GetPlayerWarning(out shortText, out description);
                warning = shortText.StripTags();
            }
            return Dto.Obj(
                Dto.Field("name", ideo.name),
                Dto.Field("adjective", ideo.adjective),
                Dto.Field("memberName", ideo.memberName),
                Dto.Field("fluid", ideo.Fluid),
                Dto.Field("culture", ideo.culture == null ? null : ideo.culture.defName),
                Dto.Field("styles", ideo.thingStyleCategories.Select(style => style.category.defName).ToArray()),
                Dto.Field("structure", ideo.memes.Where(meme => meme.category == MemeCategory.Structure).Select(meme => meme.defName).FirstOrDefault()),
                Dto.Field("memes", ideo.memes.Where(meme => meme.category == MemeCategory.Normal).Select(meme => meme.defName).ToArray()),
                Dto.Field("precepts", ideo.PreceptsListForReading.Where(precept => precept.def.visible && SectionOf(precept.def) != null).Select(precept => DescribePrecept(ideo, precept)).ToArray()),
                Dto.Field("warning", warning),
                Dto.Field("blocksNext", Blocker(ideo)),
                Dto.Field("memeCatalog", MemeCatalog(ideo)),
                Dto.Field("cultures", DefDatabase<CultureDef>.AllDefs.OrderBy(culture => culture.defName).Select(culture => culture.defName).ToArray()),
                Dto.Field("styleOptions", Styles().Select(style => style.defName).ToArray()),
                Dto.Field("addOptions", withOptions ? AddOptions(ideo).Select(option => option.ToDto()).ToArray() : null));
        }

        private static object DescribePrecept(Ideo ideo, Precept precept)
        {
            Precept_ThingDef thing = precept as Precept_ThingDef;
            Precept_Apparel apparel = precept as Precept_Apparel;
            Precept_Weapon weapon = precept as Precept_Weapon;
            Precept_Xenotype xenotype = precept as Precept_Xenotype;
            MemeDef requiredBy = ideo.GetMemeThatRequiresPrecept(precept.def);
            List<PreceptOption> replacements = Replacements(ideo, precept);
            return Dto.Obj(
                Dto.Field("id", precept.Id),
                Dto.Field("section", SectionOf(precept.def)),
                Dto.Field("precept", precept.def.defName),
                Dto.Field("issue", precept.def.issue == null ? null : precept.def.issue.defName),
                Dto.Field("label", Label(precept)),
                Dto.Field("name", precept.def.preceptClass == typeof(Precept) ? null : precept.LabelCap.ToString()),
                Dto.Field("thing", thing != null && thing.ThingDef != null ? thing.ThingDef.defName : apparel != null && apparel.apparelDef != null ? apparel.apparelDef.defName : null),
                Dto.Field("noble", weapon == null || weapon.noble == null ? null : weapon.noble.defName),
                Dto.Field("despised", weapon == null || weapon.despised == null ? null : weapon.despised.defName),
                Dto.Field("xenotype", xenotype == null || xenotype.xenotype == null ? null : xenotype.xenotype.defName),
                Dto.Field("requiredByMeme", requiredBy == null ? null : requiredBy.defName),
                Dto.Field("cannotRemove", WhyNotRemovable(ideo, precept)),
                Dto.Field("canRegenerate", precept.CanRegenerate ? (object)true : null),
                Dto.Field("replaceWith", replacements.Count == 0 ? null : replacements.Select(option => (object)Dto.Obj(
                    Dto.Field("precept", option.Def.defName),
                    Dto.Field("label", option.Label),
                    Dto.Field("reason", option.Reason))).ToArray()));
        }

        // Everything an edit or the player can change, so a change by anyone shows as a difference.
        public static string Fingerprint(Ideo ideo)
        {
            if (ideo == null)
            {
                return "-";
            }
            return string.Join("|", new[]
            {
                RuntimeHelpers.GetHashCode(ideo).ToString(),
                ideo.name, ideo.adjective, ideo.memberName,
                ideo.Fluid.ToString(),
                ideo.culture == null ? "-" : ideo.culture.defName,
                string.Join(",", ideo.memes.Select(meme => meme.defName).ToArray()),
                string.Join(",", ideo.thingStyleCategories.Select(style => style.category.defName).ToArray()),
                string.Join(",", ideo.PreceptsListForReading.Select(PreceptPrint).ToArray())
            });
        }

        private static string PreceptPrint(Precept precept)
        {
            Precept_ThingDef thing = precept as Precept_ThingDef;
            Precept_Apparel apparel = precept as Precept_Apparel;
            Precept_Weapon weapon = precept as Precept_Weapon;
            Precept_Xenotype xenotype = precept as Precept_Xenotype;
            Precept_Ritual ritual = precept as Precept_Ritual;
            return precept.Id + ":" + precept.def.defName + ":" + precept.LabelCap
                + (thing != null && thing.ThingDef != null ? ":" + thing.ThingDef.defName + "/" + StylePrint(precept, thing.ThingDef) : "")
                + (apparel != null && apparel.apparelDef != null ? ":" + apparel.apparelDef.defName : "")
                + (weapon != null ? ":" + (weapon.noble == null ? "-" : weapon.noble.defName) + "/" + (weapon.despised == null ? "-" : weapon.despised.defName) : "")
                + (xenotype != null && xenotype.xenotype != null ? ":" + xenotype.xenotype.defName : "")
                // What the precept dialog edits: a ritual's anytime start, reward, and date, and the
                // apparel a role asks for.
                + (ritual != null ? ":" + ritual.isAnytime + "/" + (ritual.attachableOutcomeEffect == null ? "-" : ritual.attachableOutcomeEffect.defName) + "/"
                    + string.Join(",", (ritual.obligationTriggers ?? new List<RitualObligationTrigger>()).OfType<RitualObligationTrigger_Date>().Select(trigger => trigger.triggerDaysSinceStartOfYear.ToString()).ToArray()) : "")
                + ":" + ApparelPrint(precept.ApparelRequirements);
        }

        private static string StylePrint(Precept precept, ThingDef thing)
        {
            StyleCategoryPair style = precept.ideo == null || precept.ideo.style == null ? null : precept.ideo.style.StyleForThingDef(thing, precept);
            return style == null ? "-" : (style.category == null ? "-" : style.category.defName) + "/" + (style.styleDef == null ? "-" : style.styleDef.defName);
        }

        private static string ApparelPrint(List<PreceptApparelRequirement> requirements)
        {
            if (requirements == null)
            {
                return "-";
            }
            return string.Join(";", requirements.Select(each => each == null || each.requirement == null ? "-"
                : string.Join(",", (each.requirement.requiredDefs ?? new List<ThingDef>()).Select(def => def.defName).ToArray()) + "|"
                  + string.Join(",", (each.requirement.requiredTags ?? new List<string>()).ToArray()) + "|"
                  + string.Join(",", (each.requirement.bodyPartGroupsMatchAny ?? new List<BodyPartGroupDef>()).Select(def => def.defName).ToArray())).ToArray());
        }

        // ---- The game's private members ----

        private static object Field(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name, Members);
            if (field == null)
            {
                throw new CommandException(501, "This game version's " + target.GetType().Name + " has no " + name + ", so RimMCP cannot edit ideoligions here; edit it in game.");
            }
            return field.GetValue(target);
        }

        private static object Property(object target, string name)
        {
            PropertyInfo property = target.GetType().GetProperty(name, Members);
            if (property == null)
            {
                throw new CommandException(501, "This game version's " + target.GetType().Name + " has no " + name + ", so RimMCP cannot edit ideoligions here; edit it in game.");
            }
            return property.GetValue(target, null);
        }

        private static object Call(object target, string name, params object[] args)
        {
            return Invoke(target.GetType(), target, name, args);
        }

        private static object CallStatic(Type type, string name, params object[] args)
        {
            return Invoke(type, null, name, args);
        }

        private static object Invoke(Type type, object target, string name, object[] args)
        {
            MethodInfo method = type.GetMethods(Members).FirstOrDefault(candidate => candidate.Name == name && candidate.GetParameters().Length == args.Length);
            if (method == null)
            {
                throw new CommandException(501, "This game version's " + type.Name + " has no " + name + ", so RimMCP cannot edit ideoligions here; edit it in game.");
            }
            try
            {
                return method.Invoke(target, args);
            }
            catch (TargetInvocationException ex)
            {
                throw ex.InnerException ?? ex;
            }
        }
    }
}
