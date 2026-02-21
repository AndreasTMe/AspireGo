using Grpc.Core;
using Microsoft.Extensions.Logging;
using Orchestrator.Abstractions;
using Orchestrator.Dispatchers;
using Orchestrator.Extensions;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Orchestrator.Services;

internal sealed class TaskDispatcherService : TaskDispatcher.TaskDispatcherBase
{
    // TODO: Observability:
    // Consider emitting metrics (OpenTelemetry .NET) in addition to logs:
    // dispatched_total{priority}, dispatch_batch_duration_ms, credits_reserved, credits_refunded, no_tasks_cycles_total, etc.
    // Also consider Activity/Tracing for Connect/Dispatch/Complete/Fail.

    private static class LogEvents
    {
        public static readonly EventId ConnectStart          = new(1000, nameof(ConnectStart));
        public static readonly EventId ConnectStop           = new(1001, nameof(ConnectStop));
        public static readonly EventId HandshakeReceived     = new(1010, nameof(HandshakeReceived));
        public static readonly EventId CapacityUpdated       = new(1020, nameof(CapacityUpdated));
        public static readonly EventId DispatchCycle         = new(2000, nameof(DispatchCycle));
        public static readonly EventId DispatchBatch         = new(2010, nameof(DispatchBatch));
        public static readonly EventId CreditsRefunded       = new(2020, nameof(CreditsRefunded));
        public static readonly EventId NoTasksAvailable      = new(2030, nameof(NoTasksAvailable));
        public static readonly EventId TaskCompleted         = new(3000, nameof(TaskCompleted));
        public static readonly EventId TaskFailed            = new(3010, nameof(TaskFailed));
        public static readonly EventId TaskHeartbeatReceived = new(3020, nameof(TaskHeartbeatReceived));
    }

    private readonly ICreditsNotificationHandler      _creditsNotificationHandler;
    private readonly IStateNotificationReceiveHandler _stateNotificationReceiveHandler;
    private readonly ITaskRepository                  _repository;
    private readonly ILogger<TaskDispatcherService>   _logger;

    public TaskDispatcherService(
        ICreditsNotificationHandler creditsNotificationHandler,
        IStateNotificationReceiveHandler stateNotificationReceiveHandler,
        ITaskRepository repository,
        ILogger<TaskDispatcherService> logger)
    {
        _creditsNotificationHandler      = creditsNotificationHandler;
        _stateNotificationReceiveHandler = stateNotificationReceiveHandler;
        _repository                      = repository;
        _logger                          = logger;
    }

    public override async Task Connect(
        IAsyncStreamReader<ClientMessage> requestStream,
        IServerStreamWriter<ServerMessage> responseStream,
        ServerCallContext context)
    {
        var cancellationToken = context.CancellationToken;

        try
        {
            var workerPoolId = context.RequestHeaders.GetValue("X-Worker-Pool-Id");
            ArgumentException.ThrowIfNullOrWhiteSpace(workerPoolId);

            var leaseOwnerId = Guid.CreateVersion7().ToString("N");

            using var _ = _logger.BeginScope(
                new
                {
                    WorkerPoolId = workerPoolId,
                    LeaseOwnerId = leaseOwnerId,
                    context.Peer
                });

            _logger.LogInformation(LogEvents.ConnectStart, "Worker connected");

            // TODO: Authentication/authorization:
            // Validate worker identity (mTLS/JWT/API key) and verify WorkerPoolId is allowed.
            // TODO: Multi-node orchestrator:
            // Ensure state/credits notifications work across instances (no in-memory-only coordination).

            var receiveTask = ReceiveAsync(requestStream, workerPoolId, leaseOwnerId, cancellationToken);
            var sendTask    = SendAsync(responseStream, workerPoolId, leaseOwnerId, cancellationToken);

            await Task.WhenAny(receiveTask, sendTask);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(LogEvents.ConnectStop, "Connection cancelled");
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            _logger.LogDebug(ex, "gRPC cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in the dispatcher");
        }
        finally
        {
            _logger.LogInformation(LogEvents.ConnectStop, "Worker disconnected");
        }
    }

    private async Task ReceiveAsync(
        IAsyncStreamReader<ClientMessage> requestStream,
        string workerPoolId,
        string leaseOwnerId,
        CancellationToken cancellationToken)
    {
        await foreach (var message in requestStream.ReadAllAsync(cancellationToken))
        {
            switch (message.PayloadCase)
            {
                case ClientMessage.PayloadOneofCase.Ack:
                    _logger.LogInformation(
                        LogEvents.HandshakeReceived,
                        "Handshake received => MaxNormal={MaxNormal}, MaxUrgent={MaxUrgent}",
                        message.Ack.MaxNormal,
                        message.Ack.MaxUrgent);

                    // TODO: Worker scaling:
                    // Server may respond with desired concurrency/lanes based on queue depth and observed latency.

                    break;

                case ClientMessage.PayloadOneofCase.Capacity:
                    _logger.LogDebug(
                        LogEvents.CapacityUpdated,
                        "Capacity update => Normal={NormalCredit}, Urgent={UrgentCredit}",
                        message.Capacity.NormalCredit,
                        message.Capacity.UrgentCredit);

                    // TODO: If capacity updates are frequent, consider sampling/aggregation and metrics-first reporting.
                    await _creditsNotificationHandler.PushCreditsUpdateAsync(
                        workerPoolId,
                        message.Capacity.NormalCredit,
                        message.Capacity.UrgentCredit,
                        cancellationToken);
                    break;

                case ClientMessage.PayloadOneofCase.Completed:
                    await _repository.MarkCompletedAsync(message.Completed.TaskId, cancellationToken);
                    _logger.LogDebug(
                        LogEvents.TaskCompleted,
                        "Task completed => TaskId={TaskId}",
                        message.Completed.TaskId);

                    // TODO: End-to-end correlation:
                    // Include correlation/trace id in completion messages so logs can link dispatch => execute => completion.

                    break;

                case ClientMessage.PayloadOneofCase.Failed:
                    await _repository.MarkFailedAsync(
                        message.Failed.TaskId,
                        message.Failed.Reason,
                        message.Failed.Retryable,
                        cancellationToken);
                    _logger.LogWarning(
                        LogEvents.TaskFailed,
                        "Task failed => TaskId={TaskId}, Retryable={Retryable}, Reason={Reason}",
                        message.Failed.TaskId,
                        message.Failed.Retryable,
                        message.Failed.Reason);

                    // TODO: Retry policy:
                    // Centralize retry classification/backoff, emit metrics, and consider a Dead Letter Queue for non-retryable failures.

                    break;

                case ClientMessage.PayloadOneofCase.Heartbeat:
                    await _repository.HeartbeatAsync(leaseOwnerId, message.Heartbeat.TaskId, cancellationToken);
                    _logger.LogTrace(
                        LogEvents.TaskHeartbeatReceived,
                        "Heartbeat => TaskId={TaskId}",
                        message.Heartbeat.TaskId);
                    break;
            }
        }
    }

    private async Task SendAsync(
        IAsyncStreamWriter<ServerMessage> responseStream,
        string workerPoolId,
        string leaseOwnerId,
        CancellationToken cancellationToken)
    {
        var preferUrgent = true;

        while (!cancellationToken.IsCancellationRequested)
        {
            var creditsSignalTask = _creditsNotificationHandler
                .WaitForCreditsChangeAsync(workerPoolId, cancellationToken)
                .AsTask();
            var storeSignalTask = _stateNotificationReceiveHandler.WaitForStateChangeAsync(cancellationToken).AsTask();

            await Task.WhenAny(creditsSignalTask, storeSignalTask);

            if (!_creditsNotificationHandler.TryReserveAllCredits(
                    workerPoolId,
                    out var normalCredits,
                    out var urgentCredits))
            {
                continue;
            }

            _logger.LogDebug(
                LogEvents.DispatchCycle,
                "Dispatch cycle => ReservedNormal={ReservedNormal}, ReservedUrgent={ReservedUrgent}, PreferUrgent={PreferUrgent}",
                normalCredits,
                urgentCredits,
                preferUrgent);

            var reservedNormal = normalCredits;
            var reservedUrgent = urgentCredits;

            var dispatchedSomething = false;
            var sentUrgent          = 0;
            var sentNormal          = 0;

            if (preferUrgent && urgentCredits > 0)
            {
                var sent = await DispatchBatchAsync(
                    responseStream,
                    Models.TaskPriority.Urgent,
                    leaseOwnerId,
                    urgentCredits,
                    cancellationToken);

                sentUrgent          += sent;
                urgentCredits       -= sent;
                dispatchedSomething |= sent > 0;
            }

            if (normalCredits > 0)
            {
                var sent = await DispatchBatchAsync(
                    responseStream,
                    Models.TaskPriority.Normal,
                    leaseOwnerId,
                    normalCredits,
                    cancellationToken);

                sentNormal          += sent;
                normalCredits       -= sent;
                dispatchedSomething |= sent > 0;
            }

            if (!preferUrgent && urgentCredits > 0)
            {
                var sent = await DispatchBatchAsync(
                    responseStream,
                    Models.TaskPriority.Urgent,
                    leaseOwnerId,
                    urgentCredits,
                    cancellationToken);

                sentUrgent          += sent;
                urgentCredits       -= sent;
                dispatchedSomething |= sent > 0;
            }

            // TODO: Fair scheduling:
            // Replace preferUrgent flip-flop with weighted fair scheduling (e.g., 80/20) and/or backlog-aware weighting.
            preferUrgent = !preferUrgent;

            if (normalCredits > 0 || urgentCredits > 0)
            {
                _creditsNotificationHandler.RefundCredits(workerPoolId, normalCredits, urgentCredits);

                _logger.LogDebug(
                    LogEvents.CreditsRefunded,
                    "Unused credits refunded => UnusedNormal={UnusedNormal}, UnusedUrgent={UnusedUrgent}",
                    normalCredits,
                    urgentCredits);
            }

            if (dispatchedSomething)
            {
                _logger.LogInformation(
                    LogEvents.DispatchCycle,
                    "Dispatched => SentNormal={SentNormal}/{ReservedNormal}, SentUrgent={SentUrgent}/{ReservedUrgent}",
                    sentNormal,
                    reservedNormal,
                    sentUrgent,
                    reservedUrgent);
            }
            else
            {
                _logger.LogDebug(
                    LogEvents.NoTasksAvailable,
                    "No tasks available for dispatch (had credits) => ReservedNormal={ReservedNormal}, ReservedUrgent={ReservedUrgent}",
                    reservedNormal,
                    reservedUrgent);

                // If we had credits but found no tasks, wait for the next task signal to avoid spinning
                await _stateNotificationReceiveHandler.WaitForStateChangeAsync(cancellationToken);

                // TODO:
                // If "no tasks" happens frequently, consider:
                // - a short cooldown delay
                // - metrics/counters to alert on worker starvation vs empty queue
                // - backlog-aware dispatch (don't reserve credits when queue is empty)
            }
        }
    }

    private async Task<int> DispatchBatchAsync(
        IAsyncStreamWriter<ServerMessage> stream,
        Models.TaskPriority priority,
        string leaseOwnerId,
        int credits,
        CancellationToken cancellationToken)
    {
        var sent = 0;
        var sw   = Stopwatch.StartNew();

        await foreach (var envelope in _repository.LeaseNextBatchAsync(
                           priority,
                           leaseOwnerId,
                           credits,
                           cancellationToken))
        {
            // TODO: Server-initiated cancellation protocol:
            // Track in-flight tasks per worker so the server can send Cancel messages when needed.
            // TODO: Backpressure:
            // Detect slow clients (WriteAsync latency) and reduce batch sizes / dispatch cadence accordingly.

            await stream.WriteAsync(envelope.ToServerMessage(), cancellationToken);
            sent++;
        }

        sw.Stop();

        _logger.LogDebug(
            LogEvents.DispatchBatch,
            "Dispatch batch => Priority={Priority}, Credits={Credits}, Sent={Sent}, ElapsedMs={ElapsedMs}",
            priority,
            credits,
            sent,
            sw.ElapsedMilliseconds);

        if (sw.ElapsedMilliseconds >= 500 && sent > 0)
        {
            _logger.LogWarning(
                LogEvents.DispatchBatch,
                "Slow dispatch batch => Priority={Priority}, Sent={Sent}, ElapsedMs={ElapsedMs}",
                priority,
                sent,
                sw.ElapsedMilliseconds);
        }

        return sent;
    }
}