namespace Distributed_System_From_Scratch.Services
{
    public interface IDataStoreService
    {
        public string Get(int key);

        public void Set(int key, string value);

        public void Delete(int key);

        public void Update(int key, string value);  
    }
}
