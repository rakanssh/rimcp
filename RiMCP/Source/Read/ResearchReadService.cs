using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using RiMCP.Bridge;
using Verse;

namespace RiMCP.Read
{
    internal static class ResearchReadService
    {
        // RimWorld keeps the current project private; keep this compatibility touchpoint local.
        private static readonly FieldInfo CurrentProjectField = typeof(ResearchManager).GetField("currentProj", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        public static BridgeResponse GetResearch(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.From(request);
            bool colony = Current.Game != null && Find.ResearchManager != null;
            bool availableOnly = context.Request.OptionalBool("availableOnly") == true;
            if (availableOnly && !colony)
            {
                throw new BridgeException(409, "availableOnly needs a loaded colony.");
            }

            ResearchProjectDef current = CurrentProject();
            IEnumerable<ResearchProjectDef> source = DefDatabase<ResearchProjectDef>.AllDefsListForReading
                .Where(def => !availableOnly || def.CanStartNow)
                .OrderBy(def => def.tab == null ? "" : def.tab.defName, StringComparer.Ordinal)
                .ThenBy(def => def.defName, StringComparer.Ordinal);
            Page<ResearchProjectDef> page = new Page<ResearchProjectDef>(source, context.Request);

            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("current", SerializeProject(current, ReadDetail.Full)),
                Dto.Field("blockers", colony ? Blockers.ToArray(ResearchBlockers()) : null),
                Dto.Field("projects", page.Items.Select(project => SerializeProject(project, context.Request.Detail)).ToArray())),
                page.Truncated,
                page.NextCursor);
        }

        // Why research is not progressing: no project, no bench the project can use (the bench
        // kind, its facilities, or power, as ResearchProjectDef.CanBeResearchedAt checks), or
        // nobody with Research on.
        public static List<object> ResearchBlockers()
        {
            List<object> blockers = new List<object>();
            ResearchProjectDef project = CurrentProject();
            if (project == null)
            {
                blockers.Add(Blockers.Of("noProject", "No research project is chosen; choose one with set_research_project."));
            }
            List<Building_ResearchBench> benches = Find.Maps
                .SelectMany(map => map.listerBuildings.allBuildingsColonist)
                .OfType<Building_ResearchBench>()
                .ToList();
            if (benches.Count == 0)
            {
                blockers.Add(Blockers.Of("missingResearchBench", "There is no research bench; build one, such as a SimpleResearchBench."));
            }
            else if (project != null && !benches.Any(bench => project.CanBeResearchedAt(bench, true)))
            {
                ThingDef required = project.requiredResearchBuilding;
                if (required != null && !benches.Any(bench => bench.def == required))
                {
                    blockers.Add(Blockers.Of("missingResearchBench", project.LabelCap + " needs a " + required.label + ".", Dto.Field("defName", required.defName)));
                }
                else
                {
                    string[] facilities = (project.requiredResearchFacilities ?? new List<ThingDef>()).Select(facility => facility.defName).ToArray();
                    blockers.Add(Blockers.Of("missingFacility", project.LabelCap + " needs these linked to a research bench: " + string.Join(", ", facilities) + ".", Dto.Field("defNames", facilities)));
                }
            }
            else if (project != null && !benches.Any(bench => project.CanBeResearchedAt(bench, false)))
            {
                blockers.Add(Blockers.Of("benchUnpowered", "No research bench that can do " + project.LabelCap + " has power."));
            }
            if (!Find.Maps.Any(map => Blockers.AnyWorker(map, WorkTypeDefOf.Research, SkillDefOf.Intellectual, 0)))
            {
                blockers.Add(Blockers.Of("noResearcher", "No colonist has Research on."));
            }
            return blockers;
        }

        public static ResearchProjectDef CurrentProject()
        {
            ResearchManager manager = Current.Game == null ? null : Find.ResearchManager;
            return manager == null || CurrentProjectField == null ? null : CurrentProjectField.GetValue(manager) as ResearchProjectDef;
        }

        public static object SerializeProject(ResearchProjectDef project, ReadDetail detail)
        {
            if (project == null)
            {
                return null;
            }

            ResearchManager manager = Current.Game == null ? null : Find.ResearchManager;
            Dictionary<string, object> dto = Dto.Obj(
                Dto.Field("defName", project.defName),
                Dto.Field("label", ReadUtil.DefLabel(project)),
                Dto.Field("tab", project.tab == null ? null : project.tab.defName),
                Dto.Field("techLevel", project.techLevel.ToString()),
                Dto.Field("baseCost", project.baseCost),
                Dto.Field("finished", manager == null ? (bool?)null : project.IsFinished),
                Dto.Field("progress", manager == null ? (float?)null : manager.GetProgress(project)));

            if (detail != ReadDetail.Summary)
            {
                dto["description"] = project.description;
                dto["prerequisites"] = project.prerequisites == null
                    ? new object[0]
                    : project.prerequisites.Select(prereq => prereq.defName).OrderBy(name => name).ToArray();
                dto["requiredResearchBuilding"] = project.requiredResearchBuilding == null ? null : project.requiredResearchBuilding.defName;
                dto["requiredResearchFacilities"] = project.requiredResearchFacilities == null
                    ? new object[0]
                    : project.requiredResearchFacilities.Select(facility => facility.defName).OrderBy(name => name).ToArray();
            }
            return dto;
        }
    }
}
