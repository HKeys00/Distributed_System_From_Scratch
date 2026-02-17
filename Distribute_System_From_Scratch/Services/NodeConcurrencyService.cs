using System.Collections.Concurrent;

namespace Distributed_System_From_Scratch.Services
{
    public class NodeConcurrencyService : INodeConcurrencyService
    {
        #region Constants

        public int MAX_CONCURRENT_OPERATIONS = 8;
        public int MAX_QUEUE_LENGTH = 100;

        #endregion

        #region Fields

        private ConcurrentQueue<int> _queue;
        private int _numExecutions;

        #endregion

        #region Constructor

        NodeConcurrencyService()
        {
            _queue = new ConcurrentQueue<int> ();
            _numExecutions = 0;
        }

        #endregion

        #region Methods

        /// <inheritdoc />
        public bool CanRunOperation() => Volatile.Read(ref _numExecutions) < MAX_CONCURRENT_OPERATIONS;

        /// <inheritdoc />
        public void EnqueueTask(int task)
        {
            if (Volatile.Read(ref _numExecutions) < MAX_CONCURRENT_OPERATIONS)
            {

            }
            throw new NotImplementedException();
        }

        
        #endregion
    }
}
