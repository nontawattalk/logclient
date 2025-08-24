using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System;
using System.IO;
using System.Threading.Tasks;

namespace WinSyslogAgent
{
    /// <summary>
    /// Entry point for the Windows Syslog Agent. This program uses the
    /// generic host builder from Microsoft.Extensions.Hosting to
    /// configure dependency injection, configuration and logging. It
    /// registers a background service (WinSyslogWorker) that subscribes
    /// to Windows Event Logs and forwards them to a remote syslog
    /// server using the selected formatter.
    /// </summary>
    public class Program
    {
        public static async Task Main(string[] args)
        {
            // Build a generic host. The configuration is loaded from
            // %ProgramData%\WinSyslogAgent\appsettings.json by default but
            // fallback values are also defined in code.
            var host = Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((hostingContext, config) =>
                {
                    // Use appsettings.json from ProgramData so that the
                    // configuration can be changed without recompiling.
                    var configPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        "WinSyslogAgent",
                        "appsettings.json");
                    config.AddJsonFile(configPath, optional: true, reloadOnChange: true);
                })
                .ConfigureServices((context, services) =>
                {
                    // Register our internal services. The worker reads
                    // events from Windows and uses the formatter factory
                    // and syslog client to emit messages.
                    services.AddSingleton<BookmarkStore>();
                    services.AddSingleton<FormatterFactory>();
                    services.AddSingleton<SyslogClient>();
                    services.AddHostedService<WinSyslogWorker>();
                })
                .Build();

            // Run the service. This will block until the service is
            // stopped (for example via SCM on Windows).
            await host.RunAsync();
        }
    }
}
