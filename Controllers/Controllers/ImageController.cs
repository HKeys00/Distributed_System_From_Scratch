using System.Dynamic;
using Microsoft.AspNetCore.Mvc;
using Shared.Constants;
using Shared.Models;
using Worker_Node.Services.Queue;

namespace Controllers.Controllers
{
    /// <summary>
    /// Handles image-related HTTP requests in the application.
    /// </summary>
    [ApiController]
    [Route("image")]
    public class ImageController : ControllerBase
    {
        #region Fields

        private readonly IIngressQueueService _ingressQueueService;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the ImageController class with the specified ingress queue service.
        /// </summary>
        /// <param name="ingressQueueService">The service used to handle ingress queue operations.</param>
        public ImageController(IIngressQueueService ingressQueueService)
        {
            _ingressQueueService = ingressQueueService;
        }

        #endregion

        #region Methods

        /// <summary>
        /// Receives image data and a cancellation token to perform image compression.
        /// </summary>
        /// <param name="data">The byte array containing the image data to compress.</param>
        /// <param name="token">A token to monitor for cancellation requests.</param>
        [HttpPost("compress")]
        public async Task<IActionResult> CompressImage([FromBody] byte[] data, CancellationToken token)
        {
            var payload = new ExpandoObject();
            payload.TryAdd("Data", data);

            var result = await _ingressQueueService.TryEnqueueAsync(payload, "image_compress", ExecutionType.CPU, token);
            
            if (result == null)
            {
                return StatusCode(429, "Too many requests. Please try again later.");
            }

            return Accepted(result);
        }

        #endregion
    }
}
