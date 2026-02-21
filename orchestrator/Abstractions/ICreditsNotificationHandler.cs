using System.Threading;
using System.Threading.Tasks;

namespace Orchestrator.Abstractions;

public interface ICreditsNotificationHandler
{
    ValueTask<bool> WaitForCreditsChangeAsync(string workerPoolId, CancellationToken cancellationToken);

    ValueTask PushCreditsUpdateAsync(string workerPoolId, int normal, int urgent, CancellationToken cancellationToken);

    bool TryReserveAllCredits(string workerPoolId, out int normalCredits, out int urgentCredits);

    public void RefundCredits(string workerPoolId, int normal, int urgent);
}