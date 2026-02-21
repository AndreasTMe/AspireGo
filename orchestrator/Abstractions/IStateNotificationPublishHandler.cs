using System.Threading;
using System.Threading.Tasks;

namespace Orchestrator.Abstractions;

public interface IStateNotificationPublishHandler
{
    ValueTask NotifyStateChangedAsync(CancellationToken cancellationToken);
}