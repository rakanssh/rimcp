using System;
using System.Collections.Generic;
using System.Globalization;
using System.Collections;
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
            return float.IsNaN(value) || float.IsInfinity(value)
                ? "null"
                : value.ToString("R", CultureInfo.InvariantCulture);
        }

        public static string Number(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value)
                ? "null"
                : value.ToString("R", CultureInfo.InvariantCulture);
        }

        public static string Bool(bool value)
        {
            return value ? "true" : "false";
        }

        public static string Serialize(object value)
        {
            StringBuilder builder = new StringBuilder();
            WriteValue(builder, value);
            return builder.ToString();
        }

        private static void WriteValue(StringBuilder builder, object value)
        {
            if (value == null)
            {
                builder.Append("null");
                return;
            }

            string text = value as string;
            if (text != null)
            {
                builder.Append(String(text));
                return;
            }

            if (value is bool)
            {
                builder.Append((bool)value ? "true" : "false");
                return;
            }

            if (value is char)
            {
                builder.Append(String(((char)value).ToString()));
                return;
            }

            if (value is float)
            {
                builder.Append(Number((float)value));
                return;
            }

            if (value is double)
            {
                builder.Append(Number((double)value));
                return;
            }

            if (value is int || value is long || value is short || value is byte ||
                value is uint || value is ulong || value is ushort || value is sbyte ||
                value is decimal)
            {
                builder.Append(System.Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }

            if (value is System.Enum)
            {
                builder.Append(String(value.ToString()));
                return;
            }

            IDictionary dictionary = value as IDictionary;
            if (dictionary != null)
            {
                WriteDictionary(builder, dictionary);
                return;
            }

            IEnumerable enumerable = value as IEnumerable;
            if (enumerable != null)
            {
                WriteEnumerable(builder, enumerable);
                return;
            }

            throw new JsonSerializationException("Unsupported JSON value type: " + value.GetType().FullName);
        }

        private static void WriteDictionary(StringBuilder builder, IDictionary dictionary)
        {
            builder.Append('{');
            bool first = true;
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Key == null || ShouldOmit(entry.Value))
                {
                    continue;
                }
                string key = entry.Key as string;
                if (key == null)
                {
                    throw new JsonSerializationException("Unsupported JSON object key type: " + entry.Key.GetType().FullName);
                }
                if (!first)
                {
                    builder.Append(',');
                }
                first = false;
                builder.Append(String(key));
                builder.Append(':');
                WriteValue(builder, entry.Value);
            }
            builder.Append('}');
        }

        private static void WriteEnumerable(StringBuilder builder, IEnumerable enumerable)
        {
            builder.Append('[');
            bool first = true;
            foreach (object item in enumerable)
            {
                if (!first)
                {
                    builder.Append(',');
                }
                first = false;
                WriteValue(builder, item);
            }
            builder.Append(']');
        }

        private static bool ShouldOmit(object value)
        {
            return value == null;
        }
    }

    internal sealed class JsonSerializationException : Exception
    {
        public JsonSerializationException(string message) : base(message)
        {
        }
    }
}
