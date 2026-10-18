using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RiMCP.Util;
using Verse;

namespace RiMCP.Read
{
    internal sealed class LetterChoice
    {
        public readonly int Index;
        public readonly string Id;
        public readonly string Text;
        public readonly string DisabledReason;

        public LetterChoice(int index, string id, string text, string disabledReason)
        {
            Index = index;
            Id = id;
            Text = text;
            DisabledReason = disabledReason;
        }
    }

    // Leaves out choices that only look at something: the camera, the Quests tab, an info card, or a
    // window of genes or the entity codex.
    internal static class LetterChoices
    {
        private static readonly FieldInfo OptionText = typeof(DiaOption).GetField("text", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly string[] ViewOnlyKeys = { "JumpToLocation", "ViewRelatedQuest", "ViewQuest", "ViewEntityCodex", "ViewGenes" };

        public static string Text(DiaOption option)
        {
            string text = OptionText == null ? null : OptionText.GetValue(option) as string;
            return string.IsNullOrEmpty(text) ? text : text.StripTags().Trim();
        }

        public static List<LetterChoice> Of(ChoiceLetter letter)
        {
            List<DiaOption> options;
            try
            {
                options = letter.Choices.ToList();
            }
            catch (Exception)
            {
                return new List<LetterChoice>();
            }
            string[] viewOnly = ViewOnlyKeys.Select(key => key.Translate().ToString()).ToArray();
            List<LetterChoice> choices = new List<LetterChoice>();
            for (int i = 0; i < options.Count; i++)
            {
                DiaOption option = options[i];
                string text = option == null ? null : Text(option);
                if (string.IsNullOrEmpty(text) || option.hyperlink.def != null || viewOnly.Any(view => text == view || text.StartsWith(view + ":")))
                {
                    continue;
                }
                string reason = option.disabled ? (string.IsNullOrEmpty(option.disabledReason) ? "Unavailable." : option.disabledReason.StripTags().Trim()) : null;
                choices.Add(new LetterChoice(i, (choices.Count + 1).ToString(), text, reason));
            }
            return choices;
        }

        // A letter whose only choice is Close is dismissed rather than answered.
        public static bool Answerable(List<LetterChoice> choices)
        {
            string close = "Close".Translate().ToString();
            return choices.Any(choice => choice.Text != close);
        }

        // Bound to the loaded game, so a revision read before another load never matches a letter
        // with the same id and options after it.
        public static string Revision(string loadId, Letter letter, List<LetterChoice> choices)
        {
            return Fingerprint.Of(loadId + "\n" + letter.ID + "\n" + string.Join("\n", choices.Select(choice => choice.Id + "\t" + choice.Text + "\t" + choice.DisabledReason).ToArray()));
        }
    }
}
