using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orchestrator.Abstractions;
using Orchestrator.Services;
using Serilog;
using Serilog.Events;
using System;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog(static (_, configuration) =>
{
    configuration
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .WriteTo.Console(
            outputTemplate:
            "{Timestamp:yyyy/MM/dd HH:mm:ss} [{Level:u4}] [{SourceContext:l}] {Message:lj}{NewLine}{Exception}");
});

builder.Services.AddGrpc();

builder.Services.TryAddSingleton(TimeProvider.System);

builder.Services.AddSingleton<DispatcherSignals>();
builder.Services.AddSingleton<IStateNotificationPublishHandler>(static sp =>
    sp.GetRequiredService<DispatcherSignals>());
builder.Services.AddSingleton<IStateNotificationReceiveHandler>(static sp =>
    sp.GetRequiredService<DispatcherSignals>());
builder.Services.AddSingleton<ICreditsNotificationHandler>(static sp => sp.GetRequiredService<DispatcherSignals>());

builder.Services.AddSingleton<DummyInMemoryStore>();
builder.Services.AddSingleton<ITaskPublisher>(static sp => sp.GetRequiredService<DummyInMemoryStore>());
builder.Services.AddSingleton<ITaskRepository>(static sp => sp.GetRequiredService<DummyInMemoryStore>());

builder.Services.AddHostedService<DummySource>();

var app = builder.Build();

app.MapGrpcService<TaskDispatcherService>();

await app.RunAsync();