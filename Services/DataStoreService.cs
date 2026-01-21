namespace Distributed_System_From_Scratch.Services
{
    public class DataStoreService : IDataStoreService
    {
        #region Fields

        private readonly Dictionary<int, string> _dataStore = new();

        #endregion

        #region Methods

        public string Get(int key)
        {
            return _dataStore[key];
        }

        public void Set(int key, string value)
        {
            _dataStore[key] = value;
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
