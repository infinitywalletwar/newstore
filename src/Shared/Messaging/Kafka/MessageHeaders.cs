using System.Text;

using Confluent.Kafka;

namespace NewStore.Messaging.Kafka;

/// <summary>The Kafka header names the library writes and reads.</summary>
public static class MessageHeaders
{
    public const string MessageId = "message-id";
    public const string Type = "type";
    public const string OccurredAt = "occurred-at";
    public const string TraceParent = "traceparent";

    // Added to a dead-lettered message.
    public const string Error = "error";
    public const string OriginalTopic = "original-topic";
    public const string OriginalPartition = "original-partition";
    public const string OriginalOffset = "original-offset";

    internal static void Add(this Headers headers, string name, string value) =>
        headers.Add(name, Encoding.UTF8.GetBytes(value));

    internal static string? GetString(this Headers headers, string name) =>
        headers.TryGetLastBytes(name, out var bytes) ? Encoding.UTF8.GetString(bytes) : null;
}
