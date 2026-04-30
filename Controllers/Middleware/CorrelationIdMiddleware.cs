using Shared.Constants;

namespace Controllers.Middleware
{
    /// <summary>
    /// Resolves the correlation id for an incoming request — taking it from the
    /// <c>X-Correlation-ID</c> header when supplied, otherwise minting a fresh one — then
    /// stashes it on <c>HttpContext.Items</c>, echoes it on the response and pushes it into
    /// the logging scope so every log line emitted while handling the request carries it.
    /// </summary>
    public class CorrelationIdMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<CorrelationIdMiddleware> _logger;

        public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            Guid correlationId;
            bool headerSupplied;
            if (context.Request.Headers.TryGetValue(CorrelationConstants.HeaderName, out var raw)
                && Guid.TryParse(raw, out var parsed))
            {
                correlationId = parsed;
                headerSupplied = true;
            }
            else
            {
                correlationId = Guid.NewGuid();
                headerSupplied = false;
            }

            context.Items[CorrelationConstants.HttpContextItemKey] = correlationId;
            context.Response.Headers[CorrelationConstants.HeaderName] = correlationId.ToString();

            using (_logger.BeginScope(new Dictionary<string, object>
            {
                [CorrelationConstants.LogScopeKey] = correlationId
            }))
            {
                _logger.LogDebug("Resolved correlation id from {Source}", headerSupplied ? "header" : "minted");
                await _next(context);
            }
        }
    }
}
