using BellfiresMQTTServer;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddConsole();
builder.Services.AddHostedService<MertikFireplace>();
var app = builder.Build();

app.Run();
