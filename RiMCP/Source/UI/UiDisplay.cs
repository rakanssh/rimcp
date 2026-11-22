using System;
using RimWorld;
using UnityEngine;
using Verse;

namespace RiMCP.UI
{
    internal readonly struct DisplayText
    {
        private DisplayText(string value)
        {
            Value = value;
        }

        public string Value { get; }

        // Display data or previously rendered text unchanged
        public static DisplayText Raw(string value) => new DisplayText(value);
    }

    internal static class UiDisplay
    {
        public static void Label(Rect rect, DisplayText text) => Widgets.Label(rect, text.Value);

        public static void Label(Listing_Standard listing, DisplayText text) => listing.Label(text.Value);

        public static void Checkbox(Rect rect, DisplayText text, ref bool value) => Widgets.CheckboxLabeled(rect, text.Value, ref value);

        public static void Checkbox(Listing_Standard listing, DisplayText text, ref bool value, DisplayText tip) =>
            listing.CheckboxLabeled(text.Value, ref value, tip.Value);

        public static void Tip(Rect rect, DisplayText text) => TooltipHandler.TipRegion(rect, text.Value);

        public static void Tip(Rect rect, Func<DisplayText> text, int id) => TooltipHandler.TipRegion(rect, new TipSignal(() => text().Value, id));

        public static FloatMenuOption Option(DisplayText text, Action action) => new FloatMenuOption(text.Value, action);

        public static void GameMessage(TaggedString text, MessageTypeDef type, bool historical) => Messages.Message(text, type, historical);

        public static void GameMessage(TaggedString text, LookTargets targets, MessageTypeDef type, bool historical) =>
            Messages.Message(text, targets, type, historical);

        // The string overload capitalizes the first character before showing the message
        public static void Message(DisplayText text, MessageTypeDef type, bool historical)
        {
            if (!string.IsNullOrEmpty(text.Value))
            {
                Messages.Message(CreateMessage(text, type, null), historical);
            }
        }

        public static Message CreateMessage(DisplayText text, MessageTypeDef type, LookTargets targets) => new Message(text.Value, type, targets);

        public static Letter CreateLetter(DisplayText title, DisplayText text, LetterDef type, LookTargets targets) =>
            LetterMaker.MakeLetter(title.Value, text.Value, type, targets);

        public static Dialog_MessageBox Dialog(DisplayText text, DisplayText confirm, Action action, DisplayText cancel, DisplayText title = default) =>
            new Dialog_MessageBox(text.Value, confirm.Value, action, cancel.Value, null, title.Value);

        // Draw without the text-anchor parameter of Widgets.ButtonText so the existing Unity references suffice
        public static bool Button(Rect rect, DisplayText label, bool active = true)
        {
            Color previous = GUI.color;
            if (!active)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.5f);
            }
            Widgets.DrawButtonGraphic(rect);
            float width = Math.Min(rect.width, Text.CalcSize(label.Value).x + 1f);
            LabelMiddle(new Rect(rect.center.x - width / 2f, rect.y, width, rect.height), label);
            GUI.color = previous;
            if (!active)
            {
                return false;
            }
            Widgets.DrawHighlightIfMouseover(rect);
            return Widgets.ButtonInvisible(rect);
        }

        public static void LabelMiddle(Rect rect, DisplayText text)
        {
            Label(new Rect(rect.x, rect.y + (rect.height - Text.LineHeight) / 2f, rect.width, Text.LineHeight), text);
        }

        public static void LabelRight(Rect rect, DisplayText text)
        {
            float width = Math.Min(rect.width, Text.CalcSize(text.Value).x + 1f);
            Label(new Rect(rect.xMax - width, rect.y + (rect.height - Text.LineHeight) / 2f, width, Text.LineHeight), text);
        }
    }
}
