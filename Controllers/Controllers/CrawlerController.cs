using Data;
using Data.Models.Task;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shared.Helpers;

namespace Controllers.Controllers
{
    /// <summary>
    /// Handles image-related HTTP requests in the application.
    /// </summary>
    [ApiController]
    [Route("crawl")]
    public class CrawlerController : ControllerBase
    {
        #region Fields

        private readonly ILogger<CrawlerController> _logger;
        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
        private readonly int _maxUnpublishedRequests = 400; //Temp value for now need to figure out a better way of sharing this across multiple controllers.

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the CrawlerController class with the specified ingress queue service.
        /// </summary>
        /// <param name="logger">The injected logger instance.</param>
        /// <param name="dbContextFactory">The injected db context factory.</param>
        public CrawlerController(ILogger<CrawlerController> logger, IDbContextFactory<ApplicationDbContext> dbContextFactory)
        {
            _logger = logger;
            _dbContextFactory = dbContextFactory;
        }

        #endregion

        #region Methods

        [HttpPost]
        public async Task<IActionResult> Crawl([FromBody] string Url, CancellationToken token)
        {
            await using var context = await _dbContextFactory.CreateDbContextAsync();
            var pendingCount = await context.Outbox.CountAsync(token);
            if (pendingCount > _maxUnpublishedRequests)
            {
                _logger.LogWarning("Failed to create task for {name}, too many requests pending.", nameof(Crawl));
                return StatusCode(429, "Too many requests. Please try again later.");
            }

            WorkItem item = new WorkItem()
            {
                TaskId = Guid.NewGuid(),
                Url = Url,
                IdempotencyId = Url.HashUrl()
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

            _logger.LogInformation("{name} Task created with id {id}", nameof(Crawl), item.TaskId);
            return Accepted(item.TaskId);
        }
    }

    #endregion
}