using System;

namespace Orchestrator.Models;

public enum TaskPriority
{
    Normal = 0,
    Urgent = 1
}

public sealed class TaskEnvelope
{
    // public string? TenantId { get; init; }

    public required string       TaskId         { get; init; }
    public required string       Type           { get; init; }
    public required byte[]       Payload        { get; init; }
    public required TaskPriority Priority       { get; init; }
    public required DateTime     LeaseExpiresAt { get; init; }
}