using System.Runtime.Versioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RamDrive.Cli;
using RamDrive.Core.Configuration;
using RamDrive.Core.FileSystem;
using RamDrive.Core.Memory;

[assembly: SupportedOSPlatform("windows")]

try
{
    var builder = Host.CreateDefaultBuilder(args)
        .UseWindowsService(options => options.ServiceName = "RamDrive")
        .UseContentRoot(AppContext.BaseDirectory)
        .ConfigureAppConfiguration((_, config) =>
        {
            config.AddJsonFile("appsettings.jsonc", optional: false, reloadOnChange: false);
            config.AddJsonFile("appsettings.dev.jsonc", optional: true, reloadOnChange: false);
            config.AddCommandLine(args);
        })
        .ConfigureLogging((context, logging) =>
        {
            logging.ClearProviders();
            logging.AddSimpleConsole(options =>
            {
                options.TimestampFormat = "HH:mm:ss ";
                options.SingleLine = true;
            });
            logging.AddEventLog(eventLog =>
            {
                eventLog.SourceName = "RamDrive";
                eventLog.LogName = "Application";
            });
            logging.AddConfiguration(context.Configuration.GetSection("Logging"));
        });

    builder.ConfigureServices((context, services) =>
    {
        services.Configure<RamDriveOptions>(context.Configuration.GetSection("RamDrive"));

        services.AddSingleton<PagePool>();
        services.AddSingleton<RamFileSystem>();
        services.AddSingleton<WinFspRamAdapter>();
        services.AddHostedService<WinFspHostedService>();
    });

    var host = builder.Build();
    await host.RunAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Fatal error: {ex.Message}");
    Environment.ExitCode = 1;
}

// If a fatal error occurred and we're running in a console (not as a Windows Service),
// wait for user input so they can read the error message before the window closes.
if (Environment.ExitCode != 0 && Environment.UserInteractive && !Console.IsInputRedirected)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("Press any key to exit...");
    try { Console.ReadKey(intercept: true); } catch { }
}
