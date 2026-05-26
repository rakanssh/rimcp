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

            ResearchProjectDef current = CurrentProject();
            IEnumerable<ResearchProjectDef> source = DefDatabase<ResearchProjectDef>.AllDefsListForReading
                .OrderBy(def => def.tab == null ? "" : def.tab.defName)
                .ThenBy(def => def.defName);
            Page<ResearchProjectDef> page = new Page<ResearchProjectDef>(source, context.Request);

            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("current", SerializeProject(current, ReadDetail.Full)),
                Dto.Field("projects", page.Items.Select(project => SerializeProject(project, context.Request.Detail)).ToArray())),
                page.Truncated,
                page.NextCursor);
        }

        public static ResearchProjectDef CurrentProject()
        {
            return Find.ResearchManager == null || CurrentProjectField == null ? null : CurrentProjectField.GetValue(Find.ResearchManager) as ResearchProjectDef;
        }

        public static object SerializeProject(ResearchProjectDef project, ReadDetail detail)
        {
            if (project == null)
            {
                return null;
            }

            Dictionary<string, object> dto = Dto.Obj(
                Dto.Field("defName", project.defName),
                Dto.Field("label", ReadUtil.DefLabel(project)),
                Dto.Field("tab", project.tab == null ? null : project.tab.defName),
                Dto.Field("techLevel", project.techLevel.ToString()),
                Dto.Field("baseCost", project.baseCost),
                Dto.Field("finished", project.IsFinished),
                Dto.Field("progress", Progress(project)));

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

        private static float Progress(ResearchProjectDef project)
        {
            return Find.ResearchManager == null ? 0f : Find.ResearchManager.GetProgress(project);
        }
    }
}
