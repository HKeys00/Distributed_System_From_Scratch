using Distributed_System_From_Scratch.Services;

namespace Distributed_System_From_Scratch.BackgroundWorkers
{
    public class HeartBeatHostedService : IHostedService
    {
        #region Fields

        private readonly INodeCommunicationService _nodeCommunicationService;

        #endregion

        #region Constructor

        public HeartBeatHostedService(INodeCommunicationService nodeCommunicationService)
        {
            _nodeCommunicationService = nodeCommunicationService;
        }

        #endregion

        #region Methods

        public async Task StartAsync(CancellationToken token)
        {
            await _nodeCommunicationService.PingPeers(token);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            //throw new NotImplementedException();
        }

        #endregion

    }
}
