using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static class UiCalls
{
    public static IEnumerable<string> Check(SemanticModel model, CompilationUnitSyntax root)
    {
        foreach (var node in root.DescendantNodes().Where(IsCallOrReference))
        {
            var info = model.GetSymbolInfo(node);
            var method = (info.Symbol as IMethodSymbol) ?? info.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault(IsTextSink);
            if (method == null || !IsTextSink(method)) continue;
            if (model.GetEnclosingSymbol(node.SpanStart)?.ContainingType?.ToDisplayString() == "RiMCP.UI.UiDisplay")
                continue;

            string entry = method.MethodKind == MethodKind.Constructor
                ? method.ContainingType.Name
                : $"{method.ContainingType.Name}.{method.Name}";
            yield return $"{Program.Location(node)}: {entry} bypasses the UiDisplay text entry point.";
        }
    }

    private static bool IsCallOrReference(SyntaxNode node) => node switch
    {
        InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax => true,
        MemberAccessExpressionSyntax or SimpleNameSyntax => node.Parent switch
        {
            InvocationExpressionSyntax call => call.Expression != node,
            MemberAccessExpressionSyntax member => member.Name != node,
            _ => true
        },
        _ => false
    };

    private static bool IsTextSink(IMethodSymbol method)
    {
        return (method.ContainingType.ToDisplayString(), method.Name) switch
        {
            ("Verse.Widgets", "Label" or "LabelScrollable" or "ButtonText" or "ButtonTextSubtle" or "CheckboxLabeled") => true,
            ("Verse.Listing_Standard", "Label" or "ButtonText" or "ButtonTextLabeled" or "CheckboxLabeled") => true,
            ("Verse.TooltipHandler", "TipRegion") => true,
            ("Verse.Messages", "Message") => method.Parameters.Any(parameter => IsText(parameter.Type)),
            ("Verse.LetterMaker", "MakeLetter") => true,
            ("Verse.LetterStack", "ReceiveLetter") => IsText(method.Parameters[0].Type),
            ("UnityEngine.GUI" or "UnityEngine.GUILayout", "Label" or "Box" or "Button" or "RepeatButton" or "Toggle") =>
                method.Parameters.Any(parameter => IsText(parameter.Type) || parameter.Type.ToDisplayString() == "UnityEngine.GUIContent"),
            ("Verse.FloatMenuOption" or "Verse.TipSignal" or "Verse.Message" or "Verse.Dialog_MessageBox", ".ctor") => true,
            _ => false
        };
    }

    private static bool IsText(ITypeSymbol type) => type.SpecialType == SpecialType.System_String
        || type.ToDisplayString() == "Verse.TaggedString";
}
