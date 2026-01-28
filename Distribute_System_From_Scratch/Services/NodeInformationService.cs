namespace Distributed_System_From_Scratch.Services
{
    public class NodeInformationService(IConfiguration configuration) : INodeInformationService
    {
        #region Fields

        private readonly char _nodeId = configuration.GetValue<char>("NODE_ID");
        private readonly int _port = configuration.GetValue<int>("HTTP_PORT");
        private readonly string _dataDir = configuration.GetValue<string>("DATA_DIR") ?? string.Empty;
        private readonly string[] _peers = configuration.GetValue<string>("PEERS")?.Split(",") ?? [];

        #endregion

        #region Methods

        public char GetNodeId()
        {
            return _nodeId;
        }

        public int GetPortNumber()
        {
            return _port;
        }

        public string GetDataDirectory()
        {
            return _dataDir;
        }

        public string[] GetPeers()
        {
            return _peers;
        }

        #endregion
    }
}
