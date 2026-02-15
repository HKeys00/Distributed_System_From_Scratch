namespace Distributed_System_From_Scratch.Services
{
    /// <summary>
    /// Service for storing and fetching information about the
    /// node container.
    /// </summary>
    public interface INodeInformationService
    {
        char NodeId { get; }

        public int Port { get; }

        public string DataDir { get; }

        public string[] Peers { get; }

        public DateTime IncarnationNumber { get; }
    }
}
