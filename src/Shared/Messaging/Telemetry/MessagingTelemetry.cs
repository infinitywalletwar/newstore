using System.Diagnostics;

namespace NewStore.Messaging.Telemetry;

/// <summary>The library's <see cref="ActivitySource"/>. The name starts with <c>NewStore.</c>, so <c>AddServiceDefaults</c> collects it.</summary>
internal static class MessagingTelemetry
{
    public const string SourceName = "NewStore.Messaging";

    public static readonly ActivitySource Source = new(SourceName);
}
