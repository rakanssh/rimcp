using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RiMCP.Util
{
    internal static class Json
    {
        public struct Property
        {
            public string Name;
            public string Value;
        }

        public static Property Prop(string name, string rawValue)
        {
            return new Property { Name = name, Value = rawValue };
        }

        public static string Object(params Property[] properties)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append('{');
            for (int i = 0; i < properties.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }
                builder.Append(String(properties[i].Name));
                builder.Append(':');
                builder.Append(properties[i].Value ?? "null");
            }
            builder.Append('}');
            return builder.ToString();
        }

        public static string Array(IEnumerable<string> rawItems)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append('[');
            bool first = true;
            foreach (string item in rawItems)
            {
                if (!first)
                {
                    builder.Append(',');
                }
                first = false;
                builder.Append(item ?? "null");
            }
            builder.Append(']');
            return builder.ToString();
        }

        public static string String(string value)
        {
            if (value == null)
            {
                return "null";
            }

            StringBuilder builder = new StringBuilder(value.Length + 2);
            builder.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\b':
                        builder.Append("\\b");
                        break;
                    case '\f':
                        builder.Append("\\f");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (c < 32)
                        {
                            builder.Append("\\u");
                            builder.Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            builder.Append(c);
                        }
                        break;
                }
            }
            builder.Append('"');
            return builder.ToString();
        }

        public static string Number(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        public static string Number(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        public static string Bool(bool value)
        {
            return value ? "true" : "false";
        }
    }
}

