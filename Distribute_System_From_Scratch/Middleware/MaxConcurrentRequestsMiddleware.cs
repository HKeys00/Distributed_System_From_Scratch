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

        #endregion

        #region Fields

        private readonly RequestDelegate _next;
        private readonly IOptions<MaxConcurrentRequestsOptions> _options;

        private readonly SemaphoreSlim _queue;
        
        #endregion

        #region Constructor

        public MaxConcurrentRequestsMiddleware(RequestDelegate next, IOptions<MaxConcurrentRequestsOptions> options)
        {
            _next = next;
            _options = options;

            _queue = new SemaphoreSlim(MAX_QUEUE_LENGTH);
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

            var canEnter = await _queue.WaitAsync(0);
            if (!canEnter)
            {
                IHttpResponseFeature? responseFeature = context.Features.Get<IHttpResponseFeature>();
                if (responseFeature != null)
                {
                    responseFeature.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    responseFeature.ReasonPhrase = "Concurrent request limit exceeded";
                }

                return;
            }
            
            await _next(context);
            _queue.Release(1);
        }

        #endregion
    }
}
