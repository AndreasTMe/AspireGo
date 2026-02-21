# Aspire + Go

A small **.NET Aspire** distributed application that demonstrates a **task orchestration + worker pool** pattern using
**gRPC streaming**, with:

- an **Orchestrator** service (ASP.NET Core + gRPC) that leases and dispatches work
- a **Go-based Workers** app that connects via gRPC, executes tasks, sends heartbeats, and reports capacity back
- an **Aspire AppHost** that wires everything together and runs the system locally

The solution targets **.NET 10** and mixes **C#** (server and host) with **Go** (workers).

![Running the app](.content/run.gif)

---

## High-level architecture

### Flow

1. Workers connect to the orchestrator via a **bi-directional gRPC stream**.
2. Workers send:
    - an initial **ACK/handshake** (max concurrency per lane)
    - periodic **capacity updates** (credits freed by completed tasks)
    - **heartbeat** messages while tasks are running
    - **completed/failed** notifications when tasks finish
3. The orchestrator:
    - tracks available worker credits (normal/urgent)
    - **leases tasks** from a repository in batches (bounded by credits)
    - dispatches tasks to workers over the stream
    - marks tasks **completed/failed**, and extends leases on heartbeats

### Protocol

The message contract is defined in **`.proto/dispatchers.proto`**:

---

## Repository layout

```plain text
aspire-go/
  .proto/                   # gRPC protocol definitions (protobuf)
  .scripts/                 # build/run automation scripts
  aspire-host/              # .NET Aspire AppHost (runs the distributed app)
  orchestrator/             # ASP.NET Core gRPC server (dispatch + leasing)
  workers/                  # Go worker pools + a .NET csproj wrapper to build Go
```

---

## Projects

### `aspire-host/` (Aspire AppHost)

**Purpose:** Local orchestration for the distributed application.

- Starts the **Orchestrator** project.
- Starts the **Go workers**.
- Injects `ORCHESTRATOR_ENDPOINT` into the Go app based on the orchestrator’s HTTP endpoint.
- Configures worker behavior via environment variables:
    - `WORKER_POOLS_COUNT`
    - `WORKER_POOL_CAPACITY`
    - `WORKER_POOL_URGENT_PERCENTAGE`
    - `WORKER_HEARTBEAT_SECONDS`

---

### `orchestrator/` (ASP.NET Core gRPC service)

**Purpose:** Dispatches tasks to workers based on available capacity credits.

Notable components:

- **gRPC service:** `TaskDispatcherService`
    - manages the streaming connection
    - receives capacity/completion/heartbeat messages
    - dispatches leased tasks back to workers
- **In-memory store (demo):** `DummyInMemoryStore`
    - publishes tasks (from a background dummy producer)
    - leases tasks with a time-based lease and basic retry/backoff
- **Signals/coordination:** `DispatcherSignals`
    - in-memory channels/dictionaries for “state changed” and “credits changed” notifications

Notes:

- The current implementation is intentionally **single-instance / in-memory**, with TODOs for durability and
  multi-orchestrator coordination.

---

### `workers/` (Go worker pools)

**Purpose:** Runs one or more worker pools that execute tasks and report capacity back.

Key bits:

- `main.go` creates N worker pools (`WORKER_POOLS_COUNT`) and runs them concurrently.
- `services/worker_pool.go`
    - connects to orchestrator with `X-Worker-Pool-Id` metadata
    - maintains **urgent** and **normal** queues
    - enforces concurrency limits and lane reservation (urgent reserved %)
    - sends task heartbeats and completion/failure messages
- `services/capacity_reporter.go`
    - accumulates “freed credits” and flushes them as `CapacityUpdate` deltas
- `services/configuration.go`
    - reads environment variables and computes effective worker pool configuration

Also included:

- `Workers.csproj`: a .NET project wrapper that builds the Go binary as part of the .NET build.

---

## Scripts

- `build-proto.ps1`  
  Generates Go gRPC stubs from `.proto` using `protoc` (outputs into `workers/.gen`).
- `build-all.ps1`  
  Builds proto (unless skipped) + builds orchestrator + apphost.
- `run.ps1`  
  Builds everything and runs the app via `aspire run --project aspire-host/AspireHost.csproj`.

---

## What this solution is (and isn’t)

- ✅ A runnable demo of **credit-based dispatch**, **priority lanes (normal/urgent)**, and **lease/heartbeat** mechanics
  over **gRPC streaming**.
- ✅ A good starting point for adding durable storage, metrics/tracing, auth, and multi-instance orchestration.
- ❌ Not yet a production-ready scheduler (many TODOs point at durability, distributed coordination, backpressure, and
  fairness scheduling improvements).