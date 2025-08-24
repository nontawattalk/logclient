using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace WinSyslogAgent
{
    /// <summary>
    /// WinSyslogWorker is a BackgroundService that subscribes to one or
    /// more Windows Event Logs (Application, System and Security by
    /// default) and forwards each event to the configured syslog server.
    /// Events are batched to avoid sending one network packet per event.
    /// A bookmark is persisted per channel so that the agent can pick
    /// up where it left off after a restart.
    /// </summary>
    public class WinSyslogWorker : BackgroundService
    {
        private readonly ILogger<WinSyslogWorker> _logger;
        private readonly IConfiguration _configuration;
        private readonly FormatterFactory _factory;
        private readonly SyslogClient _client;
        private readonly BookmarkStore _bookmarkStore;
        private IEventFormatter _formatter;
        private readonly ConcurrentQueue<EventRecordWrittenEventArgs> _queue = new();
        private readonly List<EventLogWatcher> _watchers = new();

        public WinSyslogWorker(
            ILogger<WinSyslogWorker> logger,
            IConfiguration configuration,
            FormatterFactory factory,
            SyslogClient client,
            BookmarkStore bookmarkStore)
        {
            _logger = logger;
            _configuration = configuration;
            _factory = factory;
            _client = client;
            _bookmarkStore = bookmarkStore;
        }

        /// <summary>
        /// Creates watchers for all configured channels and processes
        /// queued events in batches until the service is stopped.
        /// </summary>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var agent = _configuration.GetSection("agent");
            var mode = agent.GetValue("mode", "rfc5424");
            _formatter = _factory.CreateFormatter(mode, agent);

            // Determine which channels to subscribe to. Fall back to the
            // typical trio if not configured.
            var channels = agent.GetSection("channels").Get<string[]>() ?? new[] { "Application", "System", "Security" };
            foreach (var ch in channels)
            {
                try
                {
                    StartWatcher(ch);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to start watcher for {Channel}", ch);
                }
            }

            int batchSize = agent.GetValue("sendBatchSize", 50);
            var interval = TimeSpan.FromMilliseconds(agent.GetValue("sendIntervalMs", 200));

            // Main send loop: dequeue events, format and send them.
            while (!stoppingToken.IsCancellationRequested)
            {
                var currentBatch = new List<EventRecordWrittenEventArgs>();
                while (currentBatch.Count < batchSize && _queue.TryDequeue(out var evt))
                {
                    currentBatch.Add(evt);
                }

                if (currentBatch.Count > 0)
                {
                    foreach (var e in currentBatch)
                    {
                        try
                        {
                            var line = _formatter.Format(e.EventRecord);
                            await _client.SendAsync(line).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error while formatting or sending event");
                        }
                    }
                }

                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Creates an EventLogWatcher for a channel and starts listening
        /// immediately. If a bookmark exists for this channel it will
        /// resume from that point.
        /// </summary>
        private void StartWatcher(string channel)
        {
            var query = new EventLogQuery(channel, PathType.LogName);
            var bookmark = _bookmarkStore.Load(channel);
            if (bookmark != null)
            {
                query.Bookmark = bookmark;
            }
            var watcher = new EventLogWatcher(query);
            watcher.EventRecordWritten += (s, e) =>
            {
                if (e == null) return;
                if (e.EventException != null)
                {
                    _logger.LogError(e.EventException, "Error reading event from {Channel}", channel);
                    return;
                }
                _queue.Enqueue(e);
                // Immediately persist the bookmark so that we don't
                // duplicate events after a crash.
                try
                {
                    _bookmarkStore.Update(channel, e.EventRecord.Bookmark);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to update bookmark for {Channel}", channel);
                }
            };
            watcher.Enabled = true;
            _watchers.Add(watcher);
            _logger.LogInformation("Started watcher for {Channel}", channel);
        }

        /// <summary>
        /// Stops watchers when the service is stopping.
        /// </summary>
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            foreach (var w in _watchers)
            {
                w.Enabled = false;
            }
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
