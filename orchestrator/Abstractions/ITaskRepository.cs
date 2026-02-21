using Orchestrator.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Orchestrator.Abstractions;

public interface ITaskRepository
{
    IAsyncEnumerable<TaskEnvelope> LeaseNextBatchAsync(
        TaskPriority priority,
        string leaseOwnerId,
        int maxCount,
        CancellationToken cancellationToken);

    Task MarkCompletedAsync(string taskId, CancellationToken cancellationToken);

    Task MarkFailedAsync(string taskId, string reason, bool retryable, CancellationToken cancellationToken);

    Task HeartbeatAsync(string leaseOwnerId, string taskId, CancellationToken cancellationToken);
}