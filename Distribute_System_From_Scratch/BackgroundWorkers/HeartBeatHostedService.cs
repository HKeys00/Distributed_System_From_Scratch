using Distributed_System_From_Scratch.Services;

namespace Distributed_System_From_Scratch.BackgroundWorkers
{
    public class HeartBeatHostedService : IHostedService
    {
        #region Fields

        private readonly NodeCommunicationService _nodeCommunicationService;

        #endregion

        #region Constructor

        public HeartBeatHostedService(NodeCommunicationService nodeCommunicationService)
        {
            _nodeCommunicationService = nodeCommunicationService;
        }

        #endregion

        #region Methods

        public async Task StartAsync(CancellationToken token)
        {
            _nodeCommunicationService.
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        #endregion

    }
}
