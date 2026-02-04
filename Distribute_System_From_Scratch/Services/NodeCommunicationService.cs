using Distributed_System_From_Scratch.Data;

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

        private readonly Dictionary<string, HealthStatus> _table;
        #endregion

        #region Constructor

        public NodeCommunicationService(INodeInformationService nodeInformationService, IHttpClientFactory httpClientFactory, ILogger<NodeCommunicationService> logger)
        {
            _nodeInformationService = nodeInformationService;
            _httpClientFactory = httpClientFactory;
            _logger = logger;


            _table = new Dictionary<string, HealthStatus>();
            var peers = _nodeInformationService.GetPeers();
            foreach (var peer in peers)
            {
                _table.Add(peer, new HealthStatus() { Node =  peer });
            }
        }

        #endregion

        public async Task PingPeers()
        {
            var peers = _nodeInformationService.GetPeers();
            using var client = _httpClientFactory.CreateClient();

            foreach (var peer in peers)
            {
                var url = $"{peer}/heartbeat";
                try
                {
                    var response = await client.GetAsync(url);
                    if (response.IsSuccessStatusCode)
                    {
                        _table[peer].LastSeen = DateTime.UtcNow;
                    }
                }
                catch
                {
                     // Handle missing node.
                }
                finally
                {
                    if (_table[peer].LastSeen > DateTime.UtcNow.AddSeconds(-20))
                    {
                        _table[peer].Status = Enums.NodeStatus.Alive;
                    } else if (_table[peer].LastSeen > DateTime.UtcNow.AddMinutes(-1))
                    {
                        _table[peer].Status = Enums.NodeStatus.Suspect;
                    } else
                    {
                        _table[peer].Status = Enums.NodeStatus.Dead;
                    }

                    _logger.LogWarning($"{peer} with status {_table[peer].Status.ToString()} last seen at {_table[peer].LastSeen.ToString()}");
                }
            }
        }
    }
}
