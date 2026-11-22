using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Verse;

namespace RiMCP.UI
{
    internal readonly struct TextKey
    {
        private readonly string key;

        internal TextKey(string key) { this.key = key; }

        public DisplayText Display(params NamedArgument[] args) => DisplayText.Raw(Text(args));

        public int ReadLimit(int minimum, int fallback)
        {
            return int.TryParse(Text(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                && value >= minimum ? value : fallback;
        }

        public string Text(params NamedArgument[] args)
        {
            TaggedString template = key.Translate();
            if (string.IsNullOrWhiteSpace(template.RawText)
                && LanguageDatabase.defaultLanguage != null)
                LanguageDatabase.defaultLanguage.TryGetTextFromKey(key, out template);
            if (args.Length == 0) return template.RawText;

            var labels = new List<string>(args.Length);
            var values = new List<object>(args.Length);
            var raw = new List<string>();
            string prefix = "\uE000\uE002";
            while (template.RawText.Contains(prefix)) prefix += "\uE002";
            foreach (NamedArgument arg in args)
            {
                labels.Add(arg.label);
                object value = arg.arg;
                if (value == null || value is string || value is TaggedString)
                {
                    // Let Verse process the surrounding sentence before inserting verbatim text
                    values.Add(prefix + raw.Count.ToString(CultureInfo.InvariantCulture) + "\uE001");
                    raw.Add(value?.ToString() ?? "");
                }
                else
                {
                    values.Add(value);
                }
            }
            string rendered = RenderSymbols(template.RawText, labels, values);
            if (raw.Count == 0) return rendered;
            return Regex.Replace(rendered, prefix + @"([0-9]+)\uE001",
                match => raw[int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)]);
        }

        // Only symbols go through Verse's grammar and sentence postprocessor
        private static string RenderSymbols(string template, List<string> labels, List<object> values)
        {
            var result = new StringBuilder(template.Length);
            int position = 0;
            int start;
            while ((start = template.IndexOf('{', position)) >= 0)
            {
                result.Append(template, position, start - position);
                if (start + 1 < template.Length && template[start + 1] == '{')
                {
                    int close = template.IndexOf("}}", start + 2, StringComparison.Ordinal);
                    if (close < 0) return result.Append(template, start, template.Length - start).ToString();
                    result.Append(template, start + 1, close - start);
                    position = close + 2;
                    continue;
                }

                int end = start + 1;
                int depth = 1;
                while (end < template.Length && depth > 0)
                {
                    char c = template[end++];
                    if (c == '{') depth++;
                    if (c == '}') depth--;
                }
                if (depth != 0) return result.Append(template, start, template.Length - start).ToString();
                result.Append(GrammarResolverSimple.Formatted(template.Substring(start, end - start), labels, values).RawText);
                position = end;
            }
            return result.Append(template, position, template.Length - position).ToString();
        }
    }
}
