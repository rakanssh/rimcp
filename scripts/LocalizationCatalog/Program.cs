using System.Text;
using System.Xml.Linq;
using RiMCP.Shared;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length is < 1 or > 2)
                throw new ArgumentException("Expected the English Keyed directory and an optional generated C# path.");
            string directory = Path.GetFullPath(args[0]);
            string catalog = Path.Combine(directory, "ToolDescriptions.xml");
            if (args.Length == 2)
                WriteTextKeys(directory, catalog, args[1]);

            var tools = new XElement("LanguageData", ToolManifest.McpTools.Select(tool =>
                new XElement($"RiMCP.Tool.{tool.Name}.Description", tool.Description)));
            WriteChanged(catalog, string.Join(Environment.NewLine, "<?xml version=\"1.0\" encoding=\"utf-8\"?>",
                "<!-- Generated from Shared/ToolManifest.cs -->", tools.ToString(), ""));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void WriteTextKeys(string directory, string catalog, string output)
    {
        var keys = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(directory, "*.xml", SearchOption.AllDirectories))
        {
            if (file == catalog) continue;
            XElement? root = XDocument.Load(file).Root;
            if (root?.Name != "LanguageData")
                throw new InvalidDataException($"{file}: expected LanguageData.");
            foreach (XElement entry in root.Elements())
            {
                string key = entry.Name.ToString();
                if (string.IsNullOrWhiteSpace(entry.Value))
                    throw new InvalidDataException($"{file}: missing English text for {key}.");
                if (!keys.TryAdd(TextKeys.MemberName(key), key))
                    throw new InvalidDataException($"{file}: duplicate generated member for {key}.");
            }
        }

        WriteChanged(output, TextKeys.Source(keys));
    }

    private static void WriteChanged(string path, string text)
    {
        if (File.Exists(path))
        {
            string existing = File.ReadAllText(path, Encoding.UTF8);
            text = text.ReplaceLineEndings(existing.Contains("\r\n") ? "\r\n" : "\n");
            if (existing == text) return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }
}
