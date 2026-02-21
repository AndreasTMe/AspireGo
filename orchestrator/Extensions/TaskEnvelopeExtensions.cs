using Google.Protobuf;
using Orchestrator.Dispatchers;
using Orchestrator.Models;
using System;

namespace Orchestrator.Extensions;

internal static class TaskEnvelopeExtensions
{
    extension(TaskEnvelope envelope)
    {
        public ServerMessage ToServerMessage() =>
            new()
            {
                Task = new TaskAssignment
                {
                    TaskId  = envelope.TaskId,
                    Type    = TaskType.Unspecified,
                    Payload = ByteString.CopyFrom(envelope.Payload),
                    Priority = envelope.Priority == Models.TaskPriority.Urgent
                        ? Dispatchers.TaskPriority.Urgent
                        : Dispatchers.TaskPriority.Normal,
                    LeaseExpirationUnixMs = new DateTimeOffset(envelope.LeaseExpiresAt).ToUnixTimeMilliseconds()
                }
            };
    }
}