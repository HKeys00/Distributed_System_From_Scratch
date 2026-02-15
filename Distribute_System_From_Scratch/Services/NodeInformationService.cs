namespace Distributed_System_From_Scratch.Services
{
    public class NodeInformationService(IConfiguration configuration) : INodeInformationService
    {
        #region Properties

        public char NodeId { get; } = configuration.GetValue<char>("NODE_ID");

        public int Port { get; } = configuration.GetValue<int>("HTTP_PORT");

        public string DataDir { get; } = configuration.GetValue<string>("DATA_DIR") ?? string.Empty;

        public string[] Peers { get; } = configuration.GetValue<string>("PEERS")?.Split(",") ?? [];

        public DateTime IncarnationNumber { get; } = DateTime.UtcNow;

        #endregion
    }
}
