package services

import (
	"context"
	"math/rand"
	"sync"
	"time"

	utils "main/utilities"

	pb "main/.gen/proto"

	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials/insecure"
	"google.golang.org/grpc/metadata"
)

type WorkerPool struct {
	id     string
	cfg    WorkerPoolConfiguration
	conn   *grpc.ClientConn
	client pb.TaskDispatcherClient

	stream pb.TaskDispatcher_ConnectClient

	reporter *CapacityReporter

	// queues
	urgentQ chan *pb.TaskAssignment
	normalQ chan *pb.TaskAssignment

	// TODO: Priority lanes / weighted fair scheduling:
	// - Add additional lanes (e.g., "bulk", "interactive") or replace 2-queue model with N-queue + weights.
	// TODO: Priority aging (prevent starvation permanently):
	// - Introduce age-based promotion from normal => urgent (or a score) if items wait too long.

	// state
	mu           sync.RWMutex
	active       int
	activeUrgent int

	// TODO: Adaptive capacity (worker auto-scales concurrency):
	// - Track observed task duration / error rate / heartbeat send latency,
	//   and adjust effective concurrency limits dynamically (within bounds).

	ctx    context.Context
	cancel context.CancelFunc
	logger utils.ILogger[WorkerPool]
}

func NewWorkerPool(id string, addr string, cfg WorkerPoolConfiguration) (*WorkerPool, error) {
	conn, err := grpc.NewClient(addr, grpc.WithTransportCredentials(insecure.NewCredentials()))
	if err != nil {
		return nil, err
	}

	return &WorkerPool{
		id:     id,
		cfg:    cfg,
		conn:   conn,
		client: pb.NewTaskDispatcherClient(conn),

		// TODO: Backpressure:
		// - Make queue sizes configurable, and add a policy when full (drop? block? NACK?).
		// - Export queue depth via logs/metrics for autoscaling decisions.
		urgentQ: make(chan *pb.TaskAssignment, 1024),
		normalQ: make(chan *pb.TaskAssignment, 1024),

		logger: utils.CreateLogger[WorkerPool](),
	}, nil
}

func (w *WorkerPool) Run(ctx context.Context) error {
	w.logger.LogInfo("Worker pool %s starting...", w.id)

	// TODO: Multi-orchestrator safe coordination:
	// - If multiple orchestrators exist, ensure worker connects to the correct one,
	//   and include identity/epoch so server can avoid duplicate dispatch across instances.

	md := metadata.New(map[string]string{
		"X-Worker-Pool-Id": w.id,
	})

	ctx, cancel := context.WithCancel(context.Background())
	w.ctx = metadata.NewOutgoingContext(ctx, md)
	w.cancel = cancel

	stream, err := w.client.Connect(w.ctx)
	if err != nil {
		w.logger.LogError("Worker pool %s connect failed: %v", w.id, err)
		cancel()
		return err
	}

	w.stream = stream
	w.reporter = NewCapacityReporter(stream, w.ctx)

	maxNormal := w.cfg.WorkerPoolCapacity - w.cfg.UrgentWorkersReserved
	maxUrgent := w.cfg.UrgentWorkersReserved

	// TODO: Worker auto-scaling hints (server tells worker to spawn / resize):
	// - Extend handshake so server can respond with desired MaxNormal/MaxUrgent (or a target concurrency),
	//   based on queue depth and cluster conditions.

	err = stream.Send(&pb.ClientMessage{
		Payload: &pb.ClientMessage_Ack{
			Ack: &pb.WorkerAck{
				MaxNormal: int32(maxNormal),
				MaxUrgent: int32(maxUrgent),
			},
		},
	})
	if err != nil {
		w.logger.LogError("Worker pool %s failed to send ACK: %v", w.id, err)
		return err
	}

	w.logger.LogInfo("WorkerPool %s connected; advertised MaxNormal=%d MaxUrgent=%d", w.id, maxNormal, maxUrgent)

	// TODO: Adaptive batching / dispatch cadence:
	// - Adjust flush frequency and queue stats interval based on activity (busy vs idle).

	go w.receiverLoop()
	go w.schedulerLoop()
	go w.capacityFlushLoop()
	go w.queueStatsLoop(1 * time.Second)

	w.reporter.AddInitial(w.cfg.WorkerPoolCapacity, w.cfg.WorkerPoolCapacity)

	<-w.ctx.Done()
	w.logger.LogInfo("Worker pool %s stopped (ctx done)", w.id)

	return nil
}

func (w *WorkerPool) receiverLoop() {
	for {
		msg, err := w.stream.Recv()
		if err != nil {
			w.logger.LogError("Worker pool %s stream recv error: %v", w.id, err)
			w.cancel()
			return
		}

		switch p := msg.Payload.(type) {
		case *pb.ServerMessage_Task:
			task := p.Task

			// TODO: Server-initiated cancellation protocol:
			// - Add a new server message type like Cancel{TaskId} and handle it here
			//   by cancelling the per-task context / marking task as cancelled.

			if task.Priority == pb.TaskPriority_TASK_PRIORITY_URGENT {
				w.urgentQ <- task
			} else {
				w.normalQ <- task
			}
		}
	}
}

func (w *WorkerPool) schedulerLoop() {
	// TODO: Weighted fair scheduler:
	// - Replace "always try urgent first" with weights (e.g., 80/20) or backlog-aware arbitration.
	// TODO: Backpressure from CPU/memory metrics:
	// - If local pressure is high, stop starting new tasks even if queue has items.

	for {
		select {
		case <-w.ctx.Done():
			return

		default:
			if w.tryStartUrgent() {
				continue
			}

			if w.tryStartNormal() {
				continue
			}

			time.Sleep(10 * time.Millisecond)
		}
	}
}

func (w *WorkerPool) capacityFlushLoop() {
	// TODO: Backpressure-safe send loop:
	// - If stream.Send becomes slow, this ticker goroutine can pile up work/latency.
	// - Consider routing capacity updates into a sender queue with a single writer goroutine.

	ticker := time.NewTicker(50 * time.Millisecond)
	defer ticker.Stop()

	for {
		select {
		case <-w.ctx.Done():
			return
		case <-ticker.C:
			if err := w.reporter.Flush(w.ctx); err != nil {
				w.logger.LogWarn("Worker pool %s capacity flush failed: %v", w.id, err)
			}
		}
	}
}

func (w *WorkerPool) queueStatsLoop(every time.Duration) {
	t := time.NewTicker(every)
	defer t.Stop()

	for {
		select {
		case <-w.ctx.Done():
			return
		case <-t.C:
			urgentLen := len(w.urgentQ)
			normalLen := len(w.normalQ)

			w.mu.RLock()
			active := w.active
			activeUrgent := w.activeUrgent
			w.mu.RUnlock()

			w.logger.LogInfo(
				"WorkerPool %s stats: active=%d urgentActive=%d qNormal=%d qUrgent=%d",
				w.id,
				active,
				activeUrgent,
				normalLen,
				urgentLen,
			)
		}
	}
}

func (w *WorkerPool) tryStartUrgent() bool {
	select {
	case task := <-w.urgentQ:
		if !w.hasUrgentSlot() {
			// put back if no slot
			go func() { w.urgentQ <- task }()
			return false
		}

		w.startTask(task, true)
		return true

	default:
		return false
	}
}

func (w *WorkerPool) tryStartNormal() bool {
	select {
	case task := <-w.normalQ:
		if !w.hasNormalSlot() {
			go func() { w.normalQ <- task }()
			return false
		}

		w.startTask(task, false)
		return true

	default:
		return false
	}
}

func (w *WorkerPool) hasUrgentSlot() bool {
	w.mu.RLock()
	defer w.mu.RUnlock()

	return w.active < w.cfg.WorkerPoolCapacity
}

func (w *WorkerPool) hasNormalSlot() bool {
	w.mu.RLock()
	defer w.mu.RUnlock()

	normalLimit := w.cfg.WorkerPoolCapacity - w.cfg.UrgentWorkersReserved

	return w.active < normalLimit
}

func (w *WorkerPool) startTask(
	task *pb.TaskAssignment,
	urgent bool,
) {
	// TODO: Lease auto-renew daemon:
	// - Use task.LeaseExpirationUnixMs to renew leases periodically while executing,
	//   instead of only sending heartbeats.
	// TODO: Cancellation propagation:
	// - Track in-flight tasks (TaskId -> cancel func) so receiverLoop can cancel them on demand.

	w.mu.Lock()
	w.active++
	if urgent {
		w.activeUrgent++
	}
	w.mu.Unlock()

	go func() {
		defer func() {
			w.mu.Lock()
			w.active--
			if urgent {
				w.activeUrgent--
			}
			w.mu.Unlock()

			if urgent {
				w.reporter.TaskFinished(pb.TaskPriority_TASK_PRIORITY_URGENT)
			} else {
				w.reporter.TaskFinished(pb.TaskPriority_TASK_PRIORITY_NORMAL)
			}
		}()

		ctx, cancel := context.WithCancel(w.ctx)
		defer cancel()

		go w.heartbeatLoop(task.TaskId, ctx)

		err := executeTask(task, ctx)

		if err != nil {
			w.logger.LogWarn("Worker pool %s task failed (taskId=%s urgent=%t): %v", w.id, task.TaskId, urgent, err)
			w.sendFailed(task.TaskId, err)
			return
		}

		w.logger.LogInfo("Worker pool %s task completed (taskId=%s urgent=%t)", w.id, task.TaskId, urgent)
		w.sendCompleted(task.TaskId)
	}()
}

func (w *WorkerPool) heartbeatLoop(taskId string, ctx context.Context) {
	t := time.NewTicker(w.cfg.HeartbeatInterval)
	defer t.Stop()

	for {
		select {
		case <-ctx.Done():
			return
		case <-t.C:
			if err := w.stream.Send(&pb.ClientMessage{
				Payload: &pb.ClientMessage_Heartbeat{
					Heartbeat: &pb.TaskHeartbeat{
						TaskId: taskId,
					},
				},
			}); err != nil {
				w.logger.LogWarn("Worker pool %s heartbeat send failed (taskId=%s): %v", w.id, taskId, err)
				return
			}
		}
	}
}

func (w *WorkerPool) sendCompleted(taskID string) {
	if err := w.stream.Send(&pb.ClientMessage{
		Payload: &pb.ClientMessage_Completed{
			Completed: &pb.TaskCompleted{
				TaskId: taskID,
			},
		},
	}); err != nil {
		w.logger.LogWarn("Worker pool %s failed to send Completed (taskId=%s): %v", w.id, taskID, err)
	}
}

func (w *WorkerPool) sendFailed(taskID string, err error) {
	if err2 := w.stream.Send(&pb.ClientMessage{
		Payload: &pb.ClientMessage_Failed{
			Failed: &pb.TaskFailed{
				TaskId:    taskID,
				Reason:    err.Error(),
				Retryable: true,
			},
		},
	}); err2 != nil {
		w.logger.LogWarn("Worker pool %s failed to send Failed (taskId=%s): %v (original=%v)", w.id, taskID, err2, err)
	}
}

func executeTask(task *pb.TaskAssignment, ctx context.Context) error {
	// Demo-only. This doesn't mimic real-world workloads.
	// Latency model: mostly "fast", sometimes "slow" (tail latency).
	var d time.Duration
	if rand.Float32() < 0.90 {
		// Fast path: 100–600ms
		d = time.Duration(100+rand.Intn(500)) * time.Millisecond
	} else {
		// Slow path: 1500–3500ms
		d = time.Duration(1500+rand.Intn(2000)) * time.Millisecond
	}

	timer := time.NewTimer(d)
	defer timer.Stop()

	select {
	case <-ctx.Done():
		return ctx.Err()
	case <-timer.C:
	}

	failRate := float32(0.05)
	if task.Priority == pb.TaskPriority_TASK_PRIORITY_URGENT {
		failRate = 0.02
	}

	if rand.Float32() < failRate {
		return simulatedError
	}

	return nil
}

var simulatedError = &simErr{}

type simErr struct{}

func (e *simErr) Error() string { return "simulated error" }
