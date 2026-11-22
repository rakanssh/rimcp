# Localization checks

Run from the repository root with the .NET 8 SDK or later. For the UI check,
use the full RimWorld 1.6 managed assembly directory or the reference package
used in CI.

```powershell
$env:RimWorldManagedPath = 'C:\path\to\the\1.6\Managed'
dotnet run --project tests/Localization
dotnet build RiMCP/Source/RiMCP.csproj
```

The checker also accepts `--rimworld-managed-path <path>`. Without a reference
directory it checks the catalogs. CI runs the full check and builds the mod
against pinned RimWorld 1.6 reference assemblies. To sync `RiMCP.UI.Keys` and
`ToolDescriptions.xml` with their sources, run the catalog generator as shown in
the [localization guide](../../README.localization.md#english-resources-and-generated-keys).
CI verifies both outputs. Compilation catches references to deleted C# keys
after regeneration.

- Missing translations warn when English provides the text. Omit untranslated
  entries to use English fallback. The game loads DefInjected translations
  from their native files.
- Translated templates must keep the same distinct named and numbered
  placeholders as English. Translations can reorder and repeat placeholders
  and use escaped text such as `{{0}}`. RimWorld handles grammar modifiers
  and their branches.
- English tool descriptions come from `Shared/ToolManifest.cs`. The mod build
  refreshes their [English Keyed file](../../RiMCP/Languages/English/Keyed/ToolDescriptions.xml).
  Tool descriptions are plain text and can include literal braces.
- Use `UiDisplay` for player-facing controls and notifications. Pass resource
  text through the key's `Display()` or `Display(args)` method and use
  `DisplayText.Raw(value)` for display data such as `TCP` and `localhost`.
  Pass editable values directly to `Widgets.TextField`. The checker detects
  calls that bypass this boundary using the RimWorld and Unity API symbols
  listed in `UiCalls.cs`.

The checker exits with `0` when it finds no errors, including runs with warnings,
`1` when it finds errors and `2` when an exception prevents it from completing.
