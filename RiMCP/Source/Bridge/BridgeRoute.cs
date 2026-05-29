using System;
using System.Collections.Generic;

namespace RiMCP.Bridge
{
    internal sealed class RouteMatch
    {
        public readonly Dictionary<string, string> Values;

        public RouteMatch(Dictionary<string, string> values)
        {
            Values = values;
        }

        public string this[string name]
        {
            get { return Values[name]; }
        }
    }

    internal sealed class BridgeRoute
    {
        public delegate BridgeResponse Handler(BridgeRequest request, RouteMatch match);

        private readonly string[] templateSegments;

        private BridgeRoute(string method, string template, Handler handle)
        {
            Method = method;
            Template = NormalizePath(template);
            Handle = handle;
            templateSegments = SplitPath(Template);
        }

        public string Method { get; private set; }
        public string Template { get; private set; }
        public Handler Handle { get; private set; }

        public static BridgeRoute Get(string template, Handler handle)
        {
            return new BridgeRoute("GET", template, handle);
        }

        public static BridgeRoute Create(string method, string template, Handler handle)
        {
            return new BridgeRoute(method, template, handle);
        }

        public static BridgeRoute Put(string template, Handler handle)
        {
            return new BridgeRoute("PUT", template, handle);
        }

        public static BridgeRoute Post(string template, Handler handle)
        {
            return new BridgeRoute("POST", template, handle);
        }

        public static BridgeRoute Delete(string template, Handler handle)
        {
            return new BridgeRoute("DELETE", template, handle);
        }

        public bool MethodMatches(string method)
        {
            return string.Equals(Method, method, StringComparison.OrdinalIgnoreCase);
        }

        public bool TryMatchPath(string path, out RouteMatch match)
        {
            match = null;
            string[] pathSegments = SplitPath(NormalizePath(path));
            if (pathSegments.Length != templateSegments.Length)
            {
                return false;
            }

            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < templateSegments.Length; i++)
            {
                string templateSegment = templateSegments[i];
                if (IsCapture(templateSegment))
                {
                    string name = templateSegment.Substring(1, templateSegment.Length - 2);
                    values[name] = Uri.UnescapeDataString(pathSegments[i]);
                    continue;
                }

                if (!string.Equals(templateSegment, pathSegments[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            match = new RouteMatch(values);
            return true;
        }

        private static bool IsCapture(string segment)
        {
            return segment.Length > 2 && segment[0] == '{' && segment[segment.Length - 1] == '}';
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return "/";
            }

            path = path.TrimEnd('/');
            return path.Length == 0 ? "/" : path;
        }

        private static string[] SplitPath(string path)
        {
            string trimmed = path.Trim('/');
            return trimmed.Length == 0 ? new string[0] : trimmed.Split('/');
        }
    }
}
