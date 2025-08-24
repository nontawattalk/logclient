using System;
using System.Collections.Concurrent;
using System.Diagnostics.Eventing.Reader;
using System.IO;

namespace WinSyslogAgent
{
    /// <summary>
    /// BookmarkStore manages per-channel EventLog bookmarks. A bookmark
    /// records the last processed event so that the agent can resume
    /// without missing or duplicating events after a restart. Bookmarks
    /// are stored as plain XML strings in the ProgramData\WinSyslogAgent\bookmarks
    /// directory.
    /// </summary>
    public class BookmarkStore
    {
        private readonly string _directory;
        private readonly ConcurrentDictionary<string, EventBookmark?> _cache = new();

        public BookmarkStore()
        {
            _directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "WinSyslogAgent",
                "bookmarks");
            Directory.CreateDirectory(_directory);
        }

        private string GetPath(string channel) => Path.Combine(_directory, channel + ".bookmark");

        /// <summary>
        /// Load a bookmark for the specified channel, or null if none exists.
        /// </summary>
        public EventBookmark? Load(string channel)
        {
            try
            {
                // Use in-memory cache to avoid parsing XML repeatedly.
                if (_cache.TryGetValue(channel, out var cached))
                {
                    return cached;
                }
                var path = GetPath(channel);
                if (!File.Exists(path))
                {
                    _cache[channel] = null;
                    return null;
                }
                var xml = File.ReadAllText(path);
                var bookmark = new EventBookmark(xml);
                _cache[channel] = bookmark;
                return bookmark;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Persist a bookmark for the specified channel.
        /// </summary>
        public void Update(string channel, EventBookmark? bookmark)
        {
            if (bookmark == null)
                return;
            try
            {
                var path = GetPath(channel);
                File.WriteAllText(path, bookmark.BookmarkText);
                _cache[channel] = bookmark;
            }
            catch
            {
                // swallow exceptions to avoid crashing the agent. Logging
                // happens at the call site.
            }
        }
    }
}
}
