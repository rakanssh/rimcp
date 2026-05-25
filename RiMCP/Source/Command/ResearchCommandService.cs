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
            if (Find.ResearchManager == null)
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

            CommandContext context = CommandUtil.ContextFor(null);
            ResearchProjectDef previous = ResearchReadService.CurrentProject();
            bool changed = previous != project;
            if (changed)
            {
                Find.ResearchManager.SetCurrentProject(project);
            }

            return CommandEnvelope.Ok(context, changed, Dto.Obj(
                Dto.Field("previousProject", ResearchReadService.SerializeProject(previous, ReadDetail.Summary)),
                Dto.Field("currentProject", ResearchReadService.SerializeProject(ResearchReadService.CurrentProject(), ReadDetail.Summary))));
        }

        [DataContract]
        private sealed class SetResearchProjectBody
        {
            [DataMember(Name = "projectDefName")]
            public string ProjectDefName { get; set; }
        }
    }
}
