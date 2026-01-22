using Distributed_System_From_Scratch.Helpers;
using Distributed_System_From_Scratch.Services;

namespace Distributed_System_From_Scratch.Middleware
{
    /// <summary>
    /// Middleware that logs the request and response alongside the
    /// container id.
    /// </summary>
    public class RequestResponseLoggerMiddleware(RequestDelegate next, ILogger<RequestResponseLoggerMiddleware> logger, INodeInformationService nodeInformation)
    {
        #region Fields

        private readonly RequestDelegate _next = next;
        private readonly ILogger<RequestResponseLoggerMiddleware> _logger = logger;
        private readonly INodeInformationService _nodeInformation = nodeInformation;

        #endregion

        #region Methods
        
        public async Task InvokeAsync(HttpContext context)
        {
            context.Request.EnableBuffering();

            var node = _nodeInformation.GetNodeId();
            var path = context.Request.Path;

            _logger.LogInformation("Node {0} received request for path {1}", node, path);

            var currentBody = context.Response.Body;
            using var memoryStream = new MemoryStream();
            context.Response.Body = memoryStream;
            await _next(context);

            context.Response.Body = currentBody;
            memoryStream.Seek(0, SeekOrigin.Begin);

            var readToEnd = await new StreamReader(memoryStream).ReadToEndAsync();
            var result = readToEnd.WrapReturnMessage(node, path, context.Response.StatusCode);
            await context.Response.WriteAsync(result);
        }

        #endregion
    }
}
