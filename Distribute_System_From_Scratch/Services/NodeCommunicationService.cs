namespace Distributed_System_From_Scratch.Services
{
    /// <summary>
    /// Handles communication and discovery between nodes.
    /// </summary>
    public class NodeCommunicationService : INodeCommunicationService
    {
        #region Fields

        private readonly INodeInformationService _nodeInformationService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<NodeCommunicationService> _logger;

        #endregion

        #region Constructor

        public NodeCommunicationService(INodeInformationService nodeInformationService, IHttpClientFactory httpClientFactory, ILogger<NodeCommunicationService> logger)
        {
            _nodeInformationService = nodeInformationService;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        #endregion

        public async Task PingPeers()
        {
            var peers = _nodeInformationService.GetPeers();
            var node = _nodeInformationService.GetNodeId();
            using var client = _httpClientFactory.CreateClient();

            foreach (var peer in peers)
            {
                var url = $"{peer}/heartbeat";
                var response = await client.PostAsync(url, null);
            }
        }

        public void SetKey(int key, string value)
        {
            throw new NotImplementedException();
        }
    }
}
