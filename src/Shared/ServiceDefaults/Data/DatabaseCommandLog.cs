using Microsoft.Extensions.Logging;

namespace NewStore.ServiceDefaults.Data;

/// <summary>Log messages of the database commands, generated at build time (analyzer CA1848).</summary>
internal static partial class DatabaseCommandLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Applying pending migrations of {Context}.")]
    public static partial void Migrating(ILogger logger, string context);

    [LoggerMessage(Level = LogLevel.Information, Message = "Seeding {Context}.")]
    public static partial void Seeding(ILogger logger, string context);

    [LoggerMessage(Level = LogLevel.Information, Message = "No seeder is registered for {Context}; nothing to seed.")]
    public static partial void NoSeeder(ILogger logger, string context);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unknown command '{Command}'. Known commands: migrate, seed.")]
    public static partial void UnknownCommand(ILogger logger, string command);
}
