using System;
using System.Diagnostics.Eventing.Reader;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace WinSyslogAgent
{
    /// <summary>
    /// Factory for creating event formatters. A formatter is chosen
    /// based on the "mode" setting in configuration. Supported modes
    /// include rfc3164, rfc5424 and custom. Formatters implement
    /// IEventFormatter and produce a single line syslog message from
    /// an EventRecord.
    /// </summary>
    public class FormatterFactory
    {
        public IEventFormatter CreateFormatter(string mode, IConfigurationSection agent)
        {
            return mode?.ToLowerInvariant() switch
            {
                "rfc3164" => new Rfc3164Formatter(agent),
                "rfc5424" => new Rfc5424Formatter(agent),
                "custom"  => new CustomFormatter(agent.GetValue<string>("customTemplate")),
                _          => new Rfc5424Formatter(agent),
            };
        }
    }

    /// <summary>
    /// The interface for formatting Windows Event Records into syslog
    /// messages. Implementations may choose any RFC or custom format.
    /// </summary>
    public interface IEventFormatter
    {
        string Format(EventRecord record);
    }

    /// <summary>
    /// Implements the legacy BSD Syslog protocol (RFC 3164). Messages
    /// have the form <PRI>TIMESTAMP HOST TAG: MSG. The timestamp
    /// does not include a year or timezone and uses the local time zone.
    /// </summary>
    public class Rfc3164Formatter : IEventFormatter
    {
        private readonly IConfigurationSection _config;
        private readonly string _hostname;
        private readonly string _appName;

        public Rfc3164Formatter(IConfigurationSection agent)
        {
            _config = agent;
            _hostname = agent.GetValue<string>("hostname") ?? Environment.MachineName;
            _appName  = agent.GetValue<string>("appName")  ?? "WinSyslogAgent";
        }

        public string Format(EventRecord record)
        {
            // Facility defaults to local0 (16) if not mapped. Severity
            // uses the numeric level modulo 8 per RFC 5424 mapping.
            int facility = 16;
            var facilityMap = _config.GetSection("facilityMap");
            if (facilityMap.Exists())
            {
                var value = facilityMap.GetValue<string>(record.LogName);
                if (!string.IsNullOrEmpty(value) && int.TryParse(value, out var f))
                {
                    facility = f;
                }
            }
            int severity = Math.Clamp((int)record.Level, 0, 7);
            int pri = facility * 8 + severity;

            var timestamp = record.TimeCreated?.ToLocalTime().ToString("MMM dd HH:mm:ss");
            var tag = _appName;
            var msg = record.FormatDescription() ?? string.Empty;

            return $"<{pri}>{timestamp} {_hostname} {tag}: {msg}";
        }
    }

    /// <summary>
    /// Implements the modern IETF Syslog protocol (RFC 5424). Each
    /// message begins with <PRI>1 TIMESTAMP HOST APP PROCID MSGID
    /// [STRUCTURED-DATA] MSG. This formatter constructs a structured
    /// data element prefixed with win@48577 containing metadata about
    /// the event such as channel, provider and level.
    /// </summary>
    public class Rfc5424Formatter : IEventFormatter
    {
        private readonly IConfigurationSection _config;
        private readonly string _hostname;
        private readonly string _appName;

        public Rfc5424Formatter(IConfigurationSection agent)
        {
            _config = agent;
            _hostname = agent.GetValue<string>("hostname") ?? Environment.MachineName;
            _appName  = agent.GetValue<string>("appName")  ?? "WinSyslogAgent";
        }

        public string Format(EventRecord record)
        {
            int facility = 16;
            var facilityMap = _config.GetSection("facilityMap");
            if (facilityMap.Exists())
            {
                var value = facilityMap.GetValue<string>(record.LogName);
                if (!string.IsNullOrEmpty(value) && int.TryParse(value, out var f))
                {
                    facility = f;
                }
            }
            int severity = Math.Clamp((int)record.Level, 0, 7);
            int pri = facility * 8 + severity;

            var timestamp = record.TimeCreated?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
            var procId = record.ProcessId?.ToString() ?? "-";
            var msgId = record.Id.ToString();
            var sd = BuildStructuredData(record);
            var msg = record.FormatDescription() ?? string.Empty;
            return $"<{pri}>1 {timestamp} {_hostname} {_appName} {procId} {msgId} {sd} {msg}";
        }

        /// <summary>
        /// Builds a single structured data element keyed with win@48577
        /// containing useful metadata. Values are escaped per RFC5424.
        /// </summary>
        public string BuildStructuredData(EventRecord record)
        {
            var sb = new StringBuilder();
            sb.Append("[win@48577");
            void Add(string key, string? value)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    sb.Append(' ');
                    sb.Append(key);
                    sb.Append("=");
                    sb.Append('"');
                    sb.Append(Escape(value));
                    sb.Append('"');
                }
            }
            Add("channel", record.LogName);
            Add("provider", record.ProviderName);
            Add("level", record.LevelDisplayName);
            Add("recordId", record.RecordId?.ToString());
            Add("computer", record.MachineName);
            if (record.UserId != null)
                Add("user", record.UserId.Value.ToString());
            sb.Append(']');
            return sb.ToString();
        }

        private static string Escape(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }

    /// <summary>
    /// Implements a simple token-based custom formatter. Tokens in the
    /// template string are enclosed in braces, e.g. {timestamp:O} or
    /// {event_id}. At runtime they are replaced with values from the
    /// EventRecord. A default template is used if none is provided.
    /// </summary>
    public class CustomFormatter : IEventFormatter
    {
        private readonly string _template;

        public CustomFormatter(string template)
        {
            _template = string.IsNullOrEmpty(template)
                ? "{timestamp:O} {hostname} [{channel}] id={event_id} lvl={level} msg={message}"
                : template;
        }

        public string Format(EventRecord record)
        {
            string ReplaceToken(string token)
            {
                var parts = token.Split(':', 2);
                var name = parts[0];
                var fmt  = parts.Length > 1 ? parts[1] : null;
                return name switch
                {
                    "timestamp"  => record.TimeCreated?.ToUniversalTime().ToString(fmt ?? "O") ?? string.Empty,
                    "hostname"   => Environment.MachineName,
                    "computer"   => record.MachineName,
                    "channel"    => record.LogName,
                    "provider"   => record.ProviderName,
                    "event_id"   => record.Id.ToString(),
                    "level"      => record.LevelDisplayName,
                    "opcode"     => record.OpcodeDisplayName,
                    "task"       => record.TaskDisplayName,
                    "keywords"   => record.KeywordsDisplayNames != null ? string.Join(",", record.KeywordsDisplayNames) : string.Empty,
                    "user"       => record.UserId?.ToString() ?? string.Empty,
                    "record_id"  => record.RecordId?.ToString() ?? string.Empty,
                    "process_id" => record.ProcessId?.ToString() ?? string.Empty,
                    "thread_id"  => record.ThreadId?.ToString() ?? string.Empty,
                    "message"    => record.FormatDescription() ?? string.Empty,
                    _             => string.Empty
                };
            }

            var sb = new StringBuilder();
            int pos = 0;
            while (pos < _template.Length)
            {
                int start = _template.IndexOf('{', pos);
                if (start < 0)
                {
                    sb.Append(_template.AsSpan(pos));
                    break;
                }
                sb.Append(_template.AsSpan(pos, start - pos));
                int end = _template.IndexOf('}', start);
                if (end < 0)
                {
                    // Unterminated token; append the rest verbatim.
                    sb.Append(_template.AsSpan(start));
                    break;
                }
                var token = _template.Substring(start + 1, end - start - 1);
                sb.Append(ReplaceToken(token));
                pos = end + 1;
            }
            return sb.ToString();
        }
    }
}
