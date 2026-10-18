using System;
using System.Collections.Generic;
using System.Reflection;
using RimWorld;
using Verse;

namespace RiMCP.Command
{
    // Some buttons act on whatever is selected, such as a bed's owner type. Selector.Select would switch
    // maps, move the camera, play sounds, and end the player's drag, so the selector's list is swapped.
    internal static class Selection
    {
        private static readonly FieldInfo Selected = typeof(Selector).GetField("selected", BindingFlags.Instance | BindingFlags.NonPublic);

        public static void With(Thing thing, Action act)
        {
            With(thing, () =>
            {
                act();
                return true;
            });
        }

        public static T With<T>(Thing thing, Func<T> act)
        {
            List<object> selected = thing == null ? null : Current();
            if (selected == null)
            {
                return act();
            }
            List<object> previous = new List<object>(selected);
            selected.Clear();
            selected.Add(thing);
            try
            {
                return act();
            }
            finally
            {
                selected.Clear();
                foreach (object each in previous)
                {
                    Thing owner = each as Thing;
                    if (owner == null || !owner.Destroyed)
                    {
                        selected.Add(each);
                    }
                }
            }
        }

        private static List<object> Current()
        {
            UIRoot_Play root = Find.UIRoot as UIRoot_Play;
            Selector selector = root == null || root.mapUI == null ? null : root.mapUI.selector;
            return selector == null || Selected == null ? null : Selected.GetValue(selector) as List<object>;
        }
    }
}
