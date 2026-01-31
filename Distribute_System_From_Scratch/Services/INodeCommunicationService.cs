namespace Distributed_System_From_Scratch.Services
{
    public interface INodeCommunicationService
    {
        Task PingPeers(CancellationToken token);

        void SetKey(int key, string value);
    }
}
