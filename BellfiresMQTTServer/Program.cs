using BellfiresMQTTServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

Host.CreateDefaultBuilder(args).ConfigureServices((hostContext, services) =>
{
    services.AddHostedService<MertikFireplace>();
    services.AddLogging(builder =>
    {
        builder.AddSimpleConsole(config => {
            config.TimestampFormat = "[HH:mm:ss] ";
        });
    });
}).Build().Run();