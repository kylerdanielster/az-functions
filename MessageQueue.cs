using Azure.Storage.Queues;

namespace AzFunctions;

/// <summary>
/// Abstraction over a message queue for testability.
/// Used by <see cref="SftpProcessor"/> to decouple from the concrete <see cref="QueueClient"/>.
/// </summary>
public interface IMessageQueue
{
    /// <summary>Sends a serialized message to the queue.</summary>
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
