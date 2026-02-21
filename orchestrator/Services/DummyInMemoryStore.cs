using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orchestrator.Abstractions;
using Orchestrator.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Orchestrator.Services;

internal sealed class DummyInMemoryStore : ITaskRepository, ITaskPublisher
{
    private enum TaskStatus
    {
        Pending,
        Leased,
        Completed,
        Failed
    }

    private sealed class TaskState
    {
        public string       TaskId         { get; init; } = default!;
        public string       Type           { get; init; } = default!;
        public byte[]       Payload        { get; init; } = [];
        public TaskPriority Priority       { get; init; }
        public TaskStatus   Status         { get; set; }
        public string?      LeaseOwnedById { get; set; }
        public DateTime?    LeaseExpiresAt { get; set; }
        public int          Attempt        { get; set; }
        public DateTime     NextAttemptAt  { get; set; } = DateTime.UtcNow;
        public DateTime     CreatedAt      { get; set; } = DateTime.UtcNow;

        // TODO: Cancellation propagation:
        // Add fields like CancelRequestedAt / CancelReason / Status=Cancelled.
        // TODO: Observability/correlation:
        // Store TraceId/CorrelationId for linking logs/metrics/traces end-to-end.
    }

    private readonly IStateNotificationPublishHandler _stateNotificationPublishHandler;
    private readonly TimeProvider                     _timeProvider;
    private readonly ILogger<DummyInMemoryStore>      _logger;

    // TODO:
    // Demo-only. For production, use durable storage (Cosmos/SQL) with concurrency control (ETag/rowversion)
    // to support multi-orchestrator safe coordination and prevent duplicate leasing across instances.
    private static readonly Dictionary<string, TaskState> Tasks = [];

    private static readonly Lock     Lock          = new();
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);

    public DummyInMemoryStore(
        IStateNotificationPublishHandler stateNotificationPublishHandler,
        IHostApplicationLifetime applicationLifetime,
        TimeProvider timeProvider,
        ILogger<DummyInMemoryStore> logger)
    {
        _stateNotificationPublishHandler = stateNotificationPublishHandler;
        _timeProvider                    = timeProvider;
        _logger                          = logger;

        _ = StartPendingTasksLoggingLoopAsync(applicationLifetime.ApplicationStopping);
    }

    private async Task StartPendingTasksLoggingLoopAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                // TODO:
                // Replace log-only reporting with metrics (gauges/counters/histograms).
                // Example: tasks_pending, tasks_leased, tasks_completed_total, dispatch_batch_duration_ms, retries_total, etc.
                // Also consider splitting "Pending ready" vs "Pending delayed (NextAttemptAt > now)".

                int pendingCount;
                int leasedCount;
                int completedCount;
                int failedCount;

                lock (Lock)
                {
                    pendingCount   = Tasks.Values.Count(static t => t.Status == TaskStatus.Pending);
                    leasedCount    = Tasks.Values.Count(static t => t.Status == TaskStatus.Leased);
                    completedCount = Tasks.Values.Count(static t => t.Status == TaskStatus.Completed);
                    failedCount    = Tasks.Values.Count(static t => t.Status == TaskStatus.Failed);
                }

                _logger.LogInformation(
                    "Tasks: Pending={Pending} Leased={Leased} Completed={Completed} Failed={Failed}",
                    pendingCount,
                    leasedCount,
                    completedCount,
                    failedCount);
            }
        }
        catch (OperationCanceledException)
        {
            // Host is shutting down; ignore.
        }
    }

    public async Task PublishAsync(TaskEnvelope envelope, CancellationToken cancellationToken)
    {
        lock (Lock)
        {
            Tasks[envelope.TaskId] = new TaskState
            {
                TaskId   = envelope.TaskId,
                Type     = envelope.Type,
                Payload  = envelope.Payload,
                Priority = envelope.Priority,
                Status   = TaskStatus.Pending,
            };
        }

        // TODO:
        // In multi-instance setups, in-memory state notifications won't wake other orchestrators.
        // Use a durable notification path (Cosmos change feed / Redis / SQL polling) and treat the repository as source of truth.
        await _stateNotificationPublishHandler.NotifyStateChangedAsync(cancellationToken);
    }

    public async IAsyncEnumerable<TaskEnvelope> LeaseNextBatchAsync(
        TaskPriority priority,
        string leaseOwnerId,
        int maxCount,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var candidates = new List<TaskState>();

        lock (Lock)
        {
            if (Tasks.Values.Count != 0)
            {
                var now = _timeProvider.GetUtcNow().DateTime;

                // TODO: Stuck task recovery / reaper:
                // Move expired leases back to Pending and (optionally) increment metrics / mark as "lease expired".
                // In durable stores, this should be an atomic update to prevent racing orchestrators.

                foreach (var task in Tasks.Values.Where(t => t.Status == TaskStatus.Leased && t.LeaseExpiresAt <= now))
                {
                    task.Status         = TaskStatus.Pending;
                    task.LeaseOwnedById = null;
                    task.LeaseExpiresAt = null;
                }

                candidates.AddRange(
                    Tasks.Values
                        .Where(t => t.Priority == priority && t.Status == TaskStatus.Pending && t.NextAttemptAt <= now)
                        .OrderBy(static t => t.CreatedAt)
                        .Take(maxCount));

                foreach (var task in candidates)
                {
                    // TODO: Multi-orchestrator safe lease acquisition:
                    // In a real DB, this must be an atomic "compare-and-set" so two dispatchers can't lease the same task.

                    task.Status         = TaskStatus.Leased;
                    task.LeaseOwnedById = leaseOwnerId;
                    task.LeaseExpiresAt = now + LeaseDuration;
                    task.Attempt++;
                }
            }
        }

        if (candidates.Count == 0)
        {
            yield break;
        }

        foreach (var task in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            yield return new TaskEnvelope
            {
                TaskId         = task.TaskId,
                Type           = task.Type,
                Payload        = task.Payload,
                Priority       = task.Priority,
                LeaseExpiresAt = task.LeaseExpiresAt!.Value
            };

            await Task.Yield();
        }
    }

    public async Task MarkCompletedAsync(string taskId, CancellationToken cancellationToken)
    {
        lock (Lock)
        {
            if (Tasks.TryGetValue(taskId, out var task))
            {
                task.Status         = TaskStatus.Completed;
                task.LeaseOwnedById = null;
                task.LeaseExpiresAt = null;
            }
        }

        // TODO: Retention/cleanup:
        // Periodically purge Completed/Failed tasks (or archive) to avoid unbounded growth.

        await _stateNotificationPublishHandler.NotifyStateChangedAsync(cancellationToken);
    }

    public async Task MarkFailedAsync(string taskId, string reason, bool retryable, CancellationToken cancellationToken)
    {
        lock (Lock)
        {
            if (!Tasks.TryGetValue(taskId, out var task))
            {
                return;
            }

            if (!retryable)
            {
                task.Status         = TaskStatus.Failed;
                task.LeaseOwnedById = null;
                task.LeaseExpiresAt = null;

                return;
            }

            // simple exponential backoff
            var delaySeconds = Math.Min(60, Math.Pow(2, task.Attempt));

            task.Status         = TaskStatus.Pending;
            task.LeaseOwnedById = null;
            task.LeaseExpiresAt = null;
            task.NextAttemptAt  = _timeProvider.GetUtcNow().DateTime.AddSeconds(delaySeconds);

            // TODO: Consider recording failure count + last failure reason/time for diagnostics and metrics.
        }

        await _stateNotificationPublishHandler.NotifyStateChangedAsync(cancellationToken);
    }

    public Task HeartbeatAsync(string leaseOwnerId, string taskId, CancellationToken cancellationToken)
    {
        lock (Lock)
        {
            if (Tasks.TryGetValue(taskId, out var task)
                && task.Status == TaskStatus.Leased
                && task.LeaseOwnedById == leaseOwnerId)
            {
                task.LeaseExpiresAt = _timeProvider.GetUtcNow().DateTime + LeaseDuration;
            }
        }

        return Task.CompletedTask;
    }
}