using System.Threading.Channels;
using Shared.Models;

namespace Worker_Node.Services.Queue
{
    /// <inheritdoc />
    public class IngressQueueService : IIngressQueueService
    {
        #region Constants

        private const int MaxQueueLength = 100;

        #endregion

        #region Fields

        private int _nextRequestId;

        #endregion

        #region Fields

        private readonly Channel<WorkItem> _channel;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the IngressQueueService class and creates a bounded work queue with a single
        /// reader.
        /// </summary>
        public IngressQueueService()
        {
            _nextRequestId = 0;
            _channel = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(MaxQueueLength) {SingleReader = true});
        }

        #endregion

        #region Methods

        /// <inheritdoc />
        public async Task<WorkItem?> TryEnqueueAsync(dynamic payload, string taskType, string executionType, CancellationToken token)
        {
            if (!_channel.Reader.CanCount)
            {
                await Task.Yield();
                return null;
            }

            if (_channel.Reader.Count >= MaxQueueLength)
            {
                await Task.Yield();
                return null;
            }

            WorkItem item = new WorkItem
            {
                TaskId = _nextRequestId++,
                TaskType = taskType,
                ExecutionType = executionType,
                CreatedAt = DateTime.Now,
                Payload = payload["Data"],
                Retries = 0
            };

            await _channel.Writer.WriteAsync(item, token);
            return item;
        }

        /// <inheritdoc />
        public async Task<WorkItem?> TryDequeueAsync(CancellationToken token)
        {
            return await _channel.Reader.ReadAsync(token);
        }

        #endregion
    }
}
