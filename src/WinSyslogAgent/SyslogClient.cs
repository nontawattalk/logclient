using System;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace WinSyslogAgent
{
    /// <summary>
    /// SyslogClient encapsulates the network transport for sending
    /// formatted syslog messages. It supports UDP and TCP. For TCP
    /// transport, it will prefix each message with its length and a
    /// space (octet-counted framing per RFC6587).
    /// </summary>
    public class SyslogClient
    {
        private readonly ILogger<SyslogClient> _logger;
        private readonly string _server;
        private readonly int _port;
        private readonly string _protocol;

        public SyslogClient(ILogger<SyslogClient> logger, IConfiguration configuration)
        {
            _logger = logger;
            var agent = configuration.GetSection("agent");
            _server = agent.GetValue("server", "127.0.0.1");
            _port = agent.GetValue("port", 514);
            _protocol = agent.GetValue("protocol", "udp").ToLowerInvariant();
        }

        /// <summary>
        /// Asynchronously sends a syslog message. For UDP the message is sent
        /// in a single datagram; for TCP the message is length-prefixed.
        /// </summary>
        public async Task SendAsync(string message)
        {
            var data = Encoding.UTF8.GetBytes(message);
            try
            {
                if (_protocol == "udp")
                {
                    using var client = new UdpClient();
                    await client.SendAsync(data, data.Length, _server, _port).ConfigureAwait(false);
                }
                else
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(_server, _port).ConfigureAwait(false);
                    using var stream = client.GetStream();
                    // Prefix the length followed by a space for TCP
                    var prefix = Encoding.ASCII.GetBytes($"{data.Length} ");
                    await stream.WriteAsync(prefix, 0, prefix.Length).ConfigureAwait(false);
                    await stream.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send syslog message");
            }
        }
    }
}
