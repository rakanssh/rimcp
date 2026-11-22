using System.Xml;
using System.Xml.Linq;
using RiMCP.Shared;

internal static class KeyedResources
{
    private const string ToolCatalogPath = "RiMCP/Languages/English/Keyed/ToolDescriptions.xml";
    private const string ToolManifestPath = "Shared/ToolManifest.cs";

    private sealed record Entry(string Location, string Text)
    {
        public bool HasTranslation => !string.IsNullOrWhiteSpace(Text);
    }

    public static string[] Check(string root, SortedSet<string> errors, SortedSet<string> warnings)
    {
        var directory = new DirectoryInfo(Path.Combine(root, "RiMCP", "Languages"));
        string[] languages = (directory.Exists ? directory.EnumerateDirectories().Select(folder => folder.Name) : [])
            .Append("English").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var english = Read(root, "English").Where(pair => pair.Value.HasTranslation)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var toolDescriptions = ToolManifest.McpTools.ToDictionary(tool => $"RiMCP.Tool.{tool.Name}.Description",
            tool => new Entry(ToolManifestPath, tool.Description), StringComparer.Ordinal);
        var catalogs = languages.Where(language => language != "English")
            .ToDictionary(language => language, language => Read(root, language, warnings));

        foreach (var (key, original) in english.Concat(toolDescriptions))
        {
            // Tool descriptions are plain text, including any literal braces
            bool formatted = !toolDescriptions.ContainsKey(key);
            foreach (var (language, catalog) in catalogs)
            {
                if (catalog.Count == 0) continue;
                if (!catalog.TryGetValue(key, out var entry))
                {
                    warnings.Add($"{original.Location}: missing {language} key {key}; falls back to English");
                    continue;
                }
                if (!entry.HasTranslation)
                {
                    warnings.Add($"{entry.Location}: missing {language} translation for {key} (empty translation; omit this entry to use English)");
                    continue;
                }
                if (!formatted) continue;
                var originalArguments = Arguments(original, key, errors);
                var translation = Arguments(entry, key, errors);
                if (originalArguments != null && translation != null && !originalArguments.SequenceEqual(translation))
                    errors.Add($"{entry.Location}: {key} placeholders [{string.Join(", ", translation)}] differ from English [{string.Join(", ", originalArguments)}] at {original.Location}");
            }
        }
        return languages;
    }

    private static string[]? Arguments(Entry entry, string key, SortedSet<string> errors)
    {
        try
        {
            return TemplateArguments.Read(entry.Text);
        }
        catch (FormatException error)
        {
            errors.Add($"{entry.Location}: {key} {error.Message}");
            return null;
        }
    }

    private static Dictionary<string, Entry> Read(string root, string language, SortedSet<string>? warnings = null)
    {
        var result = new Dictionary<string, Entry>(StringComparer.Ordinal);
        string relativeDirectory = $"RiMCP/Languages/{language}/Keyed";
        string directory = Path.Combine(root, relativeDirectory);
        if (!Directory.Exists(directory))
        {
            warnings?.Add($"{relativeDirectory}: missing translation directory");
            return result;
        }
        foreach (string file in Directory.EnumerateFiles(directory, "*.xml", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string path = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (path == ToolCatalogPath) continue;

            var document = XDocument.Load(file, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
            if (document.Root?.Name != "LanguageData")
                throw new InvalidDataException($"{path}: expected a LanguageData root");
            foreach (var element in document.Root.Elements())
            {
                string key = element.Name.ToString();
                string location = $"{path}:{((IXmlLineInfo)element).LineNumber}";
                result[key] = new Entry(location, element.Value);
            }
        }
        if (result.Count == 0) warnings?.Add($"{relativeDirectory}: no translation keys found");
        return result;
    }
}
