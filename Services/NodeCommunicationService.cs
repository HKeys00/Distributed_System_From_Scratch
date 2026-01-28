namespace Distributed_System_From_Scratch.Services
{
    /// <summary>
    /// Handles communication and discovery between nodes.
    /// </summary>
    public class NodeCommunicationService : INodeCommunicationService
    {
        #region Fields

        private readonly INodeInformationService _nodeInformationService;

        #endregion

        #region Constructor

        public NodeCommunicationService(INodeInformationService nodeInformationService)
        {
            _nodeInformationService = nodeInformationService;
            var peers = _nodeInformationService.GetPeers();
            foreach (var peer in peers)
            {
                Console.WriteLine(peer);
            }
            var m = 0;
        }

        #endregion

        public void SetKey(int key, string value)
        {
            throw new NotImplementedException();
        }
    }
}
