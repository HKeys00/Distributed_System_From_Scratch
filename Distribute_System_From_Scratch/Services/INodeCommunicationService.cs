namespace Distributed_System_From_Scratch.Services
{
    public interface INodeCommunicationService
    {
        Task PingPeers();

        /// <summary>
        /// Sends an x number of requests to the /operations/cpu endpoint for each node in peers.
        /// </summary>
        /// <param name="count">The number of request to send.</param>
        Task SendCPUBoundTask(int count);

        /// <summary>
        /// Sends an x number of requests to the /operations/io endpoint for each node in peers.
        /// </summary>
        /// <param name="count">The number of request to send.</param>
        Task SendIOBoundTask(int count);
    }
}
