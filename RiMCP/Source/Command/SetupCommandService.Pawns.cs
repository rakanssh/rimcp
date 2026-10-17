using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using RimWorld;
using RimWorld.Planet;
using RiMCP.Bridge;
using RiMCP.Read;
using RiMCP.Shared;
using Verse;

namespace RiMCP.Command
{
    // The colonist screen: arranging, preferring, rerolling, and naming colonists.
    internal static partial class SetupCommandService
    {
        private const int MaxRerollAttempts = 500;

        public static BridgeResponse SetStartingPawns(BridgeRequest request, RouteMatch route)
        {
            PawnsBody body = CommandUtil.ReadBody<PawnsBody>(request);
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            return SessionCommandService.Begin(request, "set_starting_pawns", requestId, () =>
            {
                SetupController.Require<Page_ConfigureStartingPawns>(SetupController.Pawns);
                SetupController.RequireRevision(body.ExpectedRevision);
                int count = Find.GameInitData.startingAndOptionalPawns.Count;
                List<Reroll> rerolls = new List<Reroll>();
                foreach (RerollEntry entry in body.Rerolls ?? new RerollEntry[0])
                {
                    if (entry == null || !entry.Slot.HasValue || entry.Slot.Value < 0 || entry.Slot.Value >= count)
                    {
                        throw new CommandException(400, "Each reroll needs a slot from 0 to " + (count - 1) + ".");
                    }
                    rerolls.Add(new Reroll
                    {
                        Slot = entry.Slot.Value,
                        Criteria = Criteria(entry),
                        MaxAttempts = Math.Max(1, Math.Min(MaxRerollAttempts, entry.MaxAttempts ?? 100))
                    });
                }
                foreach (NameEntry name in body.Names ?? new NameEntry[0])
                {
                    if (name == null || !name.Slot.HasValue || name.Slot.Value < 0 || name.Slot.Value >= count || string.IsNullOrWhiteSpace(name.First) || string.IsNullOrWhiteSpace(name.Last))
                    {
                        throw new CommandException(400, "Each name needs a slot from 0 to " + (count - 1) + ", first, and last.");
                    }
                }
                int[] order = body.Order;
                if (order != null && (order.Length != count || order.Distinct().Count() != count || order.Any(slot => slot < 0 || slot >= count)))
                {
                    throw new CommandException(400, "Field 'order' lists every slot from 0 to " + (count - 1) + " once, in the new order; the first " + Find.GameInitData.startingPawnCount + " start.");
                }
                List<Preference> preferences = (body.Preferences ?? new PreferenceEntry[0]).Select(entry => ParsePreference(entry, count)).ToList();
                if (rerolls.Count == 0 && preferences.Count == 0 && order == null && (body.Names == null || body.Names.Length == 0))
                {
                    throw new CommandException(400, "Give order, preferences, rerolls, or names.");
                }
                int own = SetupController.Revision;
                NameEntry[] names = body.Names ?? new NameEntry[0];
                return operation => Rerolls(operation, order, preferences, rerolls, names, own);
            }, Fingerprint(body));
        }

        private sealed class Reroll
        {
            public int Slot;
            public PawnCriteria Criteria;
            public int MaxAttempts;
            public int Attempts;
            public bool Matched;
            public List<string> Unmet = new List<string>();
        }

        // One native reroll per frame: a single reroll can generate several pawns, until the colony's
        // required work types are covered. Each step first checks nobody else changed the setup.
        private sealed class Preference
        {
            public int Slot;
            public DevelopmentalStage? Stage;
            public XenotypeDef Xenotype;
            public bool AnyNonArchite;
        }

        // The colonist card's age and xenotype buttons (Biotech), which set the slot's generation
        // request; both reroll the colonist, except choosing any non-archite xenotype.
        private static Preference ParsePreference(PreferenceEntry entry, int count)
        {
            if (entry == null || !entry.Slot.HasValue || entry.Slot.Value < 0 || entry.Slot.Value >= count)
            {
                throw new CommandException(400, "Each preference needs a slot from 0 to " + (count - 1) + ".");
            }
            if (!ModsConfig.BiotechActive)
            {
                throw new CommandException(409, "The colonist screen offers age and xenotype choices only with Biotech.");
            }
            Preference preference = new Preference { Slot = entry.Slot.Value };
            if (!string.IsNullOrWhiteSpace(entry.Stage))
            {
                DevelopmentalStage stage;
                string stageName = entry.Stage.Trim();
                if (!new[] { "Adult", "Child", "Baby" }.Contains(stageName, StringComparer.OrdinalIgnoreCase) || !Enum.TryParse(stageName, true, out stage))
                {
                    throw new CommandException(400, "Field 'stage' must be Adult, Child, or Baby.");
                }
                if (stage != DevelopmentalStage.Adult && !ScenarioUtility.AllowsChildSelection(Find.Scenario))
                {
                    throw new CommandException(409, "MessageDevelopmentalStageSelectionDisabledByScenario".Translate().ToString());
                }
                preference.Stage = stage;
            }
            if (!string.IsNullOrWhiteSpace(entry.Xenotype))
            {
                if (string.Equals(entry.Xenotype.Trim(), "anyNonArchite", StringComparison.OrdinalIgnoreCase))
                {
                    preference.AnyNonArchite = true;
                }
                else
                {
                    preference.Xenotype = DefNamed<XenotypeDef>(entry.Xenotype, "xenotype");
                }
            }
            if (!preference.Stage.HasValue && preference.Xenotype == null && !preference.AnyNonArchite)
            {
                throw new CommandException(400, "Each preference sets stage, xenotype, or both.");
            }
            return preference;
        }

        // As the screen's drag to reorder does, one colonist at a time.
        private static void Reorder(int[] order)
        {
            List<Pawn> pawns = Find.GameInitData.startingAndOptionalPawns;
            List<Pawn> wanted = order.Select(slot => pawns[slot]).ToList();
            for (int to = 0; to < wanted.Count; to++)
            {
                int from = pawns.IndexOf(wanted[to]);
                if (from == to)
                {
                    continue;
                }
                Pawn pawn = pawns[from];
                pawns.Insert(to, pawn);
                pawns.RemoveAt(from < to ? from : from + 1);
                StartingPawnUtility.ReorderRequests(from, to);
            }
        }

        // Returns whether the colonist was rerolled.
        private static bool Prefer(Preference preference)
        {
            PawnGenerationRequest request = StartingPawnUtility.GetGenerationRequest(preference.Slot);
            bool reroll = false;
            bool changed = false;
            if (preference.Stage.HasValue && !request.AllowedDevelopmentalStages.Has(preference.Stage.Value))
            {
                request.AllowedDevelopmentalStages = preference.Stage.Value;
                request.AllowDowned = preference.Stage.Value == DevelopmentalStage.Baby;
                reroll = true;
                changed = true;
            }
            if (preference.AnyNonArchite && (request.ForcedXenotype != null || request.ForcedCustomXenotype != null))
            {
                request.ForcedXenotype = null;
                request.ForcedCustomXenotype = null;
                request.AllowedXenotypes = DefDatabase<XenotypeDef>.AllDefs.Where(xenotype => !xenotype.Archite && xenotype != XenotypeDefOf.Baseliner).ToList();
                request.ForceBaselinerChance = 0.5f;
                changed = true;
            }
            else if (preference.Xenotype != null && request.ForcedXenotype != preference.Xenotype)
            {
                request.ForcedXenotype = preference.Xenotype;
                request.ForcedCustomXenotype = null;
                request.AllowedXenotypes = null;
                request.ForceBaselinerChance = 0f;
                reroll = true;
                changed = true;
            }
            if (changed)
            {
                StartingPawnUtility.SetGenerationRequest(preference.Slot, request);
            }
            if (reroll)
            {
                StartingPawnUtility.RandomizePawn(preference.Slot);
            }
            return reroll;
        }

        // Order first; preferences, rerolls, and names then refer to slots in the new order.
        private static IEnumerable<string> Rerolls(Operation operation, int[] order, List<Preference> preferences, List<Reroll> rerolls, NameEntry[] names, int own)
        {
            if (order != null)
            {
                SetupController.Continue(operation, own);
                Reorder(order);
                own = SetupController.Changed();
                operation.Result = RerollResult(rerolls);
                yield return "reordering";
            }
            foreach (Preference preference in preferences)
            {
                SetupController.Continue(operation, own);
                Prefer(preference);
                own = SetupController.Changed();
                operation.Result = RerollResult(rerolls);
                yield return "settingPreferences";
            }
            foreach (Reroll reroll in rerolls)
            {
                while (true)
                {
                    SetupController.Continue(operation, own);
                    Pawn pawn = Find.GameInitData.startingAndOptionalPawns[reroll.Slot];
                    reroll.Unmet = reroll.Criteria.Unmet(pawn);
                    if (reroll.Unmet.Count == 0)
                    {
                        reroll.Matched = true;
                        break;
                    }
                    if (reroll.Attempts >= reroll.MaxAttempts)
                    {
                        break;
                    }
                    StartingPawnUtility.RandomizePawn(reroll.Slot);
                    reroll.Attempts++;
                    own = SetupController.Changed();
                    operation.Result = RerollResult(rerolls);
                    yield return "rerolling";
                }
            }
            SetupController.Continue(operation, own);
            foreach (NameEntry entry in names)
            {
                Pawn pawn = Find.GameInitData.startingAndOptionalPawns[entry.Slot.Value];
                pawn.Name = new NameTriple(entry.First.Trim(), string.IsNullOrWhiteSpace(entry.Nick) ? entry.First.Trim() : entry.Nick.Trim(), entry.Last.Trim());
            }
            if (names.Length > 0)
            {
                SetupController.Changed();
            }
            operation.Result = RerollResult(rerolls);
        }

        private static object RerollResult(List<Reroll> rerolls)
        {
            return Dto.Obj(
                Dto.Field("rerolls", rerolls.Select(reroll => (object)Dto.Obj(
                    Dto.Field("slot", reroll.Slot),
                    Dto.Field("attempts", reroll.Attempts),
                    Dto.Field("matched", reroll.Matched),
                    Dto.Field("unmet", reroll.Unmet.Count == 0 ? null : reroll.Unmet.ToArray()))).ToArray()),
                Dto.Field("roster", Roster()));
        }

        private static object[] Roster()
        {
            GameInitData init = Find.GameInitData;
            return init.startingAndOptionalPawns.Select((pawn, slot) => (object)Dto.Obj(
                Dto.Field("slot", slot),
                Dto.Field("starting", slot < init.startingPawnCount),
                Dto.Field("id", pawn.ThingID),
                Dto.Field("name", ReadUtil.PawnName(pawn)),
                Dto.Field("age", pawn.ageTracker.AgeBiologicalYears),
                Dto.Field("traits", pawn.story == null ? null : pawn.story.traits.allTraits.Select(trait => trait.def.defName).ToArray()),
                Dto.Field("skills", pawn.skills == null ? null : pawn.skills.skills.Select(skill => (object)Dto.Obj(
                    Dto.Field("skill", skill.def.defName),
                    Dto.Field("level", skill.TotallyDisabled ? null : (object)skill.Level),
                    Dto.Field("passion", skill.passion == Passion.None ? null : skill.passion.ToString()))).ToArray()),
                Dto.Field("cannotDo", DefDatabase<WorkTypeDef>.AllDefs.Where(pawn.WorkTypeIsDisabled).Select(work => work.defName).ToArray()),
                Dto.Field("stage", ModsConfig.BiotechActive ? pawn.DevelopmentalStage.ToString() : null),
                Dto.Field("xenotype", !ModsConfig.BiotechActive || pawn.genes == null ? null : pawn.genes.UniqueXenotype ? pawn.genes.xenotypeName : pawn.genes.Xenotype.defName),
                Dto.Field("preference", ModsConfig.BiotechActive ? PreferenceShown(slot) : null))).ToArray();
        }

        private static object PreferenceShown(int slot)
        {
            PawnGenerationRequest request = StartingPawnUtility.GetGenerationRequest(slot);
            return Dto.Obj(
                Dto.Field("stage", request.AllowedDevelopmentalStages.ToString()),
                Dto.Field("xenotype", request.ForcedXenotype != null ? request.ForcedXenotype.defName : request.ForcedCustomXenotype != null ? request.ForcedCustomXenotype.name : request.AllowedXenotypes != null ? "anyNonArchite" : null));
        }

        private static PawnCriteria Criteria(RerollEntry entry)
        {
            PawnCriteria criteria = new PawnCriteria { MinAge = entry.MinAge, MaxAge = entry.MaxAge };
            if (entry.Xenotypes != null)
            {
                if (!ModsConfig.BiotechActive)
                {
                    throw new CommandException(409, "Xenotypes need Biotech.");
                }
                criteria.Xenotypes = DefList<XenotypeDef>(entry.Xenotypes, "xenotypes");
            }
            foreach (SkillEntry skill in entry.Skills ?? new SkillEntry[0])
            {
                SkillDef def = skill == null ? null : DefDatabase<SkillDef>.GetNamedSilentFail((skill.Skill ?? "").Trim());
                if (def == null || !skill.Min.HasValue || skill.Min.Value < 0 || skill.Min.Value > 20)
                {
                    throw new CommandException(400, "Each skill needs a SkillDef name, such as Cooking, and min from 0 to 20.");
                }
                criteria.Skills.Add(new PawnCriteria.SkillNeed { Skill = def, Min = skill.Min.Value, Passion = (Passion)ParseEnum<Passion>(skill.Passion, "passion", Passion.None) });
            }
            criteria.Traits = DefList<TraitDef>(entry.Traits, "traits");
            criteria.ExcludeTraits = DefList<TraitDef>(entry.ExcludeTraits, "excludeTraits");
            criteria.CanDo = DefList<WorkTypeDef>(entry.CanDo, "canDo");
            if (entry.MinAge.HasValue && entry.MaxAge.HasValue && entry.MinAge.Value > entry.MaxAge.Value)
            {
                throw new CommandException(400, "Field 'minAge' is above 'maxAge'.");
            }
            if (criteria.Empty)
            {
                throw new CommandException(400, "A reroll needs at least one criterion: skills, traits, excludeTraits, canDo, xenotypes, minAge, or maxAge.");
            }
            return criteria;
        }

        private static string Fingerprint(PawnsBody body)
        {
            string rerolls = string.Join(";", (body.Rerolls ?? new RerollEntry[0]).Select(entry => entry == null ? "-" : entry.Slot + ":" + Join(entry.Traits) + ":" + Join(entry.ExcludeTraits) + ":" + Join(entry.CanDo) + ":" + Join(entry.Xenotypes) + ":" + entry.MinAge + "-" + entry.MaxAge + ":" + entry.MaxAttempts + ":" + string.Join(",", (entry.Skills ?? new SkillEntry[0]).Select(skill => skill == null ? "-" : skill.Skill + skill.Min + skill.Passion).ToArray())).ToArray());
            string names = string.Join(";", (body.Names ?? new NameEntry[0]).Select(entry => entry == null ? "-" : entry.Slot + ":" + entry.First + "/" + entry.Nick + "/" + entry.Last).ToArray());
            string order = body.Order == null ? "-" : string.Join(",", body.Order.Select(slot => slot.ToString()).ToArray());
            string preferences = string.Join(";", (body.Preferences ?? new PreferenceEntry[0]).Select(entry => entry == null ? "-" : entry.Slot + ":" + entry.Stage + ":" + entry.Xenotype).ToArray());
            return rerolls + "|" + names + "|" + order + "|" + preferences + "|" + body.ExpectedRevision;
        }

        [DataContract]
        private sealed class PawnsBody
        {
            [DataMember(Name = "order")] public int[] Order { get; set; }
            [DataMember(Name = "preferences")] public PreferenceEntry[] Preferences { get; set; }
            [DataMember(Name = "rerolls")] public RerollEntry[] Rerolls { get; set; }
            [DataMember(Name = "names")] public NameEntry[] Names { get; set; }
            [DataMember(Name = "expectedRevision")] public int? ExpectedRevision { get; set; }
            [DataMember(Name = "requestId")] public string RequestId { get; set; }
        }

        [DataContract]
        private sealed class RerollEntry
        {
            [DataMember(Name = "slot")] public int? Slot { get; set; }
            [DataMember(Name = "skills")] public SkillEntry[] Skills { get; set; }
            [DataMember(Name = "traits")] public string[] Traits { get; set; }
            [DataMember(Name = "excludeTraits")] public string[] ExcludeTraits { get; set; }
            [DataMember(Name = "canDo")] public string[] CanDo { get; set; }
            [DataMember(Name = "xenotypes")] public string[] Xenotypes { get; set; }
            [DataMember(Name = "minAge")] public int? MinAge { get; set; }
            [DataMember(Name = "maxAge")] public int? MaxAge { get; set; }
            [DataMember(Name = "maxAttempts")] public int? MaxAttempts { get; set; }
        }

        [DataContract]
        private sealed class PreferenceEntry
        {
            [DataMember(Name = "slot")] public int? Slot { get; set; }
            [DataMember(Name = "stage")] public string Stage { get; set; }
            [DataMember(Name = "xenotype")] public string Xenotype { get; set; }
        }

        [DataContract]
        private sealed class SkillEntry
        {
            [DataMember(Name = "skill")] public string Skill { get; set; }
            [DataMember(Name = "min")] public int? Min { get; set; }
            [DataMember(Name = "passion")] public string Passion { get; set; }
        }

        [DataContract]
        private sealed class NameEntry
        {
            [DataMember(Name = "slot")] public int? Slot { get; set; }
            [DataMember(Name = "first")] public string First { get; set; }
            [DataMember(Name = "nick")] public string Nick { get; set; }
            [DataMember(Name = "last")] public string Last { get; set; }
        }
    }
}
