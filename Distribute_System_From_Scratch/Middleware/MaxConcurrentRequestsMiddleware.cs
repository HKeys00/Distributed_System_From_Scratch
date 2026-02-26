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

        private const int MAX_QUEUE_LENGTH = 20;
        private const int MAX_CONCURRENT_LENGTH = 10;
        #endregion

        #region Fields

        private readonly RequestDelegate _next;
        private readonly IOptions<MaxConcurrentRequestsOptions> _options;

        private readonly SemaphoreSlim _concurrent;
        private readonly SemaphoreSlim _queue;
        
        #endregion

        #region Constructor

        public MaxConcurrentRequestsMiddleware(RequestDelegate next, IOptions<MaxConcurrentRequestsOptions> options)
        {
            _next = next;
            _options = options;

            _concurrent = new SemaphoreSlim(MAX_CONCURRENT_LENGTH, MAX_CONCURRENT_LENGTH);
            _queue = new SemaphoreSlim(MAX_QUEUE_LENGTH, MAX_QUEUE_LENGTH);
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

            var canEnterQueue = await _queue.WaitAsync(0);
            if (!canEnterQueue)
            {
                IHttpResponseFeature? responseFeature = context.Features.Get<IHttpResponseFeature>();
                if (responseFeature != null)
                {
                    responseFeature.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    responseFeature.ReasonPhrase = "Concurrent request limit exceeded";
                }

                return;
            }

            await _concurrent.WaitAsync(context.RequestAborted);
            _queue.Release();

            await _next(context);
            _concurrent.Release();
        }

        #endregion
    }
}
