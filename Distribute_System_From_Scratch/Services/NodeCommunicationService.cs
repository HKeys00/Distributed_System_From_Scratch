namespace Distributed_System_From_Scratch.Services
{
    /// <summary>
    /// Handles communication and discovery between nodes.
    /// </summary>
    public class NodeCommunicationService : INodeCommunicationService
    {
        #region Fields

        private readonly INodeInformationService _nodeInformationService;
        private readonly ILogger<NodeCommunicationService> _logger;

        #endregion

        #region Constructor

        public NodeCommunicationService(INodeInformationService nodeInformationService, ILogger<NodeCommunicationService> logger)
        {
            _nodeInformationService = nodeInformationService;
            _logger = logger;

            var peers = _nodeInformationService.GetPeers();
            Console.WriteLine("AHHHHH");
            _logger.LogCritical("peer");
            foreach (var peer in peers)
            {
                _logger.LogCritical(peer);
            }
            var m = 0;
        }

        #endregion

        public void SetKey(int key, string value)
        {
            throw new NotImplementedException();
        }
    }
}
