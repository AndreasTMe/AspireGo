package services

import (
	"context"
	"sync"

	pb "main/.gen/proto"
)

type CapacityReporter struct {
	mu sync.Mutex

	normalFreed int
	urgentFreed int

	stream pb.TaskDispatcher_ConnectClient
	ctx    context.Context
}

func NewCapacityReporter(
	stream pb.TaskDispatcher_ConnectClient,
	ctx context.Context,
) *CapacityReporter {
	return &CapacityReporter{
		stream: stream,
		ctx:    ctx,
	}
}

func (c *CapacityReporter) TaskFinished(priority pb.TaskPriority) {
	c.mu.Lock()

	if priority == pb.TaskPriority_TASK_PRIORITY_URGENT {
		c.urgentFreed++
	} else {
		c.normalFreed++
	}

	n := c.normalFreed
	u := c.urgentFreed

	c.mu.Unlock()

	// TODO: Delta credits protocol:
	// - Keep this as a "delta accumulator", but add protection against "send failed" losing credits:
	//   if Flush fails, re-add (n,u) back into counters (or keep a pending buffer).
	// TODO: Zero-allocation / high-throughput reporter:
	// - Replace mutex with atomic counters and batch/swap pattern (esp. if task completion is hot path).
	// TODO: Adaptive batching:
	// - Make this threshold dynamic (based on observed Send latency, queue depth, or worker capacity).

	// Opportunistic flush (threshold = concurrency / 4)
	if n+u >= 4 {
		_ = c.Flush(c.ctx)
	}
}

func (c *CapacityReporter) AddInitial(normal, urgent int) {
	c.mu.Lock()
	c.normalFreed += normal
	c.urgentFreed += urgent
	c.mu.Unlock()

	// TODO: Capacity semantics:
	// - Consider whether initial credits should reflect "max concurrency" per lane (normal/urgent)
	//   rather than mirroring worker capacity twice.
}

func (c *CapacityReporter) Flush(ctx context.Context) error {
	// TODO: Backpressure-safe send loop:
	// - Today Flush calls stream.Send directly (can block).
	// - Consider a single dedicated sender goroutine with a buffered channel to serialize sends and apply backpressure.
	// TODO: Cancellation propagation:
	// - If ctx is done, stop attempting to send and let upstream close cleanly.

	select {
	case <-ctx.Done():
		return ctx.Err()
	default:
	}

	c.mu.Lock()

	n := c.normalFreed
	u := c.urgentFreed

	c.normalFreed = 0
	c.urgentFreed = 0

	c.mu.Unlock()

	if n == 0 && u == 0 {
		return nil
	}

	return c.stream.Send(&pb.ClientMessage{
		Payload: &pb.ClientMessage_Capacity{
			Capacity: &pb.CapacityUpdate{
				NormalCredit: int32(n),
				UrgentCredit: int32(u),
			},
		},
	})
}
