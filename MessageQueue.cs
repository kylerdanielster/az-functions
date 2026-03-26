using Azure.Storage.Queues;

namespace AzFunctions;

/// <summary>
/// Abstraction over a message queue for testability.
/// Used by <see cref="BatchProcessor"/> to decouple from the concrete <see cref="QueueClient"/>.
/// </summary>
public interface IMessageQueue
{
    /// <summary>Sends a serialized message to the queue.</summary>
    Task SendMessageAsync(string message);
}

/// <summary>
/// Abstraction over the GL error queue for testability.
/// Used by <see cref="BatchOrchestration"/> to queue failed GL uploads for manual retry.
/// </summary>
public interface IGLErrorQueue
{
    /// <summary>Sends a serialized message to the GL error queue.</summary>
    Task SendMessageAsync(string message);
}

/// <summary>
/// Azure Storage Queue implementation of <see cref="IMessageQueue"/>.
/// Wraps <see cref="QueueClient"/> for the SFTP processing queue.
/// </summary>
public class StorageQueueClient(QueueClient queueClient) : IMessageQueue
{
    public async Task SendMessageAsync(string message)
    {
        await queueClient.SendMessageAsync(message);
    }
}

/// <summary>
/// Azure Storage Queue implementation of <see cref="IGLErrorQueue"/>.
/// Wraps <see cref="QueueClient"/> for the GL error queue.
/// </summary>
public class GLErrorQueueClient(QueueClient queueClient) : IGLErrorQueue
{
    public async Task SendMessageAsync(string message)
    {
        await queueClient.SendMessageAsync(message);
    }
}
