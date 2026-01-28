namespace Distributed_System_From_Scratch.Services
{
    public class DataStoreService(INodeCommunicationService nodeCommunicationService) : IDataStoreService
    {
        #region Fields

        private readonly INodeCommunicationService _nodeCommunicationService = nodeCommunicationService;
        private readonly Dictionary<int, string> _dataStore = new();

        #endregion

        #region Methods

        public string? Get(int key)
        {
            _dataStore.TryGetValue(key, out var value);
            return value;
        }

        public void Set(int key, string value)
        {
            _dataStore[key] = value;
            _nodeCommunicationService.SetKey(key, value);
        }

        public void Delete(int key)
        {
            _dataStore.Remove(key);
        }

        public void Update(int key, string value)
        {
            _dataStore[key] = value;
        }

        #endregion
    }
}
