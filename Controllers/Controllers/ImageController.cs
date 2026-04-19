using Data;
using Data.Models.Task;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shared.Constants;
using System.Dynamic;

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

        private readonly ILogger<ImageController> _logger;
        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
        private readonly int _maxUnpublishedRequests = 400; //Temp value for now need to figure out a better way of sharing this across multiple controllers.

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the ImageController class with the specified ingress queue service.
        /// </summary>
        /// <param name="logger">The injected logger instance.</param>
        /// <param name="dbContextFactory">The injected db context factory.</param>
        public ImageController(ILogger<ImageController> logger, IDbContextFactory<ApplicationDbContext> dbContextFactory)
        {
            _logger = logger;
            _dbContextFactory = dbContextFactory;
        }

        #endregion

        #region Methods

        /// <summary>
        /// Receives image data and a cancellation token to perform image compression.
        /// </summary>
        /// <param name="data">The byte array containing the image data to compress.</param>
        /// <param name="token">A token to monitor for cancellation requests.</param>
        [HttpPost("compress")]
        public async Task<IActionResult> CompressImage(CancellationToken token)
        {
            await using var context = await _dbContextFactory.CreateDbContextAsync();

            var pendingCount = await context.Outbox.CountAsync(token);
            if (pendingCount > _maxUnpublishedRequests)
            {
                _logger.LogWarning("Failed to create task for {name}, too many requests pending.", nameof(CompressImage));
                return StatusCode(429, "Too many requests. Please try again later.");
            }

            var payload = new ExpandoObject();
            payload.TryAdd("Data", "AHHHH");

            WorkItem item = new WorkItem()
            {
                TaskId = Guid.NewGuid(),
                TaskType = "image-compress",
                ExecutionType = ExecutionType.CPU
            };

            context.Tasks.Add(item);

            try
            {
                await context.SaveChangesAsync(token);
            }
            catch (Exception ex)
            {
                _logger.LogError("Unexpected error occured. {message}", ex.Message);
                return StatusCode(500, $"Internal server error. {ex.Message}"); //String interpolation not very performant.
            }

            _logger.LogInformation("{name} Task created with id {id}", nameof(CompressImage), item.TaskId);
            return Accepted(item.TaskId);
        }

        #endregion
    }
}
