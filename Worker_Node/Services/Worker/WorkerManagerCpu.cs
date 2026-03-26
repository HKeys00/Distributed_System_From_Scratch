using Microsoft.Extensions.ObjectPool;
using Worker_Node.Services.Queue;

namespace Worker_Node.Services.Worker
{
    /// <summary>
    /// Manages the lifecycle of a CPU-based worker as a hosted service.
    /// </summary>
    public class WorkerManagerCpu : IHostedService
    {
        #region Constants

        private const int MaxConcurrentWorkers = 5;

        #endregion

        #region Fields

        private readonly WorkerQueueCpu _workerQueue;
        private ObjectPool<WorkerCpu> _workerPool;
        private int _numConcurrentWorkers = 0;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the <see cref="WorkerManagerCpu"/> class.
        /// </summary>
        /// <param name="workerQueue">The injected worker queue.</param>
        public WorkerManagerCpu(WorkerQueueCpu workerQueue)
        {
            _workerPool = new DefaultObjectPool<WorkerCpu>(new DefaultPooledObjectPolicy<WorkerCpu>(), MaxConcurrentWorkers);
            _workerQueue = workerQueue;
        }

        #endregion

        #region Methods

        /// <inheritdoc />
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {

                var worker = _workerPool.Get();

                
            }
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            
        }

        #endregion
    }
}
