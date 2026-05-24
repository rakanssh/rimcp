using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Util;
using Verse;

namespace RiMCP.Read
{
    internal enum ReadDetail
    {
        Summary,
        Normal,
        Full
    }

    internal sealed class ReadRequest
    {
        public string MapId;
        public ReadDetail Detail = ReadDetail.Summary;
        public readonly HashSet<string> Include = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public int Limit = 50;
        public int Cursor;
        public int? SinceTick;
        public bool IdsOnly;
        public readonly Dictionary<string, string> Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static ReadRequest FromUri(Uri uri)
        {
            ReadRequest request = new ReadRequest();
            foreach (KeyValuePair<string, string> pair in ParseQuery(uri.Query))
            {
                request.Query[pair.Key] = pair.Value;
            }

            request.MapId = request.Get("mapId");
            string detail = request.Get("detail");
            if (string.Equals(detail, "normal", StringComparison.OrdinalIgnoreCase))
            {
                request.Detail = ReadDetail.Normal;
            }
            else if (string.Equals(detail, "full", StringComparison.OrdinalIgnoreCase))
            {
                request.Detail = ReadDetail.Full;
            }

            foreach (string include in SplitCsv(request.Get("include")))
            {
                request.Include.Add(include);
            }

            int parsed;
            if (int.TryParse(request.Get("limit"), out parsed))
            {
                request.Limit = Math.Max(1, Math.Min(200, parsed));
            }
            if (int.TryParse(request.Get("cursor"), out parsed))
            {
                request.Cursor = Math.Max(0, parsed);
            }
            if (int.TryParse(request.Get("sinceTick"), out parsed))
            {
                request.SinceTick = parsed;
            }
            bool boolValue;
            if (bool.TryParse(request.Get("idsOnly"), out boolValue))
            {
                request.IdsOnly = boolValue;
            }
            return request;
        }

        public string Get(string name)
        {
            string value;
            return Query.TryGetValue(name, out value) ? value : null;
        }

        public bool Wants(string include)
        {
            return Detail == ReadDetail.Full || Include.Contains(include);
        }

        private static IEnumerable<KeyValuePair<string, string>> ParseQuery(string query)
        {
            if (string.IsNullOrEmpty(query))
            {
                yield break;
            }

            string text = query[0] == '?' ? query.Substring(1) : query;
            foreach (string part in text.Split('&'))
            {
                if (part.Length == 0)
                {
                    continue;
                }
                int equals = part.IndexOf('=');
                string key = equals < 0 ? part : part.Substring(0, equals);
                string value = equals < 0 ? "" : part.Substring(equals + 1);
                yield return new KeyValuePair<string, string>(Uri.UnescapeDataString(key.Replace("+", " ")), Uri.UnescapeDataString(value.Replace("+", " ")));
            }
        }

        private static IEnumerable<string> SplitCsv(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                yield break;
            }
            foreach (string part in text.Split(','))
            {
                string item = part.Trim();
                if (item.Length > 0)
                {
                    yield return item;
                }
            }
        }
    }

    internal sealed class ReadEnvelope
    {
        public string SchemaVersion = "1";
        public int Tick;
        public string MapId;
        public bool Truncated;
        public string NextCursor;
        public bool NotModified;
        public object Data;

        public static BridgeResponse Ok(ReadContext context, object data, bool truncated = false, string nextCursor = null)
        {
            ReadEnvelope envelope = new ReadEnvelope
            {
                Tick = context.Tick,
                MapId = context.Map == null ? null : context.Map.uniqueID.ToString(),
                Truncated = truncated,
                NextCursor = nextCursor,
                Data = data
            };
            return BridgeResponse.Json(200, Json.Serialize(envelope.ToDto()));
        }

        public static BridgeResponse NotChanged(ReadContext context)
        {
            ReadEnvelope envelope = new ReadEnvelope
            {
                Tick = context.Tick,
                MapId = context.Map == null ? null : context.Map.uniqueID.ToString(),
                NotModified = true,
                Data = Dto.Obj()
            };
            return BridgeResponse.Json(200, Json.Serialize(envelope.ToDto()));
        }

        private object ToDto()
        {
            Dictionary<string, object> dto = Dto.Obj(
                Dto.Field("schemaVersion", SchemaVersion),
                Dto.Field("tick", Tick),
                Dto.Field("mapId", MapId),
                Dto.Field("truncated", Truncated),
                Dto.Field("nextCursor", NextCursor),
                Dto.Field("notModified", NotModified ? (object)true : null),
                Dto.Field("data", Data));
            return dto;
        }
    }

    internal sealed class ReadContext
    {
        public readonly ReadRequest Request;
        public readonly Map Map;
        public readonly int Tick;

        public ReadContext(ReadRequest request, Map map)
        {
            Request = request;
            Map = map;
            Tick = Find.TickManager == null ? 0 : Find.TickManager.TicksGame;
        }
    }

    internal static class Dto
    {
        public struct JsonField
        {
            public string Name;
            public object Value;
        }

        public static JsonField Field(string name, object value)
        {
            return new JsonField { Name = name, Value = value };
        }

        public static Dictionary<string, object> Obj(params JsonField[] fields)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            for (int i = 0; i < fields.Length; i++)
            {
                if (fields[i].Value != null)
                {
                    result[fields[i].Name] = fields[i].Value;
                }
            }
            return result;
        }
    }

    internal sealed class Page<T>
    {
        public readonly List<T> Items;
        public readonly bool Truncated;
        public readonly string NextCursor;

        public Page(IEnumerable<T> source, ReadRequest request)
        {
            Items = new List<T>();
            int seen = 0;
            foreach (T item in source)
            {
                if (seen++ < request.Cursor)
                {
                    continue;
                }
                if (Items.Count >= request.Limit)
                {
                    Truncated = true;
                    break;
                }
                Items.Add(item);
            }
            NextCursor = Truncated ? (request.Cursor + request.Limit).ToString() : null;
        }
    }

    internal static class ReadUtil
    {
        public static Map ResolveMap(ReadRequest request)
        {
            if (Find.Maps == null || Find.Maps.Count == 0)
            {
                return null;
            }
            if (!string.IsNullOrWhiteSpace(request.MapId))
            {
                return Find.Maps.FirstOrDefault(map => map.uniqueID.ToString() == request.MapId);
            }
            return Find.CurrentMap ?? Find.Maps.FirstOrDefault();
        }

        public static object Cell(IntVec3 cell)
        {
            return Dto.Obj(
                Dto.Field("x", cell.x),
                Dto.Field("y", cell.y),
                Dto.Field("z", cell.z));
        }

        public static object Def(Def def)
        {
            if (def == null)
            {
                return null;
            }
            return Dto.Obj(
                Dto.Field("defName", def.defName),
                Dto.Field("label", DefLabel(def)));
        }

        public static string DefLabel(Def def)
        {
            if (def == null)
            {
                return null;
            }

            string label = def.LabelCap.ToString();
            return string.IsNullOrWhiteSpace(label) ? def.defName : label;
        }

        public static object ThingIds(Thing thing)
        {
            if (thing == null)
            {
                return null;
            }
            return Dto.Obj(
                Dto.Field("id", thing.ThingID),
                Dto.Field("loadId", thing.GetUniqueLoadID()));
        }

        public static string ThingId(Thing thing)
        {
            return thing == null ? null : (thing.ThingID ?? thing.GetUniqueLoadID());
        }

        public static string QualityLabel(Thing thing)
        {
            QualityCategory quality;
            return thing != null && QualityUtility.TryGetQuality(thing, out quality) ? quality.ToString() : null;
        }

        public static string PawnName(Pawn pawn)
        {
            if (pawn == null)
            {
                return null;
            }
            return pawn.Name == null ? pawn.LabelShortCap : pawn.Name.ToStringFull;
        }

        public static object MapSummary(Map map)
        {
            if (map == null)
            {
                return null;
            }
            return Dto.Obj(
                Dto.Field("mapId", map.uniqueID.ToString()),
                Dto.Field("label", map.Parent == null ? "Unknown" : map.Parent.LabelCap),
                Dto.Field("tile", map.Tile),
                Dto.Field("biome", Def(map.Biome)),
                Dto.Field("size", Dto.Obj(
                    Dto.Field("x", map.Size.x),
                    Dto.Field("z", map.Size.z))));
        }

        public static string StableSessionId(string kind, int mapId, IntVec3 cell, string label)
        {
            return kind + ":" + mapId + ":" + cell.x + "," + cell.z + ":" + (label ?? "");
        }

        public static bool ChangedSince(ReadContext context)
        {
            return !context.Request.SinceTick.HasValue || context.Tick > context.Request.SinceTick.Value;
        }
    }
}
