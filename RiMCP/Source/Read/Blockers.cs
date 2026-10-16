using System.Collections.Generic;
using RimWorld;
using Verse;

namespace RiMCP.Read
{
    // Why progress cannot happen, as entries an agent can act on: a kind, the ids and numbers that
    // matter, and a message. They describe the colony when read, so reads work them out again
    // rather than keeping them.
    internal static class Blockers
    {
        public static Dictionary<string, object> Of(string kind, string message, params Dto.JsonField[] fields)
        {
            Dictionary<string, object> blocker = Dto.Obj(Dto.Field("kind", kind));
            foreach (Dto.JsonField field in fields)
            {
                if (field.Value != null)
                {
                    blocker[field.Name] = field.Value;
                }
            }
            blocker["message"] = message;
            return blocker;
        }

        public static object[] ToArray(List<object> blockers)
        {
            return blockers.Count == 0 ? null : blockers.ToArray();
        }

        // Whether anyone on the map could do this work now, as the game's build and crop menus
        // check before warning: a colonist who is not downed, has the work type on, and has the
        // skill (when there is one); or, with Biotech, a player mech that can.
        public static bool AnyWorker(Map map, WorkTypeDef work, SkillDef skill, int minSkill)
        {
            foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
            {
                if (!pawn.Downed && pawn.workSettings != null && pawn.workSettings.WorkIsActive(work) && (skill == null || pawn.skills == null ? minSkill <= 0 : pawn.skills.GetSkill(skill).Level >= minSkill))
                {
                    return true;
                }
            }
            Pawn mech;
            return ModsConfig.BiotechActive && MechanitorUtility.AnyPlayerMechCanDoWork(work, minSkill, out mech);
        }
    }
}
