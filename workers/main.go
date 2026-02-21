package main

import (
	"context"
	"fmt"
	svc "main/services"
	utils "main/utilities"
	"os"
	"os/signal"
	"sync"
	"syscall"
)

// Just a type for the logger
type program struct {
}

func main() {
	logger := utils.CreateLogger[program]()
	logger.LogInfo("Starting...")

	config, ok := svc.NewConfig()
	if !ok {
		return
	}

	var workerPools []*svc.WorkerPool
	for i := 0; i < config.WorkerPoolsCount; i++ {
		worker, err := svc.NewWorkerPool(fmt.Sprintf("worker-%d", i+1), config.ServerEndpoint, config.WorkerPoolConfiguration)
		if err != nil {
			logger.LogFatal("Error creating worker: %v", err)
			return
		}

		workerPools = append(workerPools, worker)
	}

	wg := sync.WaitGroup{}

	// TODO: Graceful shutdown
	// - Use ctx, cancel := context.WithCancel(context.Background()) instead of context.Background()
	// - On SIGINT/SIGTERM: call cancel() so worker pools stop their loops (Recv/scheduler/flush/stats) promptly
	// - Wait for all workers to exit: wg.Wait()
	// - Consider adding a shutdown timeout (context.WithTimeout) so the app doesn't hang forever
	// - Optionally close gRPC connections/streams after cancellation (requires plumbing a Close()/Stop() on WorkerPool)

	ctx := context.Background()

	for _, worker := range workerPools {
		w := worker

		wg.Add(1)
		go func() {
			defer wg.Done()

			if err := w.Run(ctx); err != nil {
				logger.LogFatal("Could not run worker: %v", err)
			}
		}()
	}

	sigChan := make(chan os.Signal, 1)
	signal.Notify(sigChan, os.Interrupt, syscall.SIGTERM)
	<-sigChan

	logger.LogInfo("Shutting down...")
}
