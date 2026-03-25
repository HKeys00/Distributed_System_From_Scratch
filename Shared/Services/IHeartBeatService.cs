namespace Shared.Services
{
    /// <summary>
    /// Background service for sending heartbeat messages to peers.
    /// </summary>
    public interface IHeartBeatService
    {
        /// <summary>
        /// Sends a heartbeat to a list of peers.
        /// </summary>
        /// <param name="peers">The list of peers to ping.</param>
        Task SendHeartBeat(string[] peers);
    }
}