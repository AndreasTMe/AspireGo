using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var orchestrator = builder.AddProject<Projects.Orchestrator>("orchestrator");

builder.AddGolangApp(name: "workers", workingDirectory: "../workers")
    .WithEnvironment(context =>
    {
        var endpoint = orchestrator.GetEndpoint("http");
        context.EnvironmentVariables["ORCHESTRATOR_ENDPOINT"] = $"{endpoint.Host}:{endpoint.Port}";
    })
    .WithEnvironment("WORKER_POOLS_COUNT", "4")
    .WithEnvironment("WORKER_POOL_CAPACITY", "8")
    .WithEnvironment("WORKER_POOL_URGENT_PERCENTAGE", "25")
    .WithEnvironment("WORKER_HEARTBEAT_SECONDS", "1")
    .WaitFor(orchestrator);

var app = builder.Build();

app.Run();