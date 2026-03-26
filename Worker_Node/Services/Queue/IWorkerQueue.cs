using Shared.Models;

namespace Worker_Node.Services.Queue
{
    /// <summary>
    /// Defines an interface for asynchronously enqueuing and dequeuing work items.
    /// </summary>
    public interface IWorkerQueue
    {
        /// <summary>
        /// Attempts to asynchronously enqueue the specified work item.
        /// </summary>
        /// <param name="item">The work item to enqueue.</param>
        /// <param name="token">A cancellation token to observe while waiting for the task to be enqueued.</param>
        /// <returns>A task that represents the asynchronous operation. The result is true if the item was enqueued; otherwise,
        /// false.</returns>
        Task<bool> TryEnqueueAsync(WorkItem item, CancellationToken token);

        /// <summary>
        /// Attempts to asynchronously dequeue a work item from the queue.
        /// </summary>
        /// <param name="token">A cancellation token to observe while waiting for the task to be enqueued.</param>
        /// <returns>A task that represents the asynchronous operation, containing the dequeued WorkItem or null if the queue is
        /// empty.</returns>
        Task<WorkItem> TryDequeueAsync(CancellationToken token);
    }
}
