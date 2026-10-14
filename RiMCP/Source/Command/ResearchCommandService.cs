using System.Runtime.Serialization;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Read;
using Verse;

namespace RiMCP.Command
{
    internal static class ResearchCommandService
    {
        public static BridgeResponse SetCurrent(BridgeRequest request, RouteMatch route)
        {
            SetResearchProjectBody body = CommandUtil.ReadBody<SetResearchProjectBody>(request);
            if (string.IsNullOrWhiteSpace(body.ProjectDefName))
            {
                throw new CommandException(400, "Missing required field 'projectDefName'.");
            }
            if (Current.Game == null || Find.ResearchManager == null)
            {
                throw new CommandException(409, "Research manager is not available.");
            }

            ResearchProjectDef project = DefDatabase<ResearchProjectDef>.GetNamedSilentFail(body.ProjectDefName);
            if (project == null)
            {
                throw new CommandException(404, "Research project not found.");
            }
            if (project.IsFinished)
            {
                throw new CommandException(409, "Research project is already finished.");
            }
            if (!project.CanStartNow)
            {
                throw new CommandException(409, "Research project cannot be started now.");
            }

            // Anomaly knowledge projects occupy one slot per knowledge category, alongside the
            // normal project, so compare against the slot this project will replace.
            CommandContext context = CommandUtil.ContextFor(null);
            ResearchManager manager = Find.ResearchManager;
            KnowledgeCategoryDef category = ModsConfig.AnomalyActive ? project.knowledgeCategory : null;
            ResearchProjectDef previous = manager.GetProject(category);
            bool changed = !manager.IsCurrentProject(project);
            if (changed)
            {
                manager.SetCurrentProject(project);
            }

            return CommandEnvelope.Ok(context, changed, Dto.Obj(
                Dto.Field("knowledgeCategory", category == null ? null : category.defName),
                Dto.Field("previousProject", ResearchReadService.SerializeProject(previous, ReadDetail.Summary)),
                Dto.Field("currentProject", ResearchReadService.SerializeProject(manager.GetProject(category), ReadDetail.Summary))));
        }

        [DataContract]
        private sealed class SetResearchProjectBody
        {
            [DataMember(Name = "projectDefName")]
            public string ProjectDefName { get; set; }
        }
    }
}
