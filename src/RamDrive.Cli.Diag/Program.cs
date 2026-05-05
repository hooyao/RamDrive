using System.Runtime.Versioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RamDrive.Cli;
using RamDrive.Cli.Diag;
using RamDrive.Core.Configuration;
using RamDrive.Core.FileSystem;
using RamDrive.Core.Memory;
using RamDrive.Diagnostics.DifferentialChecker;
using RamDrive.Diagnostics.MemfsReference;
using WinFsp.Native;

[assembly: SupportedOSPlatform("windows")]

try
{
    var builder = Host.CreateDefaultBuilder(args)
        .UseContentRoot(AppContext.BaseDirectory)
        .ConfigureAppConfiguration((_, config) =>
        {
            config.AddJsonFile("appsettings.jsonc", optional: false, reloadOnChange: false);
            config.AddCommandLine(args);
        })
        .ConfigureLogging((context, logging) =>
        {
            logging.ClearProviders();
            logging.AddSimpleConsole(o => { o.TimestampFormat = "HH:mm:ss "; o.SingleLine = true; });
            logging.AddConfiguration(context.Configuration.GetSection("Logging"));
        });

    builder.ConfigureServices((context, services) =>
    {
        services.Configure<RamDriveOptions>(context.Configuration.GetSection("RamDrive"));
        services.AddSingleton<PagePool>();
        services.AddSingleton<RamFileSystem>();
        services.AddSingleton<WinFspRamAdapter>();
        services.AddSingleton<IFileSystem>(sp =>
        {
            var ram = sp.GetRequiredService<WinFspRamAdapter>();
            var opts = sp.GetRequiredService<IOptions<RamDriveOptions>>().Value;
            var reference = new MemfsReferenceFs(opts.CapacityMb);
            return new DifferentialAdapter(ram, reference);
        });
        services.AddHostedService<DiagHostedService>();
    });

    var host = builder.Build();
    Console.WriteLine("RamDrive.Diag — DifferentialAdapter active. Diff will throw on first divergence.");
    await host.RunAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Fatal: {ex.GetType().Name}: {ex.Message}");
    if (ex.InnerException != null)
        Console.Error.WriteLine($"Inner: {ex.InnerException.Message}");
    Environment.ExitCode = 1;
}
