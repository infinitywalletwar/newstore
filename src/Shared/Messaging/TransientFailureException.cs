namespace NewStore.Messaging;

/// <summary>Thrown by a handler when a dependency is unavailable and the message should be tried again later.</summary>
public class TransientFailureException(string message, Exception? innerException = null) : Exception(message, innerException);
