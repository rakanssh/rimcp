using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RiMCP.Bridge;
using Verse;

namespace RiMCP.Read
{
    internal static class ResearchReadService
    {
        public static BridgeResponse GetResearch(ReadContext context)
        {
            if (!ReadUtil.ChangedSince(context))
            {
                return ReadEnvelope.NotChanged(context);
            }

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
            return Find.ResearchManager == null ? null : Reflect.Read(Find.ResearchManager, "currentProj") as ResearchProjectDef;
        }

        public static object SerializeProject(ResearchProjectDef project, ReadDetail detail)
        {
            if (project == null)
            {
                return null;
            }

            Dictionary<string, object> dto = Dto.Obj(
                Dto.Field("defName", project.defName),
                Dto.Field("label", project.LabelCap),
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
            object progress = Reflect.Invoke(Find.ResearchManager, "GetProgress", project);
            if (progress == null)
            {
                return 0f;
            }
            try
            {
                return System.Convert.ToSingle(progress, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                return 0f;
            }
        }
    }
}
