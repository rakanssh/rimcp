using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Util;
using Verse;

namespace RiMCP.Data
{
    internal static class ColonyDataService
    {
        public static BridgeResponse GetColonySummary()
        {
            Map map = CurrentMap();
            if (map == null)
            {
                return BridgeResponse.Error(409, "No active map is loaded.");
            }

            int colonistCount = map.mapPawns.FreeColonistsSpawned.Count;
            int slaveCount = map.mapPawns.SlavesOfColonySpawned.Count;
            int prisonerCount = map.mapPawns.PrisonersOfColonySpawned.Count;
            int animalCount = map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer).Count(p => p.RaceProps != null && p.RaceProps.Animal);
            int storedResourceTypes = map.resourceCounter.AllCountedAmounts.Count(pair => pair.Key != null && pair.Value > 0);
            string currentResearch = CurrentResearchDefName();

            string body = Json.Object(
                Json.Prop("mapId", Json.String(map.uniqueID.ToString())),
                Json.Prop("mapName", Json.String(map.Parent == null ? "Unknown" : map.Parent.LabelCap)),
                Json.Prop("biome", Json.String(map.Biome == null ? null : map.Biome.defName)),
                Json.Prop("colonistCount", Json.Number(colonistCount)),
                Json.Prop("slaveCount", Json.Number(slaveCount)),
                Json.Prop("prisonerCount", Json.Number(prisonerCount)),
                Json.Prop("animalCount", Json.Number(animalCount)),
                Json.Prop("storedResourceTypes", Json.Number(storedResourceTypes)),
                Json.Prop("currentResearch", Json.String(currentResearch)),
                Json.Prop("readOnly", Json.Bool(true)));

            return BridgeResponse.Json(200, body);
        }

        public static BridgeResponse ListCorePawns()
        {
            Map map = CurrentMap();
            if (map == null)
            {
                return BridgeResponse.Error(409, "No active map is loaded.");
            }
            return BridgeResponse.Json(200, Json.Object(Json.Prop("pawns", Json.Array(CorePawns(map)
                .OrderBy(p => p.Pawn.LabelShortCap)
                .Select(SerializeCorePawnSummary)))));
        }

        public static BridgeResponse GetPawn(string id)
        {
            Pawn pawn = FindPawn(id);
            if (pawn == null)
            {
                return BridgeResponse.Error(404, "Pawn not found.");
            }
            return BridgeResponse.Json(200, SerializePawnDetail(pawn));
        }

        public static BridgeResponse ListInventory()
        {
            Map map = CurrentMap();
            if (map == null)
            {
                return BridgeResponse.Error(409, "No active map is loaded.");
            }

            IEnumerable<string> items = map.resourceCounter.AllCountedAmounts
                .Where(pair => pair.Key != null && pair.Value > 0)
                .OrderBy(pair => pair.Key.defName)
                .Select(pair => Json.Object(
                    Json.Prop("defName", Json.String(pair.Key.defName)),
                    Json.Prop("label", Json.String(pair.Key.LabelCap)),
                    Json.Prop("count", Json.Number(pair.Value))));

            return BridgeResponse.Json(200, Json.Object(Json.Prop("items", Json.Array(items))));
        }

        public static BridgeResponse ListBills()
        {
            Map map = CurrentMap();
            if (map == null)
            {
                return BridgeResponse.Error(409, "No active map is loaded.");
            }

            List<string> bills = new List<string>();
            foreach (Building building in map.listerBuildings.allBuildingsColonist)
            {
                IBillGiver giver = building as IBillGiver;
                if (giver == null || giver.BillStack == null)
                {
                    continue;
                }

                foreach (Bill bill in giver.BillStack.Bills)
                {
                    Bill_Production production = bill as Bill_Production;
                    bills.Add(Json.Object(
                        Json.Prop("billId", Json.String(bill.GetUniqueLoadID())),
                        Json.Prop("label", Json.String(bill.Label)),
                        Json.Prop("recipeDefName", Json.String(bill.recipe == null ? null : bill.recipe.defName)),
                        Json.Prop("suspended", Json.Bool(bill.suspended)),
                        Json.Prop("repeatMode", Json.String(production == null ? null : production.repeatMode.ToString())),
                        Json.Prop("targetCount", Json.Number(production == null ? 0 : production.targetCount)),
                        Json.Prop("workbenchId", Json.String(building.ThingID)),
                        Json.Prop("workbenchDefName", Json.String(building.def == null ? null : building.def.defName))));
                }
            }

            return BridgeResponse.Json(200, Json.Object(Json.Prop("bills", Json.Array(bills))));
        }

        public static BridgeResponse ListAlerts()
        {
            List<string> alerts = new List<string>();
            try
            {
                object letterStack = typeof(Find).GetProperty("LetterStack", BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
                object letters = letterStack.GetType().GetProperty("LettersListForReading", BindingFlags.Public | BindingFlags.Instance).GetValue(letterStack, null);
                List<object> recentLetters = new List<object>();
                foreach (object letter in (System.Collections.IEnumerable)letters)
                {
                    recentLetters.Add(letter);
                }

                int start = recentLetters.Count > 25 ? recentLetters.Count - 25 : 0;
                for (int i = start; i < recentLetters.Count; i++)
                {
                    object letter = recentLetters[i];
                    alerts.Add(Json.Object(
                        Json.Prop("kind", Json.String("letter")),
                        Json.Prop("label", Json.String(ReadStringMember(letter, "Label", "label"))),
                        Json.Prop("text", Json.String(ReadStringMember(letter, "Text", "text"))),
                        Json.Prop("defName", Json.String(ReadDefName(letter, "def")))));
                }
            }
            catch (System.Exception ex)
            {
                return BridgeResponse.Error(500, "Unable to read RimWorld alerts: " + ex.Message);
            }

            return BridgeResponse.Json(200, Json.Object(Json.Prop("alerts", Json.Array(alerts))));
        }

        public static BridgeResponse ListAnimals()
        {
            Map map = CurrentMap();
            if (map == null)
            {
                return BridgeResponse.Error(409, "No active map is loaded.");
            }

            return BridgeResponse.Json(200, Json.Object(Json.Prop("animals", Json.Array(Animals(map).Select(SerializeAnimal)))));
        }

        private static Map CurrentMap()
        {
            return Find.CurrentMap ?? (Find.Maps == null ? null : Find.Maps.FirstOrDefault());
        }

        private static IEnumerable<CorePawn> CorePawns(Map map)
        {
            HashSet<string> seen = new HashSet<string>();
            foreach (CorePawn corePawn in CorePawnSources(map))
            {
                if (corePawn.Pawn == null)
                {
                    continue;
                }

                string key = corePawn.Pawn.ThingID ?? corePawn.Pawn.GetUniqueLoadID();
                if (seen.Add(key))
                {
                    yield return corePawn;
                }
            }
        }

        private static IEnumerable<CorePawn> CorePawnSources(Map map)
        {
            foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
            {
                yield return new CorePawn(pawn, "colonist");
            }
            foreach (Pawn pawn in map.mapPawns.SlavesOfColonySpawned)
            {
                yield return new CorePawn(pawn, "slave");
            }
            foreach (Pawn pawn in map.mapPawns.PrisonersOfColonySpawned)
            {
                yield return new CorePawn(pawn, "prisoner");
            }
        }

        private static IEnumerable<Pawn> Animals(Map map)
        {
            return map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer)
                .Where(p => p.RaceProps != null && p.RaceProps.Animal)
                .OrderBy(p => p.LabelShortCap);
        }

        private static Pawn FindPawn(string id)
        {
            Map map = CurrentMap();
            if (map == null)
            {
                return null;
            }
            return map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.ThingID == id || p.GetUniqueLoadID() == id);
        }

        private static string SerializeCorePawnSummary(CorePawn corePawn)
        {
            return Json.Object(
                Json.Prop("id", Json.String(corePawn.Pawn.ThingID)),
                Json.Prop("loadId", Json.String(corePawn.Pawn.GetUniqueLoadID())),
                Json.Prop("colonyRole", Json.String(corePawn.ColonyRole)),
                Json.Prop("name", Json.String(corePawn.Pawn.Name == null ? corePawn.Pawn.LabelShortCap : corePawn.Pawn.Name.ToStringFull)),
                Json.Prop("label", Json.String(corePawn.Pawn.LabelShortCap)),
                Json.Prop("defName", Json.String(corePawn.Pawn.def == null ? null : corePawn.Pawn.def.defName)),
                Json.Prop("gender", Json.String(corePawn.Pawn.gender.ToString())),
                Json.Prop("ageBiologicalYears", Json.Number(corePawn.Pawn.ageTracker == null ? 0 : corePawn.Pawn.ageTracker.AgeBiologicalYears)),
                Json.Prop("downed", Json.Bool(corePawn.Pawn.Downed)),
                Json.Prop("drafted", Json.Bool(corePawn.Pawn.Drafted)),
                Json.Prop("position", SerializeCell(corePawn.Pawn.Position)));
        }

        private static string SerializePawnSummary(Pawn pawn)
        {
            return Json.Object(
                Json.Prop("id", Json.String(pawn.ThingID)),
                Json.Prop("loadId", Json.String(pawn.GetUniqueLoadID())),
                Json.Prop("name", Json.String(pawn.Name == null ? pawn.LabelShortCap : pawn.Name.ToStringFull)),
                Json.Prop("label", Json.String(pawn.LabelShortCap)),
                Json.Prop("defName", Json.String(pawn.def == null ? null : pawn.def.defName)),
                Json.Prop("gender", Json.String(pawn.gender.ToString())),
                Json.Prop("ageBiologicalYears", Json.Number(pawn.ageTracker == null ? 0 : pawn.ageTracker.AgeBiologicalYears)),
                Json.Prop("downed", Json.Bool(pawn.Downed)),
                Json.Prop("drafted", Json.Bool(pawn.Drafted)),
                Json.Prop("position", SerializeCell(pawn.Position)));
        }

        private static string SerializePawnDetail(Pawn pawn)
        {
            return Json.Object(
                Json.Prop("summary", SerializePawnSummary(pawn)),
                Json.Prop("faction", Json.String(pawn.Faction == null ? null : pawn.Faction.Name)),
                Json.Prop("mentalState", Json.String(pawn.MentalStateDef == null ? null : pawn.MentalStateDef.defName)),
                Json.Prop("mood", Json.Number(pawn.needs == null || pawn.needs.mood == null ? 0f : pawn.needs.mood.CurLevelPercentage)),
                Json.Prop("skills", Json.Array(SerializeSkills(pawn))),
                Json.Prop("traits", Json.Array(SerializeTraits(pawn))),
                Json.Prop("health", SerializePawnHealth(pawn)));
        }

        private static string SerializePawnHealth(Pawn pawn)
        {
            IEnumerable<string> hediffs = pawn.health == null || pawn.health.hediffSet == null
                ? Enumerable.Empty<string>()
                : pawn.health.hediffSet.hediffs.Select(h => Json.Object(
                    Json.Prop("defName", Json.String(h.def == null ? null : h.def.defName)),
                    Json.Prop("label", Json.String(h.LabelCap)),
                    Json.Prop("severity", Json.Number(h.Severity)),
                    Json.Prop("part", Json.String(h.Part == null ? null : h.Part.LabelCap))));

            return Json.Object(
                Json.Prop("dead", Json.Bool(pawn.Dead)),
                Json.Prop("downed", Json.Bool(pawn.Downed)),
                Json.Prop("pain", Json.Number(pawn.health == null || pawn.health.hediffSet == null ? 0f : pawn.health.hediffSet.PainTotal)),
                Json.Prop("hediffs", Json.Array(hediffs)));
        }

        private static IEnumerable<string> SerializeSkills(Pawn pawn)
        {
            if (pawn.skills == null)
            {
                yield break;
            }
            foreach (SkillRecord skill in pawn.skills.skills.OrderBy(s => s.def.defName))
            {
                yield return Json.Object(
                    Json.Prop("defName", Json.String(skill.def.defName)),
                    Json.Prop("label", Json.String(skill.def.LabelCap)),
                    Json.Prop("level", Json.Number(skill.Level)),
                    Json.Prop("passion", Json.String(skill.passion.ToString())));
            }
        }

        private static IEnumerable<string> SerializeTraits(Pawn pawn)
        {
            if (pawn.story == null || pawn.story.traits == null)
            {
                yield break;
            }
            foreach (Trait trait in pawn.story.traits.allTraits)
            {
                yield return Json.Object(
                    Json.Prop("defName", Json.String(trait.def == null ? null : trait.def.defName)),
                    Json.Prop("label", Json.String(trait.LabelCap)));
            }
        }

        private static string SerializeAnimal(Pawn pawn)
        {
            return Json.Object(
                Json.Prop("id", Json.String(pawn.ThingID)),
                Json.Prop("loadId", Json.String(pawn.GetUniqueLoadID())),
                Json.Prop("name", Json.String(pawn.Name == null ? pawn.LabelShortCap : pawn.Name.ToStringFull)),
                Json.Prop("defName", Json.String(pawn.def == null ? null : pawn.def.defName)),
                Json.Prop("gender", Json.String(pawn.gender.ToString())),
                Json.Prop("position", SerializeCell(pawn.Position)),
                Json.Prop("downed", Json.Bool(pawn.Downed)));
        }

        private static string SerializeCell(IntVec3 cell)
        {
            return Json.Object(
                Json.Prop("x", Json.Number(cell.x)),
                Json.Prop("y", Json.Number(cell.y)),
                Json.Prop("z", Json.Number(cell.z)));
        }

        private static string ReadStringMember(object instance, params string[] names)
        {
            foreach (string name in names)
            {
                PropertyInfo property = instance.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (property != null)
                {
                    object value = property.GetValue(instance, null);
                    return value == null ? null : value.ToString();
                }

                FieldInfo field = instance.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null)
                {
                    object value = field.GetValue(instance);
                    return value == null ? null : value.ToString();
                }
            }
            return null;
        }

        private static string ReadDefName(object instance, string name)
        {
            FieldInfo field = instance.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            object def = field == null ? null : field.GetValue(instance);
            if (def == null)
            {
                return null;
            }
            FieldInfo defName = def.GetType().GetField("defName", BindingFlags.Public | BindingFlags.Instance);
            object value = defName == null ? null : defName.GetValue(def);
            return value == null ? null : value.ToString();
        }

        private static string CurrentResearchDefName()
        {
            if (Find.ResearchManager == null)
            {
                return null;
            }

            FieldInfo field = typeof(ResearchManager).GetField("currentProj", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            ResearchProjectDef project = field == null ? null : field.GetValue(Find.ResearchManager) as ResearchProjectDef;
            return project == null ? null : project.defName;
        }

        private sealed class CorePawn
        {
            public readonly Pawn Pawn;
            public readonly string ColonyRole;

            public CorePawn(Pawn pawn, string colonyRole)
            {
                Pawn = pawn;
                ColonyRole = colonyRole;
            }
        }
    }
}
