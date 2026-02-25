using Distributed_System_From_Scratch.Services;
using System.Timers;

namespace Distributed_System_From_Scratch.BackgroundWorkers
{
    public class HeartBeatHostedService : IHostedService
    {
        #region Fields

        private System.Timers.Timer _timer;
        private readonly INodeCommunicationService _nodeCommunicationService;
        private readonly ILogger<HeartBeatHostedService> _logger;

        #endregion

        #region Constructor

        public HeartBeatHostedService(INodeCommunicationService nodeCommunicationService, ILogger<HeartBeatHostedService> logger)
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
            //_nodeCommunicationService.PingPeers();
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _timer.Dispose();
        }

        #endregion
    }
}
