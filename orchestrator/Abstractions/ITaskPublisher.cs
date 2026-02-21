using Orchestrator.Models;
using System.Threading;
using System.Threading.Tasks;

namespace Orchestrator.Abstractions;

public interface ITaskPublisher
{
    Task PublishAsync(TaskEnvelope envelope, CancellationToken cancellationToken);
}