
using Distributed_System_From_Scratch.Services;
using System.Diagnostics;

namespace Distributed_System_From_Scratch.Middleware
{
    public class RequestTimeMiddleware(RequestDelegate next, ILogger<RequestTimeMiddleware> logger, NodeMetricsService nodeMetricsService)
    {
        #region Fields

        private readonly RequestDelegate _next = next;
        private readonly ILogger<RequestTimeMiddleware> _logger = logger;
        private readonly NodeMetricsService _nodeMetricsService = nodeMetricsService;

        #endregion

        #region Methods

        public async Task InvokeAsync(HttpContext context)
        {
            var path = context.Request.Path.ToString();
            if (!path.EndsWith("operations/cpu") && !path.EndsWith("operations/io"))
            {
                await _next.Invoke(context);
                return;
            }

            var sw = Stopwatch.StartNew();

            await _next.Invoke(context);

            sw.Stop();
            if (context.Response.StatusCode != 499 && context.Response.StatusCode != 503)
            {
                _nodeMetricsService.RecordRequest(sw.ElapsedMilliseconds);
            }
        }

        #endregion
    }
}
