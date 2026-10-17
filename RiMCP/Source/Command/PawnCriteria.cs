using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RiMCP.Command
{
    // What a starting colonist must be: minimum skills (optionally with a passion), traits wanted
    // or excluded, work types it must be able to do, and an age range. Unmet lists every criterion a
    // pawn fails, so a reroll search can say what is still missing.
    internal sealed class PawnCriteria
    {
        public List<SkillNeed> Skills = new List<SkillNeed>();
        public List<TraitDef> Traits = new List<TraitDef>();
        public List<TraitDef> ExcludeTraits = new List<TraitDef>();
        public List<WorkTypeDef> CanDo = new List<WorkTypeDef>();
        public int? MinAge;
        public int? MaxAge;

        public sealed class SkillNeed
        {
            public SkillDef Skill;
            public int Min;
            public Passion Passion;
        }

        public bool Empty
        {
            get { return Skills.Count == 0 && Traits.Count == 0 && ExcludeTraits.Count == 0 && CanDo.Count == 0 && !MinAge.HasValue && !MaxAge.HasValue; }
        }

        public List<string> Unmet(Pawn pawn)
        {
            List<string> unmet = new List<string>();
            foreach (SkillNeed need in Skills)
            {
                SkillRecord record = pawn.skills == null ? null : pawn.skills.GetSkill(need.Skill);
                int level = record == null || record.TotallyDisabled ? -1 : record.Level;
                Passion passion = record == null ? Passion.None : record.passion;
                if (level < need.Min || passion < need.Passion)
                {
                    unmet.Add(need.Skill.defName + " " + need.Min + "+" + (need.Passion == Passion.None ? "" : " with a " + need.Passion.ToString().ToLowerInvariant() + " passion")
                        + " (has " + (level < 0 ? "none" : level + (passion == Passion.None ? "" : ", " + passion.ToString().ToLowerInvariant())) + ")");
                }
            }
            foreach (TraitDef trait in Traits)
            {
                if (pawn.story == null || !pawn.story.traits.HasTrait(trait))
                {
                    unmet.Add("trait " + trait.defName);
                }
            }
            foreach (TraitDef trait in ExcludeTraits)
            {
                if (pawn.story != null && pawn.story.traits.HasTrait(trait))
                {
                    unmet.Add("no trait " + trait.defName);
                }
            }
            foreach (WorkTypeDef work in CanDo)
            {
                if (pawn.WorkTypeIsDisabled(work))
                {
                    unmet.Add("can do " + work.defName);
                }
            }
            int age = pawn.ageTracker == null ? 0 : pawn.ageTracker.AgeBiologicalYears;
            if (MinAge.HasValue && age < MinAge.Value)
            {
                unmet.Add("age " + MinAge.Value + "+ (is " + age + ")");
            }
            if (MaxAge.HasValue && age > MaxAge.Value)
            {
                unmet.Add("age up to " + MaxAge.Value + " (is " + age + ")");
            }
            return unmet;
        }
    }
}
