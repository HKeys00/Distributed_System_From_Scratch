using Shared.Models;

namespace Worker_Node.Services.Queue
{
    /// <summary>
    /// Defines the contract for services that manage ingress queues.
    /// </summary>
    public interface IIngressQueueService
    {
        /// <summary>
        /// Attempts to enqueue a task with the specified payload, task type, and execution profile asynchronously.
        /// </summary>
        /// <param name="payload">The dynamic payload to be enqueued.</param>
        /// <param name="taskType">The type of the task to enqueue.</param>
        /// <param name="executionType">The execution type associated with the task.</param>
        /// <param name="token">A cancellation token to observe while waiting for the task to be enqueued.</param>
        /// <returns>A task that represents the asynchronous operation. The task result contains true if the task was enqueued
        /// successfully; otherwise, false.</returns>
        Task<WorkItem?> TryEnqueueAsync(dynamic payload, string taskType, string executionType, CancellationToken token);

        /// <summary>
        /// Attempts to asynchronously dequeue a work item from the queue.
        /// </summary>
        /// <param name="token">A cancellation token to observe while waiting for a work item.</param>
        /// <returns>A task that represents the asynchronous operation. The result contains the dequeued WorkItem if available;
        /// otherwise, null.</returns>
        Task<WorkItem?> TryDequeueAsync(CancellationToken token);
    }
}
