using Orchestrator.Abstractions;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Orchestrator.Services;

internal sealed class DispatcherSignals
    : IStateNotificationPublishHandler, IStateNotificationReceiveHandler, ICreditsNotificationHandler
{
    private readonly record struct CapacityCredits(int Normal, int Urgent);

    // TODO: Multi-orchestrator deployments:
    // These in-memory channels/dictionaries won't coordinate across instances. Consider replacing signals with
    // a distributed mechanism (Cosmos change feed, Redis pub/sub + repository as truth, SQL notifications/polling).
    // Also consider durability: on restart, in-memory signals are lost.
    private readonly Channel<bool> _changeSignal = Channel.CreateUnbounded<bool>(
        new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });

    // TODO: WorkerPoolId lifecycle:
    // Consider cleanup of per-worker channels/locks when a worker disconnects to avoid unbounded memory growth in long-running systems.
    private readonly ConcurrentDictionary<string, Channel<bool>>   _creditsWakeSignals = [];
    private readonly ConcurrentDictionary<string, CapacityCredits> _creditsByWorker    = [];
    private readonly ConcurrentDictionary<string, Lock>            _creditsLocks       = [];

    public ValueTask NotifyStateChangedAsync(CancellationToken cancellationToken) =>
        _changeSignal.Writer.WriteAsync(true, cancellationToken);

    public ValueTask<bool> WaitForStateChangeAsync(CancellationToken cancellationToken) =>
        _changeSignal.Reader.WaitToReadAsync(cancellationToken);

    public ValueTask<bool> WaitForCreditsChangeAsync(string workerPoolId, CancellationToken cancellationToken) =>
        GetOrAddCreditsWake(workerPoolId).Reader.WaitToReadAsync(cancellationToken);

    public ValueTask PushCreditsUpdateAsync(
        string workerPoolId,
        int normal,
        int urgent,
        CancellationToken cancellationToken)
    {
        // TODO:
        // Since credits are deltas, consider de-duplication/sequence numbers to prevent double-apply on retries.

        var gate = _creditsLocks.GetOrAdd(workerPoolId, static _ => new Lock());
        lock (gate)
        {
            _creditsByWorker.AddOrUpdate(
                workerPoolId,
                static (_, delta) => new CapacityCredits(delta.Normal, delta.Urgent),
                static (_, current, delta) => new CapacityCredits(
                    current.Normal + delta.Normal,
                    current.Urgent + delta.Urgent),
                new CapacityCredits(normal, urgent));
        }

        return GetOrAddCreditsWake(workerPoolId).Writer.WriteAsync(true, cancellationToken);
    }

    public bool TryReserveAllCredits(string workerPoolId, out int normalCredits, out int urgentCredits)
    {
        normalCredits = 0;
        urgentCredits = 0;

        var gate = _creditsLocks.GetOrAdd(workerPoolId, static _ => new Lock());
        lock (gate)
        {
            if (!_creditsByWorker.TryGetValue(workerPoolId, out var credits))
            {
                return false;
            }

            normalCredits = credits.Normal;
            urgentCredits = credits.Urgent;

            _creditsByWorker[workerPoolId] = new CapacityCredits(0, 0);
        }

        // TODO:
        // This drain couples credits and state signals. If more signal types are added, consider separating
        // drains to avoid losing unrelated wakeups.

        while (GetOrAddCreditsWake(workerPoolId).Reader.TryRead(out _))
        {
            // Drain wakes
        }

        while (_changeSignal.Reader.TryRead(out _))
        {
            // Drain changes
        }

        return normalCredits > 0 || urgentCredits > 0;
    }

    public void RefundCredits(string workerPoolId, int normal, int urgent)
    {
        if (normal == 0 && urgent == 0)
        {
            return;
        }

        var gate = _creditsLocks.GetOrAdd(workerPoolId, static _ => new Lock());
        lock (gate)
        {
            _creditsByWorker.AddOrUpdate(
                workerPoolId,
                static (_, delta) => new CapacityCredits(delta.Normal, delta.Urgent),
                static (_, current, delta) => new CapacityCredits(
                    current.Normal + delta.Normal,
                    current.Urgent + delta.Urgent),
                new CapacityCredits(normal, urgent));
        }

        GetOrAddCreditsWake(workerPoolId).Writer.TryWrite(true);
    }

    private Channel<bool> GetOrAddCreditsWake(string workerPoolId) =>
        _creditsWakeSignals.GetOrAdd(
            workerPoolId,
            _ => Channel.CreateUnbounded<bool>(
                new UnboundedChannelOptions
                {
                    SingleReader = false,
                    SingleWriter = false
                }));
}