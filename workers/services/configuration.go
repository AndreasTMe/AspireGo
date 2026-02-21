package services

import (
	"os"
	"strconv"
	"time"

	utils "main/utilities"
)

const DefaultWorkerPoolsCount = 1
const DefaultWorkerPoolCapacity = 4
const DefaultWorkerPoolUrgentPercentage = 25
const DefaultWorkerHeartbeatSeconds = 1

type Configuration struct {
	ServerEndpoint          string
	WorkerPoolsCount        int
	WorkerPoolConfiguration WorkerPoolConfiguration
}

type WorkerPoolConfiguration struct {
	WorkerPoolCapacity    int
	UrgentWorkersReserved int
	HeartbeatInterval     time.Duration

	// TODO: Adaptive capacity / autoscaling knobs:
	// - Add min/max concurrency, target latency, max in-flight, queue size, etc.
	// TODO: Backpressure tuning:
	// - Add config for send buffer sizes, flush interval, and queue stats logging interval.
}

func NewConfig() (Configuration, bool) {
	logger := utils.CreateLogger[WorkerPoolConfiguration]()

	// TODO: Multi-orchestrator coordination:
	// - Support multiple endpoints with failover / load balancing.

	orchestratorEndpoint := os.Getenv("ORCHESTRATOR_ENDPOINT")
	if utils.IsEmptyOrWhitespace(orchestratorEndpoint) {
		logger.LogError("Invalid endpoint: %s", orchestratorEndpoint)
		return Configuration{}, false
	}

	workerPoolsCount, err := strconv.Atoi(os.Getenv("WORKER_POOLS_COUNT"))
	if err != nil {
		logger.LogError("Invalid worker pools count, default to %d - Error: %v", DefaultWorkerPoolsCount, err)
		workerPoolsCount = DefaultWorkerPoolsCount
	}
	if workerPoolsCount <= 0 {
		logger.LogError("Invalid worker pools count: %d - Default to %d", workerPoolsCount, DefaultWorkerPoolsCount)
		workerPoolsCount = DefaultWorkerPoolsCount
	}

	workerPoolCapacity, err := strconv.Atoi(os.Getenv("WORKER_POOL_CAPACITY"))
	if err != nil {
		logger.LogError("Invalid worker pool capacity, default to %d - Error: %v", DefaultWorkerPoolCapacity, err)
		workerPoolCapacity = DefaultWorkerPoolCapacity
	}
	if workerPoolCapacity <= 0 {
		logger.LogError("Invalid worker pool capacity: %d - Default to %d", workerPoolCapacity, DefaultWorkerPoolCapacity)
		workerPoolCapacity = DefaultWorkerPoolCapacity
	}

	workerPoolUrgentPercentage, err := strconv.Atoi(os.Getenv("WORKER_POOL_URGENT_PERCENTAGE"))
	if err != nil {
		logger.LogError("Invalid worker pool urgent percentage, default to %d - Error: %v", DefaultWorkerPoolUrgentPercentage, err)
		workerPoolUrgentPercentage = DefaultWorkerPoolUrgentPercentage
	}
	if workerPoolUrgentPercentage <= 0 || workerPoolUrgentPercentage > 100 {
		logger.LogError("Invalid worker pool urgent percentage: %d - Default to %d", workerPoolUrgentPercentage, DefaultWorkerPoolUrgentPercentage)
		workerPoolUrgentPercentage = DefaultWorkerPoolUrgentPercentage
	}

	heartbeatSeconds, err := strconv.Atoi(os.Getenv("WORKER_HEARTBEAT_SECONDS"))
	if err != nil {
		logger.LogError("Invalid worker heartbeat seconds, default to %d - Error: %v", DefaultWorkerHeartbeatSeconds, err)
		heartbeatSeconds = DefaultWorkerHeartbeatSeconds
	}
	if heartbeatSeconds <= 0 {
		logger.LogError("Invalid worker heartbeat seconds: %d - Default to %d", heartbeatSeconds, DefaultWorkerHeartbeatSeconds)
		heartbeatSeconds = DefaultWorkerHeartbeatSeconds
	}

	return Configuration{
		ServerEndpoint:   orchestratorEndpoint,
		WorkerPoolsCount: workerPoolsCount,
		WorkerPoolConfiguration: WorkerPoolConfiguration{
			WorkerPoolCapacity:    workerPoolCapacity,
			UrgentWorkersReserved: workerPoolCapacity * workerPoolUrgentPercentage / 100,
			HeartbeatInterval:     time.Duration(heartbeatSeconds) * time.Second,
		},
	}, true
}
