using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            string? managedPath = args switch
            {
                [] => Environment.GetEnvironmentVariable("RimWorldManagedPath"),
                ["--rimworld-managed-path", string path] => path,
                _ => throw new ArgumentException("Usage: Localization [--rimworld-managed-path <path>]")
            };
            return Check(Directory.GetCurrentDirectory(), managedPath);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 2;
        }
    }

    private static int Check(string root, string? managedPath)
    {
        var errors = new SortedSet<string>(StringComparer.Ordinal);
        var warnings = new SortedSet<string>(StringComparer.Ordinal);
        var languages = KeyedResources.Check(root, errors, warnings);
        if (!string.IsNullOrWhiteSpace(managedPath))
            errors.UnionWith(CheckUi(root, managedPath));
        else
            Console.WriteLine("UI entry points were skipped. Set RimWorldManagedPath to enable the check.");

        foreach (string error in errors) Console.Error.WriteLine(error);
        foreach (string warning in warnings) Console.Error.WriteLine($"Warning: {warning}");
        Console.WriteLine($"Checked Keyed resources across {string.Join(", ", languages)}. {errors.Count} error(s), {warnings.Count} warning(s).");
        return errors.Count == 0 ? 0 : 1;
    }

    private static IEnumerable<string> CheckUi(string root, string managedPath)
    {
        var sources = Directory.EnumerateFiles(Path.Combine(root, "RiMCP", "Source"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "Shared"), "*.cs", SearchOption.AllDirectories));
        var trees = sources
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
            .Where(path => !path.Split('/').Any(part => part is "bin" or "obj"))
            .Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, path)), path: path))
            .ToArray();
        var compilation = CSharpCompilation.Create("Localization", trees, RimWorldReferences(managedPath),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return trees.SelectMany(tree => UiCalls.Check(compilation.GetSemanticModel(tree), tree.GetCompilationUnitRoot()));
    }

    private static MetadataReference[] RimWorldReferences(string managedPath)
    {
        string game = Path.Combine(managedPath, "Assembly-CSharp.dll");
        if (AssemblyName.GetAssemblyName(game).Version is not { Major: 1, Minor: 6 })
            throw new ArgumentException("The UI check requires RimWorld 1.6 reference assemblies.");
        foreach (string name in new[] { "mscorlib.dll", "UnityEngine.CoreModule.dll", "UnityEngine.IMGUIModule.dll" })
            if (!File.Exists(Path.Combine(managedPath, name)))
                throw new FileNotFoundException($"RimWorld reference directory does not contain {name}.");
        return Directory.EnumerateFiles(managedPath, "*.dll")
            .Select(file => MetadataReference.CreateFromFile(file)).ToArray();
    }

    internal static string Location(SyntaxNode node) =>
        $"{node.SyntaxTree.FilePath}:{node.GetLocation().GetLineSpan().StartLinePosition.Line + 1}";
}
