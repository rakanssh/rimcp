using System.Collections.Generic;

namespace RiMCP.Bridge
{
    internal sealed class RecentLog
    {
        private readonly int capacity;
        private readonly Queue<string> lines = new Queue<string>();

        public RecentLog(int capacity)
        {
            this.capacity = capacity;
        }

        public IEnumerable<string> Lines
        {
            get
            {
                lock (lines)
                {
                    return lines.ToArray();
                }
            }
        }

        public void Add(string line)
        {
            lock (lines)
            {
                while (lines.Count >= capacity)
                {
                    lines.Dequeue();
                }
                lines.Enqueue(line);
            }
        }
    }
}

