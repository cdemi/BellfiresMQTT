using BellfiresMQTTServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder();

builder.Services.AddHostedService<MertikFireplace>();
builder.Services.AddLogging(builder =>
{
    builder.AddSimpleConsole(config => {
        config.TimestampFormat = "[HH:mm:ss] ";
    });
});

var host = builder.Build();
host.Run();