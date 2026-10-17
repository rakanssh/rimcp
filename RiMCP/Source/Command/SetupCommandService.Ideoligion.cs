using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using RimWorld;
using RimWorld.Planet;
using RiMCP.Bridge;
using RiMCP.Read;
using RiMCP.Shared;
using Verse;

namespace RiMCP.Command
{
    // The ideoligion screens: classic, a preset, a saved one, or a custom one created and edited.
    internal static partial class SetupCommandService
    {
        public static BridgeResponse SetIdeoligion(BridgeRequest request, RouteMatch route)
        {
            IdeoligionBody body = CommandUtil.ReadBody<IdeoligionBody>(request);
            if (string.Equals((body.Mode ?? "").Trim(), "saved", StringComparison.OrdinalIgnoreCase))
            {
                return LoadSavedIdeoligion(request, body);
            }
            Page_ChooseIdeoPreset page = SetupController.Require<Page_ChooseIdeoPreset>(SetupController.Ideoligion);
            SetupController.RequireRevision(body.ExpectedRevision);
            string mode = (body.Mode ?? "").Trim();
            Type selection = typeof(Page_ChooseIdeoPreset).GetNestedType("PresetSelection", System.Reflection.BindingFlags.NonPublic);
            if (selection == null)
            {
                throw new CommandException(501, "This game version's ideoligion screen cannot be driven; choose in game.");
            }
            MemeDef customStructure = null;
            List<MemeDef> customMemes = null;
            if (string.Equals(mode, "classic", StringComparison.OrdinalIgnoreCase))
            {
                SetupController.Set(page, "presetSelection", Enum.Parse(selection, "Classic"));
            }
            else if (string.Equals(mode, "preset", StringComparison.OrdinalIgnoreCase))
            {
                IdeoPresetDef preset = Presets().FirstOrDefault(def => string.Equals(def.defName, (body.Preset ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
                if (preset == null)
                {
                    throw new CommandException(400, "Field 'preset' must be one of the screen's presets; get_setup lists them.");
                }
                MemeDef structure = null;
                if (!string.IsNullOrWhiteSpace(body.Structure))
                {
                    structure = Structures().FirstOrDefault(def => string.Equals(def.defName, body.Structure.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (structure == null)
                    {
                        throw new CommandException(400, "Field 'structure' must be a structure meme the player faction may use; get_setup lists them.");
                    }
                }
                SetupController.Set(page, "presetSelection", Enum.Parse(selection, "Preset"));
                SetupController.Set(page, "selectedIdeo", preset);
                SetupController.Set(page, "selectedStructure", structure);
            }
            else if (string.Equals(mode, "customFixed", StringComparison.OrdinalIgnoreCase) || string.Equals(mode, "customFluid", StringComparison.OrdinalIgnoreCase))
            {
                bool fluid = string.Equals(mode, "customFluid", StringComparison.OrdinalIgnoreCase);
                customStructure = MemeNamed(body.Structure, "structure");
                customMemes = MemesNamed(body.Memes);
                // Checked first as the meme dialogs the screen opens would check them, on a new
                // ideoligion that is never added to the game, so a refusal changes nothing.
                Ideo probe = IdeoUtility.MakeEmptyIdeo();
                probe.Fluid = fluid;
                List<string> problems = IdeoEditor.CheckMemes(probe, customStructure, customMemes, true);
                if (problems.Count > 0)
                {
                    throw new CommandException(409, "The meme dialog would not accept these memes: " + string.Join(" ", problems.ToArray()));
                }
                SetupController.Set(page, "presetSelection", Enum.Parse(selection, fluid ? "CustomFluid" : "CustomFixed"));
            }
            else
            {
                throw new CommandException(400, "Field 'mode' must be classic, preset, customFixed, customFluid, or saved.");
            }
            if (!SetupController.CanNext(page))
            {
                SetupController.Changed();
                throw new CommandException(409, "The ideoligion screen did not accept this choice.");
            }
            SetupController.DoNext(page);
            object outcome = null;
            if (customStructure != null)
            {
                Page_ConfigureIdeo custom = Find.WindowStack.WindowOfType<Page_ConfigureIdeo>();
                if (custom == null)
                {
                    SetupController.Changed();
                    throw new CommandException(409, "The custom ideoligion screen did not open.");
                }
                outcome = CreateCustom(custom, customStructure, customMemes);
            }
            SetupController.Changed();
            Dictionary<string, object> state = (Dictionary<string, object>)State();
            if (outcome != null)
            {
                state["outcome"] = outcome;
            }
            return SessionCommandService.Envelope(true, state);
        }

        // The custom screen's Create button (fixed) or the fluid button's own start, then the meme
        // dialogs it opens, answered as their accept buttons are: the structure, then the memes.
        private static object CreateCustom(Page_ConfigureIdeo page, MemeDef structure, List<MemeDef> memes)
        {
            if (page.ideo == null)
            {
                page.SelectOrMakeNewIdeo();
            }
            Dialog_ChooseMemes dialog = Find.WindowStack.WindowOfType<Dialog_ChooseMemes>();
            if (dialog == null)
            {
                // An ideoligion chosen before, reopened: its memes change as an edit would.
                List<string> problems = IdeoEditor.CheckMemes(page.ideo, structure, memes, false);
                if (problems.Count > 0)
                {
                    return Dto.Obj(Dto.Field("memes", "rejected"), Dto.Field("reason", string.Join(" ", problems.ToArray())));
                }
                IdeoEditor.ApplyMemes(page.ideo, structure, memes);
                return Dto.Obj(Dto.Field("memes", "applied"), Dto.Field("regenerated", Regenerated));
            }
            string refusal = IdeoEditor.AnswerMemeDialog(dialog, new List<MemeDef> { structure });
            Dialog_ChooseMemes normal = Find.WindowStack.WindowOfType<Dialog_ChooseMemes>();
            if (refusal == null && normal != null && normal != dialog)
            {
                refusal = IdeoEditor.AnswerMemeDialog(normal, new List<MemeDef> { structure }.Concat(memes).ToList());
            }
            return Dto.Obj(Dto.Field("memes", refusal == null ? "applied" : "rejected"), Dto.Field("reason", refusal));
        }

        // Load saved, as an operation: the screen's Load saved, then the file's button in its list.
        // The game may ask about the file's game version or mods first; the operation then waits.
        private static BridgeResponse LoadSavedIdeoligion(BridgeRequest request, IdeoligionBody body)
        {
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            return SessionCommandService.Begin(request, "set_setup_ideoligion", requestId, () =>
            {
                Page_ChooseIdeoPreset page = SetupController.Require<Page_ChooseIdeoPreset>(SetupController.Ideoligion);
                SetupController.RequireRevision(body.ExpectedRevision);
                string file = SavedIdeoligions().FirstOrDefault(name => string.Equals(name, (body.File ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
                if (file == null)
                {
                    throw new CommandException(404, "Field 'file' must name a saved ideoligion; get_setup lists them.");
                }
                Type selection = typeof(Page_ChooseIdeoPreset).GetNestedType("PresetSelection", System.Reflection.BindingFlags.NonPublic);
                System.Reflection.MethodInfo pick = typeof(Dialog_IdeoList_Load).GetMethod("DoFileInteraction", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                if (selection == null || pick == null)
                {
                    throw new CommandException(501, "This game version's saved ideoligions cannot be loaded here; choose the file in game.");
                }
                int own = SetupController.Revision;
                return operation => LoadSaved(operation, page, file, Enum.Parse(selection, "Load"), pick, own);
            }, "saved", body.File, body.ExpectedRevision);
        }

        private static IEnumerable<string> LoadSaved(Operation operation, Page_ChooseIdeoPreset page, string file, object load, System.Reflection.MethodInfo pick, int own)
        {
            SetupController.Continue(operation, own);
            SetupController.Set(page, "presetSelection", load);
            if (!SetupController.CanNext(page))
            {
                SetupController.Changed();
                throw new OperationEnded(Operation.Failed, "The ideoligion screen did not accept Load saved.");
            }
            SetupController.DoNext(page);
            // Its own change, recorded before any question is asked, so the question is not stale.
            SetupController.Changed();
            Dialog_IdeoList_Load list = Find.WindowStack.WindowOfType<Dialog_IdeoList_Load>();
            if (list == null)
            {
                throw new OperationEnded(Operation.Failed, "The saved ideoligion list did not open.");
            }
            List<Window> listed = Find.WindowStack.Windows.ToList();
            pick.Invoke(list, new object[] { file });
            if (Find.WindowStack.IsOpen(page))
            {
                List<Window> asking = Find.WindowStack.Windows.Where(window => !listed.Contains(window)).ToList();
                if (asking.Count == 0)
                {
                    list.Close();
                    SetupController.Changed();
                    throw new OperationEnded(Operation.Failed, "The game could not load '" + file + "'; get_game_log may say why.");
                }
                foreach (string step in AwaitQuestion(operation, asking, asking.Any(window => window is Dialog_ModMismatch) ? "modMismatch" : "versionMismatch", () => !Find.WindowStack.IsOpen(page)))
                {
                    yield return step;
                }
                if (Find.WindowStack.IsOpen(page))
                {
                    SetupController.Changed();
                    throw new OperationEnded(Operation.Declined, "Loading '" + file + "' was not confirmed.");
                }
            }
            SetupController.Changed();
            Dictionary<string, object> state = (Dictionary<string, object>)State();
            state["loaded"] = file;
            operation.Result = state;
        }

        private static IEnumerable<string> SavedIdeoligions()
        {
            return GenFilePaths.AllCustomIdeoFiles.Select(file => System.IO.Path.GetFileNameWithoutExtension(file.Name));
        }

        public static BridgeResponse EditIdeoligion(BridgeRequest request, RouteMatch route)
        {
            EditIdeoBody body = CommandUtil.ReadBody<EditIdeoBody>(request);
            Page_ConfigureIdeo page = SetupController.Require<Page_ConfigureIdeo>(SetupController.CustomIdeoligion);
            SetupController.RequireRevision(body.ExpectedRevision);
            Window busy = Find.WindowStack.Windows.FirstOrDefault(window => !(window is Page) && window.absorbInputAroundWindow || window is FloatMenu);
            if (busy != null)
            {
                throw new CommandException(409, "A " + busy.GetType().Name + " is open on the ideoligion screen; finish it in game first.");
            }
            // Everything named is checked to exist before anything changes.
            MemeDef structure = body.Structure == null ? null : MemeNamed(body.Structure, "structure");
            List<MemeDef> memes = body.Memes == null ? null : MemesNamed(body.Memes);
            CultureDef culture = body.Culture == null ? null : DefNamed<CultureDef>(body.Culture, "culture");
            List<StyleCategoryDef> styles = body.Styles == null ? null : body.Styles.Select(name => IdeoEditor.Styles().FirstOrDefault(style => string.Equals(style.defName, (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) ?? Refuse<StyleCategoryDef>("Field 'styles' has an unknown or fixed-only StyleCategoryDef: " + name + ".")).ToList();
            if (styles != null && (styles.Count == 0 || styles.Count > 3 || styles.Distinct().Count() != styles.Count))
            {
                throw new CommandException(400, "Field 'styles' takes one to three different styles, the most dominant first.");
            }
            foreach (KeyValuePair<string, string> symbol in new[] { Pair("name", body.Name), Pair("adjective", body.Adjective), Pair("memberName", body.MemberName) })
            {
                if (symbol.Value != null && (symbol.Value.Trim().Length == 0 || !IdeoEditor.ValidSymbol(symbol.Value.Trim(), IdeoEditor.MaxSymbolLength)))
                {
                    throw new CommandException(400, "Field '" + symbol.Key + "' takes up to " + IdeoEditor.MaxSymbolLength + " letters, digits, spaces, apostrophes, and hyphens, as the symbols dialog does.");
                }
            }
            string randomize = body.Randomize == null ? null : body.Randomize.Trim().ToLowerInvariant();
            if (randomize != null && randomize != "all" && randomize != "precepts")
            {
                throw new CommandException(400, "Field 'randomize' must be all or precepts.");
            }
            List<PreceptEdit> edits = (body.Precepts ?? new PreceptEdit[0]).ToList();
            if (edits.Count > 40 || edits.Any(edit => edit == null))
            {
                throw new CommandException(400, "Field 'precepts' takes up to 40 edits.");
            }
            foreach (PreceptEdit edit in edits)
            {
                ValidateEdit(edit);
            }
            if (page.ideo == null)
            {
                if (structure == null || memes == null)
                {
                    throw new CommandException(409, "No ideoligion has been created on this screen yet; give structure and memes to create one.");
                }
                Ideo probe = IdeoUtility.MakeEmptyIdeo();
                List<string> problems = IdeoEditor.CheckMemes(probe, structure, memes, true);
                if (problems.Count > 0)
                {
                    throw new CommandException(409, "The meme dialog would not accept these memes: " + string.Join(" ", problems.ToArray()));
                }
            }

            List<object> outcomes = new List<object>();
            Ideo ideo = page.ideo;
            bool stopped = false;
            if (ideo == null)
            {
                outcomes.Add(Outcome("create", CreateCustom(page, structure, memes)));
                ideo = page.ideo;
                structure = null;
                memes = null;
            }
            if (randomize != null && ideo != null)
            {
                IdeoEditor.Randomize(ideo, randomize == "all");
                outcomes.Add(Dto.Obj(Dto.Field("part", "randomize"), Dto.Field("outcome", "applied"), Dto.Field("randomized", randomize)));
            }
            if ((structure != null || memes != null) && ideo != null)
            {
                MemeDef wantedStructure = structure ?? ideo.memes.First(meme => meme.category == MemeCategory.Structure);
                List<MemeDef> wantedMemes = memes ?? ideo.memes.Where(meme => meme.category == MemeCategory.Normal).ToList();
                if (wantedStructure == ideo.memes.FirstOrDefault(meme => meme.category == MemeCategory.Structure) && new HashSet<MemeDef>(wantedMemes).SetEquals(ideo.memes.Where(meme => meme.category == MemeCategory.Normal)))
                {
                    outcomes.Add(Dto.Obj(Dto.Field("part", "memes"), Dto.Field("outcome", "alreadySatisfied")));
                }
                else
                {
                    List<string> problems = IdeoEditor.CheckMemes(ideo, wantedStructure, wantedMemes, false);
                    if (problems.Count > 0)
                    {
                        outcomes.Add(Dto.Obj(Dto.Field("part", "memes"), Dto.Field("outcome", "rejected"), Dto.Field("reason", string.Join(" ", problems.ToArray()))));
                        stopped = true;
                    }
                    else
                    {
                        IdeoEditor.ApplyMemes(ideo, wantedStructure, wantedMemes);
                        outcomes.Add(Dto.Obj(Dto.Field("part", "memes"), Dto.Field("outcome", "applied"), Dto.Field("regenerated", Regenerated)));
                    }
                }
            }
            if (!stopped && ideo != null)
            {
                if (culture != null)
                {
                    bool same = ideo.culture == culture;
                    if (!same)
                    {
                        IdeoEditor.SetCulture(ideo, culture);
                    }
                    outcomes.Add(Dto.Obj(Dto.Field("part", "culture"), Dto.Field("outcome", same ? "alreadySatisfied" : "applied")));
                }
                if (styles != null)
                {
                    bool same = ideo.thingStyleCategories.Select(style => style.category).SequenceEqual(styles);
                    if (!same)
                    {
                        IdeoEditor.SetStyles(ideo, styles);
                    }
                    outcomes.Add(Dto.Obj(Dto.Field("part", "styles"), Dto.Field("outcome", same ? "alreadySatisfied" : "applied")));
                }
                Dictionary<int, int> renumbered = new Dictionary<int, int>();
                foreach (PreceptEdit edit in edits)
                {
                    outcomes.Add(ApplyEdit(ideo, edit, renumbered));
                }
                foreach (KeyValuePair<string, string> symbol in new[] { Pair("name", body.Name), Pair("adjective", body.Adjective), Pair("memberName", body.MemberName) })
                {
                    if (symbol.Value == null)
                    {
                        continue;
                    }
                    string text = symbol.Value.Trim();
                    string current = symbol.Key == "name" ? ideo.name : symbol.Key == "adjective" ? ideo.adjective : ideo.memberName;
                    if (current == text)
                    {
                        outcomes.Add(Dto.Obj(Dto.Field("part", symbol.Key), Dto.Field("outcome", "alreadySatisfied")));
                        continue;
                    }
                    if (symbol.Key == "name")
                    {
                        ideo.name = text;
                    }
                    else if (symbol.Key == "adjective")
                    {
                        ideo.adjective = text;
                    }
                    else
                    {
                        ideo.memberName = text;
                        ideo.MakeMemeberNamePluralDirty();
                    }
                    outcomes.Add(Dto.Obj(Dto.Field("part", symbol.Key), Dto.Field("outcome", "applied")));
                }
            }
            SetupController.Changed();
            if (body.Done == true && !stopped && ideo != null)
            {
                string blocker = IdeoEditor.Blocker(ideo);
                if (blocker != null)
                {
                    outcomes.Add(Dto.Obj(Dto.Field("part", "done"), Dto.Field("outcome", "rejected"), Dto.Field("reason", blocker)));
                }
                else if (!SetupController.CanNext(page))
                {
                    outcomes.Add(Dto.Obj(Dto.Field("part", "done"), Dto.Field("outcome", "rejected"), Dto.Field("reason", "The ideoligion screen did not accept it; see the message in game.")));
                }
                else
                {
                    SetupController.DoNext(page);
                    outcomes.Add(Dto.Obj(Dto.Field("part", "done"), Dto.Field("outcome", "applied")));
                }
                SetupController.Changed();
            }
            Dictionary<string, object> state = (Dictionary<string, object>)State();
            state["outcomes"] = outcomes.ToArray();
            return SessionCommandService.Envelope(true, state);
        }

        // What the meme dialog's accept regenerates on an ideoligion that already has precepts.
        private static readonly string[] Regenerated = { "precepts", "styles", "description" };

        private static object Outcome(string part, object created)
        {
            Dictionary<string, object> outcome = (Dictionary<string, object>)created;
            outcome["part"] = part;
            outcome["outcome"] = outcome["memes"];
            outcome.Remove("memes");
            return outcome;
        }

        private static KeyValuePair<string, string> Pair(string key, string value)
        {
            return new KeyValuePair<string, string>(key, value);
        }

        private static T Refuse<T>(string message)
        {
            throw new CommandException(400, message);
        }

        private static void ValidateEdit(PreceptEdit edit)
        {
            int actions = (edit.Add != null ? 1 : 0) + (edit.ReplaceWith != null ? 1 : 0) + (edit.Regenerate == true ? 1 : 0) + (edit.Rename != null ? 1 : 0) + (edit.Remove == true ? 1 : 0);
            if (actions != 1)
            {
                throw new CommandException(400, "Each precept edit does one thing: add, or with id one of replaceWith, regenerate, rename, or remove.");
            }
            if (edit.Add == null && !edit.Id.HasValue)
            {
                throw new CommandException(400, "Precept edits other than add need the precept's id from get_setup.");
            }
            if (edit.Add != null)
            {
                DefNamed<PreceptDef>(edit.Add.Precept, "add.precept");
                if (edit.Add.Pattern != null)
                {
                    DefNamed<RitualPatternDef>(edit.Add.Pattern, "add.pattern");
                }
                if (edit.Add.Thing != null)
                {
                    DefNamed<ThingDef>(edit.Add.Thing, "add.thing");
                }
                if (edit.Add.Weapons != null)
                {
                    DefNamed<WeaponClassPairDef>(edit.Add.Weapons, "add.weapons");
                }
                if (edit.Add.Xenotype != null)
                {
                    DefNamed<XenotypeDef>(edit.Add.Xenotype, "add.xenotype");
                }
            }
            if (edit.ReplaceWith != null)
            {
                DefNamed<PreceptDef>(edit.ReplaceWith, "replaceWith");
            }
            if (edit.Rename != null && (edit.Rename.Trim().Length == 0 || !IdeoEditor.ValidSymbol(edit.Rename.Trim(), IdeoEditor.MaxPreceptNameLength)))
            {
                throw new CommandException(400, "Field 'rename' takes up to " + IdeoEditor.MaxPreceptNameLength + " letters, digits, spaces, apostrophes, and hyphens, as the precept dialog does.");
            }
        }

        // One precept edit, as the screen's Add buttons and precept boxes offer it.
        // Regenerating or replacing a precept gives it a new id, as the game does; later edits in the
        // same request that name the old id follow it.
        private static object ApplyEdit(Ideo ideo, PreceptEdit edit, Dictionary<int, int> renumbered)
        {
            if (edit.Add != null)
            {
                PreceptDef def = DefNamed<PreceptDef>(edit.Add.Precept, "add.precept");
                List<PreceptOption> matching = IdeoEditor.AddOptions(ideo).Where(option => option.Def == def
                    && (edit.Add.Pattern == null || (option.Pattern != null && option.Pattern.defName == edit.Add.Pattern.Trim()))
                    && (edit.Add.Thing == null || (option.Thing != null && option.Thing.defName == edit.Add.Thing.Trim()))
                    && (edit.Add.Weapons == null || (option.Weapons != null && option.Weapons.defName == edit.Add.Weapons.Trim()))
                    && (edit.Add.Xenotype == null || (option.Xenotype != null && option.Xenotype.defName == edit.Add.Xenotype.Trim()))).ToList();
                List<PreceptOption> usable = matching.Where(option => option.Reason == null).ToList();
                Precept occupying = def.issue == null || matching.Count > 0 || !ideo.HasMaxPreceptsForIssue(def.issue) ? null : ideo.PreceptsListForReading.FirstOrDefault(other => other.def.issue == def.issue);
                if (usable.Count == 0 && occupying != null)
                {
                    return Dto.Obj(Dto.Field("part", "precept"), Dto.Field("add", def.defName), Dto.Field("outcome", "rejected"), Dto.Field("reason", "Its issue already has " + occupying.def.defName + " (id " + occupying.Id + "); replace that one with replaceWith."));
                }
                if (usable.Count == 0)
                {
                    string reason = matching.Count > 0 ? matching[0].Reason : "The screen does not offer " + def.defName + (edit.Add.Thing != null || edit.Add.Weapons != null || edit.Add.Xenotype != null || edit.Add.Pattern != null ? " with that choice" : "") + " now; get_setup with include preceptOptions lists what it offers.";
                    return Dto.Obj(Dto.Field("part", "precept"), Dto.Field("add", def.defName), Dto.Field("outcome", "rejected"), Dto.Field("reason", reason));
                }
                if (usable.Count > 1 && (def.preceptClass == typeof(Precept_Weapon) || def.preceptClass == typeof(Precept_Xenotype) || usable.Any(option => option.Thing != null)))
                {
                    return Dto.Obj(Dto.Field("part", "precept"), Dto.Field("add", def.defName), Dto.Field("outcome", "rejected"), Dto.Field("reason", "Choose which " + (usable[0].Thing != null ? "thing" : usable[0].Weapons != null ? "weapons" : "xenotype") + " with it; get_setup with include preceptOptions lists them."));
                }
                Precept added = IdeoEditor.Add(ideo, usable[0]);
                return Dto.Obj(Dto.Field("part", "precept"), Dto.Field("add", def.defName), Dto.Field("outcome", "applied"), Dto.Field("id", added.Id), Dto.Field("label", IdeoEditor.Label(added)));
            }
            int id = edit.Id.Value;
            while (renumbered.ContainsKey(id))
            {
                id = renumbered[id];
            }
            Precept precept = ideo.PreceptsListForReading.FirstOrDefault(candidate => candidate.Id == id);
            if (precept == null)
            {
                return Dto.Obj(Dto.Field("part", "precept"), Dto.Field("id", edit.Id.Value), Dto.Field("outcome", "rejected"), Dto.Field("reason", "This ideoligion has no precept " + edit.Id.Value + "; get_setup lists its precepts."));
            }
            Dictionary<string, object> result = Dto.Obj(Dto.Field("part", "precept"), Dto.Field("id", precept.Id), Dto.Field("precept", precept.def.defName));
            if (edit.ReplaceWith != null)
            {
                PreceptDef with = DefNamed<PreceptDef>(edit.ReplaceWith, "replaceWith");
                if (with == precept.def)
                {
                    result["outcome"] = "alreadySatisfied";
                    return result;
                }
                PreceptOption option = IdeoEditor.Replacements(ideo, precept).FirstOrDefault(candidate => candidate.Def == with);
                if (option == null || option.Reason != null)
                {
                    result["outcome"] = "rejected";
                    result["reason"] = option != null ? option.Reason : "The precept's box does not offer " + with.defName + "; replaceWith lists what it offers.";
                    return result;
                }
                Precept replacement = IdeoEditor.Replace(ideo, precept, with);
                renumbered[precept.Id] = replacement.Id;
                result["outcome"] = "applied";
                result["replacedBy"] = Dto.Obj(Dto.Field("id", replacement.Id), Dto.Field("precept", with.defName));
                return result;
            }
            if (edit.Regenerate == true)
            {
                if (!precept.CanRegenerate)
                {
                    result["outcome"] = "rejected";
                    result["reason"] = "The precept's box does not offer Regenerate for this precept.";
                    return result;
                }
                int before = precept.Id;
                IdeoEditor.Regenerate(ideo, precept);
                renumbered[before] = precept.Id;
                result["outcome"] = "applied";
                result["newId"] = precept.Id;
                result["label"] = IdeoEditor.Label(precept);
                return result;
            }
            if (edit.Rename != null)
            {
                string name = edit.Rename.Trim();
                if (precept.def.preceptClass == typeof(Precept))
                {
                    result["outcome"] = "rejected";
                    result["reason"] = "Precepts by issue take no name.";
                    return result;
                }
                if (precept.LabelCap.ToString() == name)
                {
                    result["outcome"] = "alreadySatisfied";
                    return result;
                }
                IdeoEditor.Rename(ideo, precept, name);
                result["outcome"] = "applied";
                return result;
            }
            string refusal = IdeoEditor.WhyNotRemovable(ideo, precept);
            if (refusal != null)
            {
                result["outcome"] = "rejected";
                result["reason"] = refusal;
                return result;
            }
            ideo.RemovePrecept(precept);
            result["outcome"] = "applied";
            return result;
        }

        private static MemeDef MemeNamed(string name, string field)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new CommandException(400, "Field '" + field + "' must name a MemeDef; get_setup lists them.");
            }
            return DefNamed<MemeDef>(name, field);
        }

        private static List<MemeDef> MemesNamed(string[] names)
        {
            if (names == null || names.Length == 0)
            {
                throw new CommandException(400, "Field 'memes' must list the normal memes; get_setup lists them.");
            }
            List<MemeDef> memes = names.Select(name => DefNamed<MemeDef>(name, "memes")).ToList();
            if (memes.Distinct().Count() != memes.Count)
            {
                throw new CommandException(400, "Field 'memes' lists a meme twice.");
            }
            return memes;
        }

        // The presets the ideoligion screen lists: every category but classic, fluid, and custom,
        // which are the screen's own buttons.
        private static IEnumerable<IdeoPresetDef> Presets()
        {
            return DefDatabase<IdeoPresetDef>.AllDefs.Where(def => def.categoryDef != null
                && def.categoryDef != IdeoPresetCategoryDefOf.Classic
                && def.categoryDef != IdeoPresetCategoryDefOf.Custom
                && def.categoryDef != IdeoPresetCategoryDefOf.Fluid);
        }

        private static IEnumerable<MemeDef> Structures()
        {
            FactionDef player = Find.FactionManager.OfPlayer.def;
            return DefDatabase<MemeDef>.AllDefs.Where(meme => meme.category == MemeCategory.Structure && IdeoUtility.IsMemeAllowedFor(meme, player));
        }

        [DataContract]
        private sealed class IdeoligionBody
        {
            [DataMember(Name = "mode")] public string Mode { get; set; }
            [DataMember(Name = "preset")] public string Preset { get; set; }
            [DataMember(Name = "structure")] public string Structure { get; set; }
            [DataMember(Name = "memes")] public string[] Memes { get; set; }
            [DataMember(Name = "file")] public string File { get; set; }
            [DataMember(Name = "expectedRevision")] public int? ExpectedRevision { get; set; }
            [DataMember(Name = "requestId")] public string RequestId { get; set; }
        }

        [DataContract]
        private sealed class EditIdeoBody
        {
            [DataMember(Name = "randomize")] public string Randomize { get; set; }
            [DataMember(Name = "structure")] public string Structure { get; set; }
            [DataMember(Name = "memes")] public string[] Memes { get; set; }
            [DataMember(Name = "culture")] public string Culture { get; set; }
            [DataMember(Name = "styles")] public string[] Styles { get; set; }
            [DataMember(Name = "precepts")] public PreceptEdit[] Precepts { get; set; }
            [DataMember(Name = "name")] public string Name { get; set; }
            [DataMember(Name = "adjective")] public string Adjective { get; set; }
            [DataMember(Name = "memberName")] public string MemberName { get; set; }
            [DataMember(Name = "done")] public bool? Done { get; set; }
            [DataMember(Name = "expectedRevision")] public int? ExpectedRevision { get; set; }
        }

        [DataContract]
        private sealed class PreceptEdit
        {
            [DataMember(Name = "id")] public int? Id { get; set; }
            [DataMember(Name = "add")] public PreceptAdd Add { get; set; }
            [DataMember(Name = "replaceWith")] public string ReplaceWith { get; set; }
            [DataMember(Name = "regenerate")] public bool? Regenerate { get; set; }
            [DataMember(Name = "rename")] public string Rename { get; set; }
            [DataMember(Name = "remove")] public bool? Remove { get; set; }
        }

        [DataContract]
        private sealed class PreceptAdd
        {
            [DataMember(Name = "precept")] public string Precept { get; set; }
            [DataMember(Name = "pattern")] public string Pattern { get; set; }
            [DataMember(Name = "thing")] public string Thing { get; set; }
            [DataMember(Name = "weapons")] public string Weapons { get; set; }
            [DataMember(Name = "xenotype")] public string Xenotype { get; set; }
        }
    }
}
