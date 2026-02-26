using System.Net;
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
            foreach (var peer in _nodeInformationService.Peers)
            {
                _table.Add(peer, new HealthStatus() { Node =  peer, Incarnation = 0});
            }
        }

        #endregion

        public async Task PingPeers()
        {
            using var client = _httpClientFactory.CreateClient();

            foreach (var peer in _nodeInformationService.Peers)
            {
                var url = $"{peer}/heartbeat";
                try
                {
                    var response = await client.GetAsync(url);
                    if (response.IsSuccessStatusCode)
                    {
                        var ticksString = await response.Content.ReadAsStringAsync();
                        var incarnation = Convert.ToInt64(ticksString);

                        if (_table[peer].Incarnation > incarnation)
                        {
                            // Discard message from old incarnation.
                            return;
                        }

                        _table[peer].Incarnation = incarnation;
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

                    //_logger.LogWarning($"{peer}, {_table[peer].Incarnation} with status {_table[peer].Status.ToString()} last seen at {_table[peer].LastSeen.ToString()}");
                }
            }
        }

        public async Task SendCPUBoundTask(int count)
        {
            //_logger.LogWarning("Firing CPU Bound volley, count: {count}", count);
            using var client = _httpClientFactory.CreateClient();
            var random = new Random();
            var requests = new List<Task<HttpResponseMessage>>();
            foreach (var peer in _nodeInformationService.Peers)
            {
                var url = $"{peer}/operations/cpu";
                for (int i = 0; i < count; i++)
                {
                    requests.Add(client.PostAsJsonAsync(url, random.Next(100_000)));
                }
            }
        }

        public async Task SendIOBoundTask(int count)
        {
            //_logger.LogWarning("Firing I/O Bound volley, count: {count}", count);
            using var client = _httpClientFactory.CreateClient();
            var tasks = new Task[(count * _nodeInformationService.Peers.Length)];
            for (int i = 0; i < _nodeInformationService.Peers.Length; i++)
            {
                var peer = _nodeInformationService.Peers[i];
                var url = $"{peer}/operations/io";
                for (int j = 0; j < count; j++)
                {
                    tasks[i * j] = client.PostAsync(url, null);
                }
            }

            await Task.WhenAll(tasks);
        }
    }
}
