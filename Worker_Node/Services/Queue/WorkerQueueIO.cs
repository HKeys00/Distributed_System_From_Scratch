using Shared.Models;

namespace Worker_Node.Services.Queue
{
    /// <summary>
    /// Represents a worker queue implementation for input/output operations.
    /// </summary>
    public class WorkerQueueIo : IWorkerQueue
    {
        #region Methods

        /// <inheritdoc />
        public async Task<bool> TryEnqueueAsync(WorkItem item, CancellationToken token)
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc />
        public async Task<WorkItem> TryDequeueAsync(CancellationToken token)
        {
            throw new NotImplementedException();
        }

        #endregion
    }
}
