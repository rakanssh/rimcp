using System.Globalization;

internal sealed class TemplateArguments(string text)
{
    private readonly HashSet<string> arguments = new(StringComparer.Ordinal);
    private int position;

    public static string[] Read(string text)
    {
        var template = new TemplateArguments(text);
        template.ReadBody(nested: false);
        return template.arguments.Order(StringComparer.Ordinal).ToArray();
    }

    private void ReadBody(bool nested)
    {
        while (position < text.Length)
        {
            switch (text[position++])
            {
                case '{':
                    ReadSymbol();
                    break;
                case '}' when nested:
                    return;
                case '}':
                    throw new FormatException("has an unmatched closing brace");
            }
        }

        if (nested) throw new FormatException("has an unmatched opening brace");
    }

    private void ReadSymbol()
    {
        if (position < text.Length && text[position] == '{')
        {
            int closing = text.IndexOf("}}", position + 1, StringComparison.Ordinal);
            if (closing < 0) throw new FormatException("has an unclosed escaped brace pair");
            position = closing + 2;
            return;
        }

        int start = position;
        while (position < text.Length && text[position] is not ('}' or '{' or '?' or ':')) position++;
        if (position == text.Length) throw new FormatException("has an unmatched opening brace");

        string header = text[start..position];
        char delimiter = text[position++];
        if (delimiter == '{') throw new FormatException("has a nested symbol outside a grammar argument");
        if (delimiter != ':')
        {
            int modifier = header.IndexOf('_');
            string label = modifier >= 0 ? header[..modifier] : header;
            if (label.Length == 0 || label.Any(char.IsWhiteSpace))
                throw new FormatException($"has an invalid placeholder '{{{header}}}'");
            if (int.TryParse(label, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
            {
                if (index < 0) throw new FormatException($"has a negative argument index '{label}'");
                label = index.ToString(CultureInfo.InvariantCulture);
            }
            arguments.Add("{" + label + "}");
        }

        if (delimiter is '?' or ':') ReadBody(nested: true);
    }
}
