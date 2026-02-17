using System.Collections.Concurrent;

namespace Distributed_System_From_Scratch.Services
{
    /// <summary>
    /// Service that will throttle the number of incoming CPU bound requests.
    /// </summary>
    public interface INodeConcurrencyService
    {
        #region Methods

        /// <summary>
        /// Enqueues a task to the queue.
        /// </summary>
        /// <param name="task">The cpu bound task.</param>
        void EnqueueTask(int task);

        /// <summary>
        /// Checks to see if the operation can be ran.
        /// </summary>
        bool CanRunOperation();
        #endregion
    }
}
