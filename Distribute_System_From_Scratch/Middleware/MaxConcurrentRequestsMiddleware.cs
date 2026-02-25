using System.Collections.Concurrent;
using Distributed_System_From_Scratch.Middleware.Options;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace Distributed_System_From_Scratch.Middleware
{
    /// <summary>
    /// Middleware class used to throttle requests.
    /// </summary>
    public class MaxConcurrentRequestsMiddleware
    {
        #region Constants

        private const int MAX_CONCURRENT_OPERATIONS = 8;
        private const int MAX_QUEUE_LENGTH = 100;

        #endregion

        #region Fields

        private readonly RequestDelegate _next;
        private readonly IOptions<MaxConcurrentRequestsOptions> _options;

        private ConcurrentQueue<int> _queue;
        private int _numExecutions;

        #endregion

        #region Constructor

        public MaxConcurrentRequestsMiddleware(RequestDelegate next, IOptions<MaxConcurrentRequestsOptions> options)
        {
            _next = next;
            _options = options;

            _queue = new ConcurrentQueue<int>();
            _numExecutions = 0;
        }

        #endregion

        #region Methods

        public async Task Invoke(HttpContext context)
        {
            if (!_options.Value.Enabled)
            {
                await _next(context);
                return;
            }
            
            if (CanRunOperation())
            {
                Interlocked.Increment(ref _numExecutions);
                await _next(context);
                Interlocked.Decrement(ref _numExecutions);
            }

            if (CanQueueOperation())
            {
                // TODO: Implement Queue logic.
            }

            IHttpResponseFeature? responseFeature = context.Features.Get<IHttpResponseFeature>();
            if (responseFeature != null)
            {
                responseFeature.StatusCode = StatusCodes.Status503ServiceUnavailable;
                responseFeature.ReasonPhrase = "Concurrent request limit exceeded";
            }
        }

        /// <summary>
        /// Returns if the operation can be run immediately.
        /// </summary>
        /// <returns>a bool.</returns>
        private bool CanRunOperation() => Volatile.Read(ref _numExecutions) < MAX_CONCURRENT_OPERATIONS;

        /// <summary>
        /// Returns if the operation can be queued.
        /// </summary>
        /// <returns>a bool.</returns>
        private bool CanQueueOperation() => _queue.Count < MAX_QUEUE_LENGTH;

        #endregion
    }
}
