using Distributed_System_From_Scratch.Services;
using System.Timers;

namespace Distributed_System_From_Scratch.BackgroundWorkers
{
    /// <summary>
    /// Hosted service for sending concurrent requests to other peer nodes.
    /// </summary>
    public class OperationsHostedService : IHostedService
    {
        #region Fields

        private System.Timers.Timer _timer;
        private readonly INodeCommunicationService _nodeCommunicationService;
        private readonly ILogger<OperationsHostedService> _logger;
        private int _count;

        #endregion

        #region Constructor

        public OperationsHostedService(INodeCommunicationService nodeCommunicationService, ILogger<OperationsHostedService> logger)
        {
            _timer = new System.Timers.Timer();
            _nodeCommunicationService = nodeCommunicationService;
            _logger = logger;
        }

        #endregion

        #region Methods

        public Task StartAsync(CancellationToken token)
        {
            _timer = new System.Timers.Timer(5000);
            _timer.Elapsed += DoWork;
            _timer.AutoReset = true;
            _timer.Enabled = true;

            return Task.CompletedTask;
        }

        public void DoWork(Object? source, ElapsedEventArgs e)
        {
            //_nodeCommunicationService.SendCPUBoundTask(_count);
            _nodeCommunicationService.SendIOBoundTask(_count);

            _count++;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _timer.Dispose();
        }

        #endregion
    }
}
