using Shared.Constants;
using Worker_Node.Services.Queue;

namespace Worker_Node.Services
{
    public class DispatcherService : IHostedService
    {
        #region Fields

        private readonly IIngressQueueService _ingressQueueService;
        private readonly WorkerQueueCpu _queueCpu;
        private readonly WorkerQueueIo _queueIo;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the DispatcherService class with the specified ingress queue service and
        /// worker queues.
        /// </summary>
        /// <param name="ingressQueueService">The ingress queue service to be used by the dispatcher.</param>
        /// <param name="queueCpu">The CPU-bound worker queue.</param>
        /// <param name="queueIo">The IO-bound worker queue.</param>
        public DispatcherService(IIngressQueueService ingressQueueService, WorkerQueueCpu queueCpu, WorkerQueueIo queueIo)
        {
            _ingressQueueService = ingressQueueService;
            _queueCpu = queueCpu;
            _queueIo = queueIo;
        }

        #endregion

        /// <inheritdoc />
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var item = await _ingressQueueService.TryDequeueAsync(cancellationToken);
                if (item == null)
                {
                    //Log null item found when dequeuing 
                    continue;
                }

                switch (item.ExecutionType)
                {
                    case ExecutionType.CPU:
                    {
                        var result = await _queueCpu.TryEnqueueAsync(item, cancellationToken);
                        if (!result)
                        {
                            //Log item discarded.
                        }

                        break;
                    }

                    case ExecutionType.IO:
                    {
                        var result = await _queueIo.TryEnqueueAsync(item, cancellationToken);
                        if (!result)
                        {
                            //Log item discarded.
                        }

                        break;
                    }

                    default:
                        //Log unknown execution type found.
                        throw new Exception();
                }
            }
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }
}
