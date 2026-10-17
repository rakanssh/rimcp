using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using UnityEngine;
using Verse;

namespace RiMCP.Command
{
    // What one native save produced, before the file itself is checked.
    internal sealed class SaveAttempt
    {
        public string Name;
        public string Path;
        public DateTime? PreviousWriteUtc;
        public bool MarkerSeen;
        public int Errors;
        public string FirstError;
    }

    // Saving a game, and deciding whether it worked. The game's save catches and logs its own
    // exceptions, and so does every object saved inside it, so returning is not success, and neither
    // is a complete-looking file. A save succeeds only when nothing was logged as an error while it
    // ran, the log was visibly working throughout, and the file was written and is well-formed.
    internal static class SaveVerifier
    {
        private static int saves;

        // Counts errors logged on the saving thread, and whether a marker logged just before the save
        // came through, which shows the log was working. Attach before logging the marker; dispose in
        // a finally.
        internal sealed class LogWatch : IDisposable
        {
            private readonly int thread = Thread.CurrentThread.ManagedThreadId;
            public readonly string Marker;
            public bool MarkerSeen;
            public int Errors;
            public string FirstError;

            public LogWatch(string marker)
            {
                Marker = marker;
                Application.logMessageReceivedThreaded += OnLog;
            }

            internal void OnLog(string message, string stackTrace, LogType type)
            {
                // Other threads, such as another mod's worker, are not the save.
                if (Thread.CurrentThread.ManagedThreadId != thread)
                {
                    return;
                }
                if (message == Marker)
                {
                    MarkerSeen = true;
                    return;
                }
                if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                {
                    Errors++;
                    if (FirstError == null)
                    {
                        FirstError = message.Length > 300 ? message.Substring(0, 300) + "…" : message;
                    }
                }
            }

            public void Dispose()
            {
                Application.logMessageReceivedThreaded -= OnLog;
            }
        }

        // Main thread. The log can hide errors two ways, and both are cleared first: after 10,000
        // messages the game stops logging, which ResetMessageCount turns back on without clearing the
        // player's log; and repeated messages fold, which a marker unique to this save prevents.
        public static SaveAttempt Save(string name)
        {
            string path = GenFilePaths.FilePathForSavedGame(name);
            SaveAttempt attempt = new SaveAttempt
            {
                Name = name,
                Path = path,
                PreviousWriteUtc = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : (DateTime?)null
            };
            string marker = "RimMCP: saving \"" + name + "\" (" + (++saves) + ", " + DateTime.UtcNow.ToString("HH:mm:ss.fff") + ")";
            Log.ResetMessageCount();
            using (LogWatch watch = new LogWatch(marker))
            {
                Log.Message(marker);
                // Saved either way: without the marker the save still happens, but cannot be verified.
                GameDataSaveLoader.SaveGame(name);
                attempt.MarkerSeen = watch.MarkerSeen;
                attempt.Errors = watch.Errors;
                attempt.FirstError = watch.FirstError;
            }
            return attempt;
        }

        // Any thread. Null when the file was written after previousWriteUtc and is well-formed XML from
        // its savegame root and meta header through its closing tag; otherwise why not.
        public static string CheckFile(string path, DateTime? previousWriteUtc)
        {
            if (!File.Exists(path))
            {
                return "The save file was not written.";
            }
            if (previousWriteUtc.HasValue && File.GetLastWriteTimeUtc(path) <= previousWriteUtc.Value)
            {
                return "The save file was not updated.";
            }
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    int first = FirstSignificantByte(stream);
                    if (first != '<')
                    {
                        return "The save file is not plain XML, so it could not be checked for completeness.";
                    }
                    stream.Position = 0;
                    XmlReaderSettings settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = true, IgnoreComments = true, CloseInput = false };
                    using (XmlReader reader = XmlReader.Create(stream, settings))
                    {
                        reader.MoveToContent();
                        if (reader.NodeType != XmlNodeType.Element || reader.Name != "savegame")
                        {
                            return "The save file does not start with a savegame element.";
                        }
                        bool meta = false;
                        while (reader.Read())
                        {
                            if (!meta && reader.NodeType == XmlNodeType.Element && reader.Depth == 1)
                            {
                                if (reader.Name != "meta")
                                {
                                    return "The save file has no meta header.";
                                }
                                meta = true;
                            }
                        }
                        return meta ? null : "The save file has no meta header.";
                    }
                }
            }
            catch (XmlException ex)
            {
                return "The save file is incomplete or damaged: " + ex.Message;
            }
            catch (IOException ex)
            {
                return "The save file could not be read: " + ex.Message;
            }
        }

        // The first byte that is not a byte order mark or whitespace.
        private static int FirstSignificantByte(Stream stream)
        {
            int value;
            int index = 0;
            while ((value = stream.ReadByte()) != -1)
            {
                bool bom = index < 3 && (value == 0xEF || value == 0xBB || value == 0xBF);
                index++;
                if (!bom && !char.IsWhiteSpace((char)value))
                {
                    return value;
                }
            }
            return -1;
        }
    }

    // A save file's header: game version and mods from its meta element, and whether it is a
    // commitment-mode game from game/info. Reads only the start of the file.
    internal sealed class SaveHeader
    {
        public string GameVersion;
        public List<string> ModIds = new List<string>();
        public bool CommitmentMode;
        public string Problem;

        // Tracks the elements above the current one, since XmlReader has no parent pointer.
        public static SaveHeader Read(string path)
        {
            SaveHeader header = new SaveHeader();
            try
            {
                XmlReaderSettings settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = true, IgnoreComments = true };
                using (XmlReader reader = XmlReader.Create(path, settings))
                {
                    reader.MoveToContent();
                    if (reader.Name != "savegame")
                    {
                        header.Problem = "Not a savegame file.";
                        return header;
                    }
                    // stack[i] is the open element at depth i + 1. Reading an element's content
                    // already moves to the next node, so the loop advances itself only otherwise.
                    List<string> stack = new List<string>();
                    reader.Read();
                    while (!reader.EOF)
                    {
                        if (reader.NodeType == XmlNodeType.Element)
                        {
                            int depth = reader.Depth;
                            while (stack.Count >= depth)
                            {
                                stack.RemoveAt(stack.Count - 1);
                            }
                            string parent = stack.Count > 0 ? stack[stack.Count - 1] : null;
                            string section = stack.Count > 0 ? stack[0] : reader.Name;
                            string name = reader.Name;
                            if (depth == 2 && section == "meta" && name == "gameVersion")
                            {
                                header.GameVersion = reader.ReadElementContentAsString().Trim();
                                continue;
                            }
                            if (depth == 3 && section == "meta" && parent == "modIds" && name == "li")
                            {
                                header.ModIds.Add(reader.ReadElementContentAsString().Trim());
                                continue;
                            }
                            if (depth == 3 && section == "game" && parent == "info" && name == "permadeathMode")
                            {
                                header.CommitmentMode = string.Equals(reader.ReadElementContentAsString().Trim(), "True", StringComparison.OrdinalIgnoreCase);
                                continue;
                            }
                            if (!reader.IsEmptyElement)
                            {
                                stack.Add(name);
                            }
                        }
                        else if (reader.NodeType == XmlNodeType.EndElement)
                        {
                            // game/info is near the top; nothing after it is needed.
                            if (reader.Depth == 2 && reader.Name == "info" && stack.Count > 0 && stack[0] == "game")
                            {
                                return header;
                            }
                            while (stack.Count >= reader.Depth && stack.Count > 0)
                            {
                                stack.RemoveAt(stack.Count - 1);
                            }
                        }
                        reader.Read();
                    }
                }
            }
            catch (Exception ex)
            {
                header.Problem = "The header could not be read: " + ex.Message;
            }
            return header;
        }

        // Whether this save's mods match the running ones, in order and ignoring case, by package id or
        // folder name, as the game checks before loading.
        public bool ModsMatch(IList<string> runningIds, IList<string> runningFolders)
        {
            return SameList(ModIds, runningIds) || SameList(ModIds, runningFolders);
        }

        private static bool SameList(IList<string> a, IList<string> b)
        {
            return a != null && b != null && a.Count == b.Count && !a.Where((id, i) => !string.Equals(id, b[i], StringComparison.OrdinalIgnoreCase)).Any();
        }
    }
}
