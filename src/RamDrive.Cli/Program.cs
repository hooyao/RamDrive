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
