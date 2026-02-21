using System.Threading;
using System.Threading.Tasks;

namespace Orchestrator.Abstractions;

public interface IStateNotificationReceiveHandler
{
    ValueTask<bool> WaitForStateChangeAsync(CancellationToken cancellationToken);
}