using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RiMCP.Bridge;
using Verse;

namespace RiMCP.Read
{
    internal static class PawnReadService
    {
        private static readonly string[] SupportedFilters =
        {
            "core", "colonist", "colonists", "slave", "slaves", "prisoner", "prisoners",
            "guest", "guests", "colonyAnimal", "colonyAnimals", "wildAnimal", "wildAnimals",
            "hostile", "hostiles", "animals", "wildlife", "threats", "other", "others", "all"
        };
        private static readonly HashSet<string> SupportedFilterLookup = new HashSet<string>(SupportedFilters, StringComparer.OrdinalIgnoreCase);

        public static BridgeResponse ListPawns(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.FromMap(request);

            string filter = context.Request.Get("filter");
            if (string.IsNullOrWhiteSpace(filter))
            {
                filter = "core";
            }
            if (!SupportedFilterLookup.Contains(filter))
            {
                return BridgeResponse.Error(400, "Unknown pawn filter '" + filter + "'. Supported filters: " + string.Join(", ", SupportedFilters) + ".");
            }

            IEnumerable<PawnRole> source = PawnsForFilter(context.Map, filter);
            source = source.OrderBy(p => p.Role).ThenBy(p => p.Pawn.LabelShortCap);
            Page<PawnRole> page = new Page<PawnRole>(source, context.Request);
            object[] pawns = page.Items
                .Select(role => context.Request.IdsOnly ? ReadUtil.ThingId(role.Pawn) : SerializePawn(role.Pawn, role.Role, context.Request.Detail, context.Request))
                .ToArray();

            Dictionary<string, object> data = Dto.Obj(
                Dto.Field("filter", filter),
                Dto.Field("pawns", pawns));
            if (context.Request.Wants("assignmentOptions"))
            {
                data["assignmentOptions"] = SerializeAssignmentOptions(context.Map);
            }

            return ReadEnvelope.Ok(context, data, page.Truncated, page.NextCursor);
        }

        public static BridgeResponse GetPawn(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.FromMap(request);
            string id = route["id"];

            Pawn pawn = FindPawn(context.Map, id);
            if (pawn == null)
            {
                return BridgeResponse.Error(404, "Pawn not found.");
            }

            return ReadEnvelope.Ok(context, SerializePawn(pawn, RoleForPawn(context.Map, pawn), ReadDetail.Full, context.Request));
        }

        public static Pawn FindPawn(Map map, string id)
        {
            if (map == null || string.IsNullOrWhiteSpace(id))
            {
                return null;
            }
            return map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.ThingID == id || p.GetUniqueLoadID() == id);
        }

        public static IEnumerable<PawnRole> PawnsForFilter(Map map, string filter)
        {
            if (map == null)
            {
                yield break;
            }

            string normalized = (filter ?? "core").ToLowerInvariant();
            foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
            {
                string role = RoleForPawn(map, pawn);
                string roleKey = role.ToLowerInvariant();
                if (normalized == "all" ||
                    normalized == "core" && IsCoreRole(role) ||
                    normalized == roleKey ||
                    normalized == roleKey + "s" ||
                    normalized == "animals" && role == "colonyAnimal" ||
                    normalized == "wildlife" && role == "wildAnimal" ||
                    normalized == "threats" && role == "hostile")
                {
                    yield return new PawnRole(pawn, role);
                }
            }
        }

        public static string RoleForPawn(Map map, Pawn pawn)
        {
            if (pawn == null)
            {
                return "unknown";
            }
            if (pawn.HostileTo(Faction.OfPlayer))
            {
                return "hostile";
            }
            if (map.mapPawns.FreeColonistsSpawned.Contains(pawn))
            {
                return "colonist";
            }
            if (map.mapPawns.SlavesOfColonySpawned.Contains(pawn))
            {
                return "slave";
            }
            if (map.mapPawns.PrisonersOfColonySpawned.Contains(pawn))
            {
                return "prisoner";
            }
            if (pawn.RaceProps != null && pawn.RaceProps.Animal)
            {
                return pawn.Faction == Faction.OfPlayer ? "colonyAnimal" : "wildAnimal";
            }
            if (pawn.RaceProps != null && pawn.RaceProps.Humanlike && pawn.Faction != Faction.OfPlayer)
            {
                return "guest";
            }
            return "other";
        }

        public static bool IsCoreRole(string role)
        {
            return role == "colonist" || role == "slave" || role == "prisoner";
        }

        public static object SerializePawn(Pawn pawn, string role, ReadDetail detail, ReadRequest request)
        {
            Dictionary<string, object> dto = SummaryDto.Pawn(pawn);
            dto["role"] = role;
            dto["def"] = ReadUtil.Def(pawn.def);
            dto["kindDef"] = pawn.kindDef == null ? null : ReadUtil.Def(pawn.kindDef);
            dto["faction"] = SummaryDto.FactionIdentity(pawn.Faction);
            dto["gender"] = pawn.gender.ToString();
            dto["ageBiologicalYears"] = pawn.ageTracker == null ? 0 : pawn.ageTracker.AgeBiologicalYears;
            dto["downed"] = pawn.Downed;
            dto["dead"] = pawn.Dead;
            dto["drafted"] = pawn.Drafted;
            dto["position"] = ReadUtil.Cell(pawn.Position);
            dto["mentalState"] = pawn.MentalStateDef == null ? null : pawn.MentalStateDef.defName;
            dto["currentJob"] = SerializeCurrentJob(pawn);
            dto["ideoligion"] = IdeoligionReadService.SerializePawnIdeoligionSummary(pawn);

            if (detail != ReadDetail.Summary || request.Wants("needs"))
            {
                dto["needs"] = SerializeNeeds(pawn, detail);
                dto["mood"] = SerializeMood(pawn, detail);
            }
            if (detail != ReadDetail.Summary || request.Wants("health"))
            {
                dto["health"] = SerializeHealth(pawn, detail);
            }
            if (detail != ReadDetail.Summary || request.Wants("skills"))
            {
                dto["skills"] = SerializeSkills(pawn);
                dto["traits"] = SerializeTraits(pawn);
            }
            if (detail == ReadDetail.Full || request.Wants("work"))
            {
                dto["work"] = SerializePawnWork(pawn);
                dto["schedule"] = SerializeSchedule(pawn);
            }
            if (detail == ReadDetail.Full || request.Wants("assignments"))
            {
                dto["assignments"] = SerializeAssignments(pawn);
            }
            if (detail == ReadDetail.Full || request.Wants("training"))
            {
                dto["training"] = SerializeTraining(pawn);
            }
            if (detail == ReadDetail.Full || request.Wants("gear"))
            {
                dto["gear"] = SerializeGear(pawn);
                dto["inventory"] = SerializeInventory(pawn);
            }
            if (detail == ReadDetail.Full || request.Wants("relations"))
            {
                dto["relations"] = SerializeRelations(pawn);
            }
            return dto;
        }

        public static object SerializeCurrentJob(Pawn pawn)
        {
            JobDef jobDef = pawn.CurJobDef;
            if (jobDef == null)
            {
                return null;
            }
            string report = null;
            try
            {
                report = pawn.CurJob == null ? null : pawn.CurJob.GetReport(pawn);
            }
            catch
            {
            }
            return Dto.Obj(
                Dto.Field("defName", jobDef.defName),
                Dto.Field("label", ReadUtil.DefLabel(jobDef)),
                Dto.Field("report", report));
        }

        public static object SerializeNeeds(Pawn pawn, ReadDetail detail)
        {
            if (pawn.needs == null)
            {
                return null;
            }
            List<object> needs = new List<object>();
            foreach (Need need in pawn.needs.AllNeeds.OrderBy(n => n.def.defName))
            {
                needs.Add(Dto.Obj(
                    Dto.Field("defName", need.def.defName),
                    Dto.Field("label", need.LabelCap),
                    Dto.Field("level", need.CurLevelPercentage),
                    Dto.Field("seeking", need.CurLevelPercentage < 0.35f)));
            }
            return needs;
        }

        public static object SerializeMood(Pawn pawn, ReadDetail detail)
        {
            if (pawn.needs == null || pawn.needs.mood == null)
            {
                return null;
            }

            Dictionary<string, object> mood = Dto.Obj(
                Dto.Field("level", pawn.needs.mood.CurLevelPercentage),
                Dto.Field("instantLevel", pawn.needs.mood.CurInstantLevelPercentage));

            if (detail == ReadDetail.Full)
            {
                List<object> thoughts = new List<object>();
                MemoryThoughtHandler memories = pawn.needs.mood.thoughts == null ? null : pawn.needs.mood.thoughts.memories;
                foreach (Thought_Memory memory in memories == null ? Enumerable.Empty<Thought_Memory>() : memories.Memories)
                {
                    thoughts.Add(Dto.Obj(
                        Dto.Field("defName", memory.def == null ? null : memory.def.defName),
                        Dto.Field("label", memory.LabelCap),
                        Dto.Field("moodOffset", memory.MoodOffset())));
                }
                mood["thoughts"] = thoughts;
            }
            return mood;
        }

        public static object SerializeHealth(Pawn pawn, ReadDetail detail)
        {
            List<object> hediffs = new List<object>();
            if (pawn.health != null && pawn.health.hediffSet != null)
            {
                foreach (Hediff hediff in pawn.health.hediffSet.hediffs.OrderByDescending(h => h.Severity))
                {
                    hediffs.Add(Dto.Obj(
                        Dto.Field("defName", hediff.def == null ? null : hediff.def.defName),
                        Dto.Field("label", hediff.LabelCap),
                        Dto.Field("severity", hediff.Severity),
                        Dto.Field("part", hediff.Part == null ? null : hediff.Part.LabelCap)));
                }
            }

            Dictionary<string, object> health = Dto.Obj(
                Dto.Field("dead", pawn.Dead),
                Dto.Field("downed", pawn.Downed),
                Dto.Field("pain", pawn.health == null || pawn.health.hediffSet == null ? 0f : pawn.health.hediffSet.PainTotal),
                Dto.Field("summaryHealthPercent", pawn.health == null || pawn.health.summaryHealth == null ? 0f : pawn.health.summaryHealth.SummaryHealthPercent),
                Dto.Field("hediffs", hediffs));

            if (detail == ReadDetail.Full && pawn.health != null && pawn.health.capacities != null)
            {
                health["capacities"] = DefDatabase<PawnCapacityDef>.AllDefsListForReading
                    .OrderBy(def => def.defName)
                    .Select(def => Dto.Obj(
                        Dto.Field("defName", def.defName),
                        Dto.Field("label", ReadUtil.DefLabel(def)),
                        Dto.Field("level", pawn.health.capacities.GetLevel(def))))
                    .ToArray();
            }
            return health;
        }

        public static object SerializeSkills(Pawn pawn)
        {
            if (pawn.skills == null)
            {
                return new object[0];
            }
            return pawn.skills.skills
                .OrderBy(skill => skill.def.defName)
                .Select(skill => Dto.Obj(
                    Dto.Field("defName", skill.def.defName),
                    Dto.Field("label", ReadUtil.DefLabel(skill.def)),
                    Dto.Field("level", skill.Level),
                    Dto.Field("passion", skill.passion.ToString())))
                .ToArray();
        }

        public static object SerializeTraits(Pawn pawn)
        {
            if (pawn.story == null || pawn.story.traits == null)
            {
                return new object[0];
            }
            return pawn.story.traits.allTraits
                .Select(trait => Dto.Obj(
                    Dto.Field("defName", trait.def == null ? null : trait.def.defName),
                    Dto.Field("label", trait.LabelCap)))
                .ToArray();
        }

        public static object SerializePawnWork(Pawn pawn)
        {
            List<object> work = new List<object>();
            foreach (WorkTypeDef workType in DefDatabase<WorkTypeDef>.AllDefsListForReading.OrderBy(def => def.naturalPriority))
            {
                bool disabled = false;
                bool active = false;
                int priority = 0;
                try
                {
                    disabled = pawn.WorkTypeIsDisabled(workType);
                    if (!disabled && pawn.workSettings != null)
                    {
                        active = pawn.workSettings.WorkIsActive(workType);
                        priority = pawn.workSettings.GetPriority(workType);
                    }
                }
                catch
                {
                }
                work.Add(Dto.Obj(
                    Dto.Field("defName", workType.defName),
                    Dto.Field("label", ReadUtil.DefLabel(workType)),
                    Dto.Field("naturalPriority", workType.naturalPriority),
                    Dto.Field("disabled", disabled),
                    Dto.Field("active", active),
                    Dto.Field("priority", priority)));
            }
            return work;
        }

        public static object SerializeSchedule(Pawn pawn)
        {
            Pawn_TimetableTracker timetable = pawn.timetable;
            if (timetable == null)
            {
                return null;
            }
            List<object> hours = new List<object>();
            for (int hour = 0; hour < 24; hour++)
            {
                TimeAssignmentDef assignment = timetable.GetAssignment(hour);
                hours.Add(Dto.Obj(
                    Dto.Field("hour", hour),
                    Dto.Field("assignment", assignment == null ? null : assignment.defName)));
            }
            Area area = pawn.playerSettings == null ? null : pawn.playerSettings.AreaRestrictionInPawnCurrentMap;
            return Dto.Obj(
                Dto.Field("hours", hours),
                Dto.Field("allowedArea", SerializeAllowedArea(area, pawn.Map)));
        }

        public static object SerializeAssignments(Pawn pawn)
        {
            Pawn_PlayerSettings settings = pawn.playerSettings;
            return Dto.Obj(
                Dto.Field("schedule", SerializeSchedule(pawn)),
                Dto.Field("policies", Dto.Obj(
                    Dto.Field("apparel", pawn.outfits == null ? null : SerializePolicy(pawn.outfits.CurrentApparelPolicy)),
                    Dto.Field("food", pawn.foodRestriction == null ? null : SerializePolicy(pawn.foodRestriction.CurrentFoodPolicy)),
                    Dto.Field("drug", pawn.drugs == null ? null : SerializePolicy(pawn.drugs.CurrentPolicy)),
                    Dto.Field("reading", pawn.reading == null ? null : SerializePolicy(pawn.reading.CurrentPolicy)))),
                Dto.Field("medicalCare", settings == null ? null : Dto.Obj(
                    Dto.Field("value", settings.medCare.ToString()),
                    Dto.Field("label", MedicalCareUtility.GetLabel(settings.medCare)))),
                Dto.Field("selfTend", settings == null ? (object)null : settings.selfTend),
                Dto.Field("hostilityResponse", settings == null || !settings.UsesConfigurableHostilityResponse ? null : settings.hostilityResponse.ToString()),
                Dto.Field("allowedArea", settings == null ? null : SerializeAllowedArea(settings.AreaRestrictionInPawnCurrentMap, pawn.Map)),
                Dto.Field("carryMedicine", SerializeCarryMedicine(pawn)),
                Dto.Field("prisonerInteraction", pawn.guest == null ? null : ReadUtil.Def(pawn.guest.ExclusiveInteractionMode)));
        }

        public static object SerializeAssignmentOptions(Map map)
        {
            InventoryStockGroupDef medicineGroup = InventoryStockGroupDefOf.Medicine;
            return Dto.Obj(
                Dto.Field("assignmentKinds", new[]
                {
                    "schedule", "policy", "medicalCare", "selfTend", "hostilityResponse", "allowedArea", "carryMedicine", "prisonerInteraction"
                }),
                Dto.Field("timeAssignments", DefDatabase<TimeAssignmentDef>.AllDefsListForReading
                    .OrderBy(def => def.defName)
                    .Select(def => ReadUtil.Def(def))
                    .ToArray()),
                Dto.Field("policies", Dto.Obj(
                    Dto.Field("apparel", Current.Game == null || Current.Game.outfitDatabase == null ? new object[0] : Current.Game.outfitDatabase.AllOutfits.Select(SerializePolicy).ToArray()),
                    Dto.Field("food", Current.Game == null || Current.Game.foodRestrictionDatabase == null ? new object[0] : Current.Game.foodRestrictionDatabase.AllFoodRestrictions.Select(SerializePolicy).ToArray()),
                    Dto.Field("drug", Current.Game == null || Current.Game.drugPolicyDatabase == null ? new object[0] : Current.Game.drugPolicyDatabase.AllPolicies.Select(SerializePolicy).ToArray()),
                    Dto.Field("reading", Current.Game == null || Current.Game.readingPolicyDatabase == null ? new object[0] : Current.Game.readingPolicyDatabase.AllReadingPolicies.Select(SerializePolicy).ToArray()))),
                Dto.Field("medicalCare", Enum.GetValues(typeof(MedicalCareCategory))
                    .Cast<MedicalCareCategory>()
                    .Select(value => Dto.Obj(
                        Dto.Field("value", value.ToString()),
                        Dto.Field("label", MedicalCareUtility.GetLabel(value))))
                    .ToArray()),
                Dto.Field("hostilityResponse", Enum.GetNames(typeof(HostilityResponseMode))),
                Dto.Field("allowedAreas", map == null ? new object[0] : map.areaManager.AllAreas
                    .Where(area => area.AssignableAsAllowed())
                    .OrderBy(area => area.Label)
                    .Select(area => SerializeAllowedArea(area, map))
                    .ToArray()),
                Dto.Field("medicineCarry", Dto.Obj(
                    Dto.Field("minCount", 0),
                    Dto.Field("maxCount", 3),
                    Dto.Field("medicineOptions", medicineGroup == null || medicineGroup.thingDefs == null ? new object[0] : medicineGroup.thingDefs
                        .OrderBy(def => def.defName)
                        .Select(def => ReadUtil.Def(def))
                        .ToArray()))),
                Dto.Field("prisonerInteractionModes", DefDatabase<PrisonerInteractionModeDef>.AllDefsListForReading
                    .Where(def => !def.isNonExclusiveInteraction)
                    .OrderBy(def => def.defName)
                    .Select(def => ReadUtil.Def(def))
                    .ToArray()));
        }

        public static object SerializePolicy(Policy policy)
        {
            return policy == null
                ? null
                : Dto.Obj(
                    Dto.Field("id", policy.GetUniqueLoadID()),
                    Dto.Field("numericId", policy.id.ToString()),
                    Dto.Field("label", policy.label),
                    Dto.Field("renamableLabel", policy.RenamableLabel));
        }

        public static object SerializeAllowedArea(Area area, Map map)
        {
            return area == null
                ? null
                : Dto.Obj(
                    Dto.Field("id", AllowedAreaId(area, map)),
                    Dto.Field("numericId", area.ID.ToString()),
                    Dto.Field("label", area.Label));
        }

        public static string AllowedAreaId(Area area, Map map)
        {
            return area == null ? null : "area:" + (map == null ? "" : map.uniqueID.ToString()) + ":" + area.ID;
        }

        public static object SerializeCarryMedicine(Pawn pawn)
        {
            if (pawn == null || pawn.inventoryStock == null || InventoryStockGroupDefOf.Medicine == null)
            {
                return null;
            }

            InventoryStockGroupDef group = InventoryStockGroupDefOf.Medicine;
            ThingDef thingDef = pawn.inventoryStock.GetDesiredThingForGroup(group);
            return Dto.Obj(
                Dto.Field("count", pawn.inventoryStock.GetDesiredCountForGroup(group)),
                Dto.Field("thing", ReadUtil.Def(thingDef)));
        }

        public static object SerializeTraining(Pawn pawn)
        {
            return SerializeTraining(pawn, null);
        }

        public static object SerializeTraining(Pawn pawn, Dictionary<string, bool> wantedOverride)
        {
            if (pawn == null || pawn.training == null || !pawn.IsAnimal)
            {
                return null;
            }

            return DefDatabase<TrainableDef>.AllDefsListForReading
                .OrderBy(def => def.listPriority)
                .ThenBy(def => def.defName)
                .Select(def => SerializeTrainable(pawn, def, wantedOverride))
                .ToArray();
        }

        private static object SerializeTrainable(Pawn pawn, TrainableDef trainable, Dictionary<string, bool> wantedOverride)
        {
            bool wanted;
            if (wantedOverride == null || !wantedOverride.TryGetValue(trainable.defName, out wanted))
            {
                wanted = pawn.training.GetWanted(trainable);
            }

            bool canBeTrained = false;
            bool assignable = false;
            string rejectionReason = null;
            try
            {
                canBeTrained = pawn.training.CanBeTrained(trainable);
                AcceptanceReport report = pawn.training.CanAssignToTrain(trainable);
                assignable = report.Accepted;
                rejectionReason = report.Accepted ? null : report.Reason;
            }
            catch
            {
            }

            return Dto.Obj(
                Dto.Field("defName", trainable.defName),
                Dto.Field("label", ReadUtil.DefLabel(trainable)),
                Dto.Field("wanted", wanted),
                Dto.Field("learned", pawn.training.HasLearned(trainable)),
                Dto.Field("canBeTrained", canBeTrained),
                Dto.Field("assignable", assignable),
                Dto.Field("rejectionReason", rejectionReason));
        }

        public static object SerializeGear(Pawn pawn)
        {
            List<object> equipment = new List<object>();
            if (pawn.equipment != null)
            {
                foreach (ThingWithComps thing in pawn.equipment.AllEquipmentListForReading)
                {
                    equipment.Add(ThingSummary(thing));
                }
            }
            List<object> apparel = new List<object>();
            if (pawn.apparel != null)
            {
                foreach (Apparel thing in pawn.apparel.WornApparel)
                {
                    apparel.Add(ThingSummary(thing));
                }
            }
            return Dto.Obj(
                Dto.Field("equipment", equipment),
                Dto.Field("apparel", apparel));
        }

        public static object SerializeInventory(Pawn pawn)
        {
            if (pawn.inventory == null || pawn.inventory.innerContainer == null)
            {
                return new object[0];
            }
            return pawn.inventory.innerContainer
                .Select(ThingSummary)
                .ToArray();
        }

        public static object SerializeRelations(Pawn pawn)
        {
            if (pawn.relations == null)
            {
                return new object[0];
            }
            List<object> relations = new List<object>();
            foreach (DirectPawnRelation relation in pawn.relations.DirectRelations)
            {
                relations.Add(Dto.Obj(
                    Dto.Field("defName", relation.def == null ? null : relation.def.defName),
                    Dto.Field("label", ReadUtil.DefLabel(relation.def)),
                    Dto.Field("otherPawnId", relation.otherPawn == null ? null : ReadUtil.ThingId(relation.otherPawn)),
                    Dto.Field("otherPawn", relation.otherPawn == null ? null : ReadUtil.PawnName(relation.otherPawn))));
            }
            return relations;
        }

        private static object ThingSummary(Thing thing)
        {
            if (thing == null)
            {
                return null;
            }
            Dictionary<string, object> dto = SummaryDto.Thing(thing);
            dto["stackCount"] = thing.stackCount;
            dto["hitPoints"] = thing.HitPoints;
            dto["quality"] = ReadUtil.QualityLabel(thing);
            return dto;
        }

        internal sealed class PawnRole
        {
            public readonly Pawn Pawn;
            public readonly string Role;

            public PawnRole(Pawn pawn, string role)
            {
                Pawn = pawn;
                Role = role;
            }
        }
    }
}
