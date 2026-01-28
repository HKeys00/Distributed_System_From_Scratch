namespace Distributed_System_From_Scratch.Services
{
    public interface IDataStoreService
    {
        string? Get(int key);

        void Set(int key, string value);

        void Delete(int key);

        void Update(int key, string value);  
    }
}
