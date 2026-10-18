using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using RimWorld;
using RiMCP.Bridge;
using Verse;

namespace RiMCP.Read
{
    internal sealed class HistoryEntry
    {
        public readonly object Source;
        public readonly string LoadId;
        public readonly int Tick;
        // Messages are found by id: the game numbers them as it makes them, and moves an existing
        // message's tick forward when something repeats it. -1 for anything else.
        public readonly int MessageId;
        // The frame a message was last shown, which a repeat moves forward even while paused. Saved
        // with the game, so a loaded message's frame is from the session that made it.
        public readonly int Frame;

        public HistoryEntry(object source, string loadId, int tick, int messageId, int frame)
        {
            Source = source;
            LoadId = loadId;
            Tick = tick;
            MessageId = messageId;
            Frame = frame;
        }

        public static HistoryEntry Of(IArchivable archivable)
        {
            string loadId = archivable.GetUniqueLoadID();
            Message message = archivable as Message;
            int messageId = -1;
            if (message != null && !int.TryParse(loadId.Substring(loadId.LastIndexOf('_') + 1), out messageId))
            {
                messageId = -1;
            }
            return new HistoryEntry(archivable, loadId, archivable.CreatedTicksGame, messageId, message == null ? 0 : message.startingFrame);
        }

        public bool IsMessage
        {
            get { return MessageId >= 0; }
        }
    }

    // Where a reader of the History tab got to, in one loaded game. A quest's letter is numbered when
    // the quest is made but arrives later, so letters and the rest are found by tick and load id.
    internal sealed class HistoryCursor
    {
        private const string Version = "2";

        public string LoadId;
        public int IssueTick;
        public int Frame;
        // The History watch's look at which the reader last caught up.
        public int Look;
        public int MessageId;
        public int Tick;
        public HashSet<string> Ids = new HashSet<string>(StringComparer.Ordinal);

        public bool Covers(HistoryEntry entry)
        {
            if (entry.IsMessage)
            {
                return entry.MessageId <= MessageId;
            }
            return entry.Tick < Tick || entry.Tick == Tick && Ids.Contains(entry.LoadId);
        }

        // While paused a repeat moves only a message's frame, and a loaded message's frame is from
        // another session, so frames are compared only within the cursor's tick.
        public bool RepeatedSince(HistoryEntry message, int frame)
        {
            return message.Tick > IssueTick || message.Tick == IssueTick && message.Frame > Frame && message.Frame <= frame;
        }

        public string Encode()
        {
            string raw = string.Join("\n", new[] { Version, LoadId ?? "", IssueTick.ToString(), Frame.ToString(), Look.ToString(), MessageId.ToString(), Tick.ToString() }.Concat(Ids.OrderBy(id => id, StringComparer.Ordinal)).ToArray());
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        public static HistoryCursor Decode(string text)
        {
            try
            {
                string base64 = text.Trim().Replace('-', '+').Replace('_', '/');
                base64 += new string('=', (4 - base64.Length % 4) % 4);
                string[] parts = Encoding.UTF8.GetString(Convert.FromBase64String(base64)).Split('\n');
                if (parts.Length < 7 || parts[0] != Version)
                {
                    throw new FormatException();
                }
                HistoryCursor cursor = new HistoryCursor
                {
                    LoadId = parts[1],
                    IssueTick = int.Parse(parts[2]),
                    Frame = int.Parse(parts[3]),
                    Look = int.Parse(parts[4]),
                    MessageId = int.Parse(parts[5]),
                    Tick = int.Parse(parts[6])
                };
                for (int i = 7; i < parts.Length; i++)
                {
                    cursor.Ids.Add(parts[i]);
                }
                return cursor;
            }
            catch (Exception)
            {
                throw new BridgeException(400, "Field 'since' must be history.cursor from an earlier get_events call.");
            }
        }
    }

    internal sealed class HistoryPage
    {
        public readonly List<HistoryEntry> Entries;
        public readonly List<HistoryEntry> Repeated;
        public readonly bool More;
        public readonly bool Reset;
        public readonly bool Incomplete;
        public readonly HistoryCursor Next;

        public HistoryPage(List<HistoryEntry> entries, List<HistoryEntry> repeated, bool more, bool reset, bool incomplete, HistoryCursor next)
        {
            Entries = entries;
            Repeated = repeated;
            More = more;
            Reset = reset;
            Incomplete = incomplete;
            Next = next;
        }
    }

    // What the History tab lost: entries seen and then removed, kept as the newest message id and the
    // newest tick of anything else with its load ids there, and the latest look after which entries
    // may have been made and removed unseen.
    internal sealed class HistoryLoss
    {
        public int MessageId = -1;
        public int Tick = -1;
        public readonly HashSet<string> Ids = new HashSet<string>(StringComparer.Ordinal);
        public int Look;
        public int Unseen = -1;

        public void Record(HistoryEntry entry)
        {
            if (entry.IsMessage)
            {
                MessageId = Math.Max(MessageId, entry.MessageId);
                return;
            }
            if (entry.Tick > Tick)
            {
                Tick = entry.Tick;
                Ids.Clear();
            }
            if (entry.Tick == Tick)
            {
                Ids.Add(entry.LoadId);
            }
        }

        public bool Behind(HistoryCursor cursor)
        {
            return cursor.Look < Unseen || MessageId > cursor.MessageId || Tick > cursor.Tick || Tick == cursor.Tick && Ids.Any(id => !cursor.Ids.Contains(id));
        }
    }

    internal struct Sighting
    {
        public readonly int Tick;
        public readonly bool Cullable;

        public Sighting(int tick, bool cullable)
        {
            Tick = tick;
            Cullable = cullable;
        }
    }

    internal sealed class HistoryWatcher
    {
        // Every add, sort, and removal changes it, so an unchanged one means nothing came or went.
        private static readonly FieldInfo ListVersion = typeof(List<IArchivable>).GetField("_version", BindingFlags.Instance | BindingFlags.NonPublic);

        public readonly HistoryLoss Loss = new HistoryLoss();
        private Dictionary<IArchivable, Sighting> seen;
        private int seenTick;
        private int? seenVersion;

        public void Look(Archive archive, int tick)
        {
            Dictionary<IArchivable, Sighting> now = new Dictionary<IArchivable, Sighting>();
            foreach (IArchivable archivable in archive.ArchivablesListForReading)
            {
                if (archivable != null)
                {
                    now[archivable] = new Sighting(archivable.CreatedTicksGame, !archive.IsPinned(archivable) && archivable.CanCullArchivedNow);
                }
            }
            int? version = ListVersion == null ? (int?)null : (int)ListVersion.GetValue(archive.ArchivablesListForReading);
            Loss.Look++;
            if (seen != null)
            {
                foreach (IArchivable gone in seen.Keys.Where(each => !now.ContainsKey(each)))
                {
                    Loss.Record(HistoryEntry.Of(gone));
                }
                if ((version == null || version != seenVersion) && !Continuous(seen, seenTick, now))
                {
                    Loss.Unseen = Loss.Look;
                }
            }
            seen = now;
            seenTick = tick;
            seenVersion = version;
        }

        // The game culls the oldest entries that can go, after sorting by tick in an order that can
        // swap equal ticks. An entry that could go at both looks, still there with a tick before the
        // last look, shows nothing made since was culled. Without one, losses cannot be ruled out
        // while the tab is full or something went.
        internal static bool Continuous(Dictionary<IArchivable, Sighting> before, int beforeTick, Dictionary<IArchivable, Sighting> now)
        {
            foreach (KeyValuePair<IArchivable, Sighting> each in before)
            {
                Sighting later;
                if (each.Value.Cullable && each.Value.Tick < beforeTick && now.TryGetValue(each.Key, out later) && later.Cullable && later.Tick == each.Value.Tick)
                {
                    return true;
                }
            }
            return now.Values.Count(sighting => sighting.Cullable) < Archive.MaxNonPinnedArchivables && before.Keys.All(now.ContainsKey);
        }
    }

    internal static class HistoryWatch
    {
        private const int Interval = 15;
        private static object game;
        private static HistoryWatcher watcher = new HistoryWatcher();
        private static int frames;

        public static HistoryLoss Loss
        {
            get
            {
                Look();
                return watcher.Loss;
            }
        }

        public static void Update()
        {
            if (++frames % Interval == 0)
            {
                Look();
            }
        }

        private static void Look()
        {
            Archive archive = Current.Game == null ? null : Find.Archive;
            if (archive == null || !ReferenceEquals(game, Current.Game))
            {
                game = Current.Game;
                watcher = new HistoryWatcher();
            }
            if (archive != null)
            {
                watcher.Look(archive, Find.TickManager.TicksGame);
            }
        }
    }

    internal static class EventHistory
    {
        // Without a cursor, or with one from another load, the latest entries and a cursor covering
        // everything there. With one, the entries after it, oldest first, the messages repeated since
        // it was made, and whether entries after it were lost.
        public static HistoryPage Page(IList<HistoryEntry> all, string since, int limit, string loadId, int frame, int tick, HistoryLoss loss = null)
        {
            HistoryCursor cursor = string.IsNullOrWhiteSpace(since) ? null : HistoryCursor.Decode(since);
            int look = loss == null ? 0 : loss.Look;
            if (cursor == null || cursor.LoadId != loadId)
            {
                List<HistoryEntry> ordered = Ordered(all);
                List<HistoryEntry> latest = ordered.Skip(Math.Max(0, ordered.Count - limit)).ToList();
                return new HistoryPage(latest, new List<HistoryEntry>(), false, cursor != null, false, Everything(all, loadId, frame, tick, look));
            }

            List<HistoryEntry> fresh = Ordered(all.Where(entry => !cursor.Covers(entry)));
            List<HistoryEntry> page = fresh.Take(limit).ToList();
            bool more = fresh.Count > page.Count;
            List<HistoryEntry> repeated = all
                .Where(entry => entry.IsMessage && cursor.Covers(entry) && cursor.RepeatedSince(entry, frame))
                .OrderBy(entry => entry.Tick)
                .ThenBy(entry => entry.MessageId)
                .ToList();
            bool incomplete = loss != null && loss.Behind(cursor);
            HistoryCursor next = more ? After(cursor, page, frame, tick) : Everything(all, loadId, frame, tick, look);
            next.MessageId = Math.Max(next.MessageId, cursor.MessageId);
            return new HistoryPage(page, repeated, more, false, incomplete, next);
        }

        // Each kind in its own order (messages by id, the rest by tick and load id), merged by tick,
        // so a page is always a prefix of each kind and the cursor covers exactly what it returned.
        private static List<HistoryEntry> Ordered(IEnumerable<HistoryEntry> entries)
        {
            List<HistoryEntry> list = entries.ToList();
            List<HistoryEntry> messages = list.Where(entry => entry.IsMessage).OrderBy(entry => entry.MessageId).ToList();
            List<HistoryEntry> others = list.Where(entry => !entry.IsMessage).OrderBy(entry => entry.Tick).ThenBy(entry => entry.LoadId, StringComparer.Ordinal).ToList();
            List<HistoryEntry> merged = new List<HistoryEntry>(list.Count);
            int m = 0;
            int o = 0;
            while (m < messages.Count || o < others.Count)
            {
                if (m >= messages.Count || o < others.Count && others[o].Tick <= messages[m].Tick)
                {
                    merged.Add(others[o++]);
                }
                else
                {
                    merged.Add(messages[m++]);
                }
            }
            return merged;
        }

        private static HistoryCursor Everything(IList<HistoryEntry> all, string loadId, int frame, int tick, int look)
        {
            HistoryCursor cursor = new HistoryCursor
            {
                LoadId = loadId,
                IssueTick = tick,
                Frame = frame,
                Look = look,
                MessageId = all.Where(entry => entry.IsMessage).Select(entry => entry.MessageId).DefaultIfEmpty(0).Max(),
                Tick = Math.Max(tick, all.Where(entry => !entry.IsMessage).Select(entry => entry.Tick).DefaultIfEmpty(0).Max())
            };
            foreach (HistoryEntry entry in all.Where(entry => !entry.IsMessage && entry.Tick == cursor.Tick))
            {
                cursor.Ids.Add(entry.LoadId);
            }
            return cursor;
        }

        // A reader still paging has not caught up, so keeps the look it last caught up at.
        private static HistoryCursor After(HistoryCursor cursor, List<HistoryEntry> page, int frame, int tick)
        {
            HistoryCursor next = new HistoryCursor
            {
                LoadId = cursor.LoadId,
                IssueTick = tick,
                Frame = frame,
                Look = cursor.Look,
                MessageId = page.Where(entry => entry.IsMessage).Select(entry => entry.MessageId).DefaultIfEmpty(cursor.MessageId).Max(),
                Tick = cursor.Tick
            };
            next.Ids.UnionWith(cursor.Ids);
            HistoryEntry newest = page.Where(entry => !entry.IsMessage).LastOrDefault();
            if (newest != null && newest.Tick > cursor.Tick)
            {
                next.Tick = newest.Tick;
                next.Ids.Clear();
            }
            next.Ids.UnionWith(page.Where(entry => !entry.IsMessage && entry.Tick == next.Tick).Select(entry => entry.LoadId));
            return next;
        }
    }
}
