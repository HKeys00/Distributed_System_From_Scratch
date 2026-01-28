namespace Distributed_System_From_Scratch.Services
{
    /// <summary>
    /// Service for storing and fetching information about the
    /// node container.
    /// </summary>
    public interface INodeInformationService
    {
        char GetNodeId();

        int GetPortNumber();

        string GetDataDirectory();

        string[] GetPeers();
    }
}
