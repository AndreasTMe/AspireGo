using Microsoft.Extensions.Hosting;
using Orchestrator.Abstractions;
using Orchestrator.Models;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Orchestrator.Services;

internal sealed class DummySource : BackgroundService
{
    private static int _count = 1;

    private readonly ITaskPublisher _publisher;

    public DummySource(ITaskPublisher publisher)
    {
        _publisher = publisher;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (Random.Shared.NextDouble() < 0.80)
                await PublishSingleAsync(stoppingToken);
            else
                await PublishBurstAsync(stoppingToken);

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    private Task PublishSingleAsync(CancellationToken cancellationToken)
    {
        var envelope = new TaskEnvelope
        {
            TaskId         = $"id-{_count++}",
            Type           = string.Empty,
            Payload        = "Dummy data"u8.ToArray(),
            Priority       = NextPriority(),
            LeaseExpiresAt = DateTime.UtcNow
        };
        return _publisher.PublishAsync(envelope, cancellationToken);
    }

    private Task PublishBurstAsync(CancellationToken cancellationToken)
    {
        var tasks = Enumerable.Range(0, 25)
            .Select(static _ =>
                new TaskEnvelope
                {
                    TaskId         = $"id-{_count++}",
                    Type           = string.Empty,
                    Payload        = "Dummy data"u8.ToArray(),
                    Priority       = NextPriority(),
                    LeaseExpiresAt = DateTime.UtcNow
                })
            .Select(envelope => _publisher.PublishAsync(envelope, cancellationToken));
        return Task.WhenAll(tasks);
    }

    private static TaskPriority NextPriority() =>
        Random.Shared.NextDouble() < 0.80 ? TaskPriority.Normal : TaskPriority.Urgent;
}