# RimMCP localization guide

[简体中文](README.localization.zh-CN.md)

## For translators

You can translate and test RimMCP using a built copy of the mod and a plain-text editor such as Notepad or VS Code. Save the XML as UTF-8.

### Add or update a language

1. Find the RimMCP mod folder, which contains the `About`, `Assemblies` and `Languages` folders. If you use the Workshop version, test in a local copy under RimWorld's `Mods` folder so Workshop updates don't replace your edits. Enable only one copy of RimMCP in the game's mod list.
2. Open `Languages`. The folder name must match RimWorld's language folder name. For example, use `French` for French or `ChineseSimplified` for Simplified Chinese. To update an existing translation, open that language's `Keyed` folder and edit it in place. For a new translation, create `Languages/French/Keyed`.
3. To try a small translation, copy `Languages/English/Keyed/AgentActivity.xml` to `Languages/French/Keyed/AgentActivity.xml`. Replace the contents of the French copy with this complete file to translate the Agent log title.

   ```xml
   <?xml version="1.0" encoding="utf-8"?>
   <LanguageData>
     <RiMCP.AgentActivity.LogTitle>Journal de l’agent</RiMCP.AgentActivity.LogTitle>
   </LanguageData>
   ```

4. Launch RimWorld, select French in its language settings, and reload the game if prompted. With RimMCP enabled, load a test colony and use the RimMCP quick-control button to open the Agent log. The title should read **Journal de l’agent**. Other RimMCP captions in this example use English fallback. Switch back to English and confirm the original title returns.
5. For more coverage, add translated entries from the English `Keyed` files. For contributions, submit the files under your language's directory and mention which game language and screens you checked.

### What to translate

- In a `Keyed` XML file, edit the text between each opening and closing tag. Keep the tag names and `LanguageData` wrapper. Each key belongs in one file per language. Use `&amp;` for `&` and `&lt;` for `<` in XML text.
- Keep placeholders such as `{0}` and `{NOTE}` exactly as written. You can move or repeat them to fit your sentence. For example, the time log translates `{0} minutes` as `{0} 分钟` in Simplified Chinese. In a formatted template, `{{0}}` displays the literal text `{0}`. Preserve any RimWorld grammar modifiers and their branches.
- Preserve tool and parameter names such as `get_operation` and `requestId`, as well as enum values and numeric limits that describe how a tool works. Omit entries you haven't translated so the game can use the English text.
- RimWorld supplies the quest acceptance notification and the Royalty favor warning in the selected game language. Their translations live in the game's language files.
- Tool descriptions in the **Access** tab have keys such as `RiMCP.Tool.get_game_log.Description`. Copy the entry you want from [ToolDescriptions.xml](RiMCP/Languages/English/Keyed/ToolDescriptions.xml) into an XML file under your own language's `Keyed` folder, and translate its text there. Tool descriptions are plain text, including any literal braces. Open **Access** and check that both the description and a search using words from your translation show the expected tool.
- The quick-control button's definition description lives in `DefInjected`. To translate it, copy `Languages/ChineseSimplified/DefInjected/MainButtonDef/RiMCP.xml` into the same `DefInjected/MainButtonDef` path under your language folder, then translate the text inside `RiMCP_QuickControl.description`. Leaving that entry out keeps the English description from the button definition.

### Set the Agent log detail length

In your language's `Keyed/AgentActivity.xml`, add or edit `RiMCP.AgentActivity.LogDetailLimit` to tune the length of the short detail shown in each Agent log row. English uses `120` and Simplified Chinese uses `60`. Omit the key to use the English resource value. Use a whole number of at least `3`. An invalid value uses the built-in default of `120`.

```xml
<RiMCP.AgentActivity.LogDetailLimit>100</RiMCP.AgentActivity.LogDetailLimit>
```

The final three dots count toward the limit. Rows can still grow to fit wrapped text, and hovering over a row shows the full detail. The diagnostic API returns the full recorded detail.

### Set the note length limit

In your language's `Keyed/AgentActivity.xml`, add or edit `RiMCP.AgentActivity.NoteLengthLimit` to set the maximum length of a `tell_player` note. English uses `280` and Simplified Chinese uses `140`. Omit the key to use the English resource value. Use an integer of at least `1`. An invalid value uses the built-in default of `280`.

```xml
<RiMCP.AgentActivity.NoteLengthLimit>320</RiMCP.AgentActivity.NoteLengthLimit>
```

The limit counts UTF-16 code units, including leading and trailing spaces or other whitespace. It applies to ordinary notes and letters sent with `attention: true`. RimMCP rejects any note over the limit without truncating it. The Agent log's row detail preview has its own separate limit.

## For developers

The source paths and commands below apply to a checkout of the repository. Run the commands from the repository root.

### English resources and generated keys

Add or edit ordinary English UI and activity strings in `RiMCP/Languages/English/Keyed/`. Put related keys together, with names such as `RiMCP.TimeLog.MinuteMany`. `RiMCP.UI.Keys` is defined in [TextKeys.g.cs](RiMCP/Source/UI/TextKeys.g.cs). CI checks that this file and the English tool descriptions are in sync with their sources. The generator drops the `RiMCP.` prefix and replaces dots with underscores, so that example becomes `Keys.TimeLog_MinuteMany`. The generator checks English values and rejects colliding generated names. Renaming or removing a key also requires updating the C# sites that use it. [Mod project](RiMCP/Source/RiMCP.csproj) · [Catalog generator](scripts/LocalizationCatalog/Program.cs)

When you add, remove, or rename an English key or edit an English tool description, run this command from the repository root to sync `TextKeys.g.cs` and `ToolDescriptions.xml`. Commit any updated outputs with their source changes. The command runs without the RimWorld game assemblies.

```sh
dotnet run --project scripts/LocalizationCatalog -- RiMCP/Languages/English/Keyed RiMCP/Source/UI/TextKeys.g.cs
```

For an English tool description, edit the tool's declaration in `Shared/ToolManifest.cs`. The manifest supplies the MCP tool description and the English description in the game's Access settings. The mod build refreshes `RiMCP/Languages/English/Keyed/ToolDescriptions.xml` from the manifest. For a non-English display override, add `RiMCP.Tool.<tool_name>.Description` to that language's `Keyed` resources. A missing or blank override uses the English manifest text. An override affects the game's description and search only. MCP consumers receive the manifest description. [Tool manifest](Shared/ToolManifest.cs) · [Tool description lookup](RiMCP/Source/UI/UiText.cs)

### UI text and template arguments

Use `UiDisplay` for player-facing labels, buttons, tooltips, notifications, letters and dialogs. Pass resource text as `Keys.SomeKey.Display()` or `Keys.SomeKey.Display(args)`. Use `.Text(args)` when a log or another consumer needs a string. Wrap data and already rendered strings in `DisplayText.Raw(value)` so they are passed through without another translation or formatting step. Editable values such as addresses still go to `Widgets.TextField` directly.

```csharp
UiDisplay.Label(rect, Keys.TimeLog_MinuteMany.Display(3));
UiDisplay.Label(rect, DisplayText.Raw("localhost"));
```

For a new template such as `Agent note: {NOTE} · {0} changes · {{0}}`, pass the count first and the note as `new NamedArgument(note, "NOTE")`. For example, `Keys.AgentLog_Example.Text(3, new NamedArgument(note, "NOTE"))` would use a new `RiMCP.AgentLog.Example` resource key. RimMCP lets Verse resolve template symbols and inserts string and `TaggedString` arguments verbatim. A note that contains braces, lowercase text, or newlines keeps those contents.

For a RimWorld key, pass all arguments to Verse in one call, such as `"MessageQuestAccepted".Translate(pawn, quest.name)`. Verse applies the current language's grammar to the whole sentence. Use `UiDisplay.GameMessage` to display the notification with its character names. When inserting a game label into plain log text, use `StripTags()` on the label. Pass `tell_player` notes, save names, identifiers and error messages as data. [TextKey](RiMCP/Source/UI/TextKey.cs) · [UI entry points](RiMCP/Source/UI/UiDisplay.cs)

### Activity and API boundaries

The agent log records localized action lines and relevant detail when an entry is created or updated. Pending operations, time advances and dialogs can finish in the same entry. A language change affects new text and the labels drawn by the current UI, such as outcome badges. Existing action strings retain their recorded wording until a subsequent action or completion updates them. Completed save, load and leave operations record the save names used by the game. Refusals preserve the names in the request. When retrying `tell_player` with the same `requestId`, preserve the exact message text, including leading and trailing whitespace.

The HTTP diagnostic `/v1/agent-log` returns the stored `line` and `detail` and stable structured fields such as `tool` and `outcome`. An Agent log detail preview counts UTF-16 code units and preserves surrogate pairs at the cutoff. The limit applies only to the short detail. Layout uses the displayed text to measure its height, the tooltip gets the full detail, and the diagnostic response sends the stored text. Separately, `get_game_log` exposes the game's original log messages and the optional original stack traces. MCP names, schemas, identifiers and configuration syntax remain stable. [Agent log](RiMCP/Source/Command/AgentLog.cs) · [Agent log window](RiMCP/Source/UI/Window_AgentLog.cs) · [Diagnostic route](RiMCP/Source/Read/AgentLogReadService.cs) · [Game log](RiMCP/Source/Read/GameLogReadService.cs)

### Verify localization changes

Run `dotnet run --project tests/Localization` from the repository root to check for missing translations and compare template placeholders with English. If you changed C# code that displays text, follow the [localization checks](tests/Localization/README.md) to check UI calls as well.

Open the affected screens in the game with the target language selected. For example, search for a tool using a word from its translation, narrow the Agent log window to check wrapping, and hover over a row to read the full detail. For templates that include a save name or note, try one containing braces and punctuation. Switch back to English and check the same screens.
