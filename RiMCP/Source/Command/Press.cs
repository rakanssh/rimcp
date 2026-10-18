using System;
using System.Collections.Generic;
using RiMCP.Bridge;
using Verse;

namespace RiMCP.Command
{
    // Every agent press runs through Execute: a right-click option, a button, a letter's option, or a
    // dialog answer.
    internal static class Press
    {
        public static void Check(ActionOrigin origin)
        {
            if (origin.IsMod && !BridgeRuntime.ModActionsAllowed)
            {
                throw new CommandException(403, "Mods' menu options and buttons are off in RimMCP settings (Safety). This one is from " + origin.Mod + ".");
            }
            if (Targets.Active())
            {
                throw new CommandException(409, "The player is choosing a target in game; try again when they finish.");
            }
        }

        // settle runs once the press has run, even when it failed.
        public static void Execute(Dictionary<string, object> data, ActionOrigin origin, string tool, string subject, Thing selected, Map map, LocalTargetInfo aim, Action press, Action settle = null, Interaction continuing = null)
        {
            Check(origin);
            List<Window> before = FollowUps.Snapshot();
            try
            {
                // Targeting callbacks may read the selection, so it stays the agent's until targeting ends.
                Selection.With(selected, () =>
                {
                    try
                    {
                        press();
                    }
                    catch
                    {
                        if (Targets.Active())
                        {
                            Targets.Cancel();
                        }
                        throw;
                    }
                    Dictionary<string, object> targeting = Targets.Resolve(aim, map);
                    if (targeting != null)
                    {
                        data["targeting"] = targeting;
                        if (!origin.IsMod)
                        {
                            data["unforeseen"] = true;
                            Log.Warning("RimMCP: '" + subject + "' (" + origin.Type + ") started targeting, which its classification did not foresee.");
                        }
                    }
                });
            }
            finally
            {
                if (settle != null)
                {
                    settle();
                }
            }
            FollowUps.Report(data, tool, subject, FollowUps.OpenedSince(before), continuing, selected, origin);
        }
    }
}
