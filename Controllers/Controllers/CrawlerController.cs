using Controllers.Requests;
using Data;
using Data.Models.Task;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Shared.Constants;
using Shared.Helpers;

namespace Controllers.Controllers
{
    /// <summary>
    /// Handles crawl HTTP requests in the application.
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
        public async Task<IActionResult> Crawl([FromBody] CrawlRequest request, CancellationToken token)
        {
            var correlationId = HttpContext.Items[CorrelationConstants.HttpContextItemKey] is Guid id
                ? id
                : Guid.NewGuid();

            await using var context = await _dbContextFactory.CreateDbContextAsync();
            var pendingCount = await context.Outbox.CountAsync(token);
            if (pendingCount > _maxUnpublishedRequests)
            {
                _logger.LogWarning("CorrelationId={CorrelationId} Request throttled, too many pending tasks", correlationId);
                AppMetrics.Controllers.TasksAccepted.WithLabels("throttled").Inc();
                return StatusCode(429, "Too many requests. Please try again later.");
            }

            WorkItem item = new WorkItem()
            {
                TaskId = Guid.NewGuid(),
                CorrelationId = correlationId,
                Url = request.Url,
                IdempotencyId = request.Url.HashUrl()
            };

            var exists = await context.Successes.FirstOrDefaultAsync(s => s.IdempotencyId == item.IdempotencyId);
            if (exists != null)
            {
                _logger.LogInformation("CorrelationId={CorrelationId} TaskId={TaskId} Url already recently crawled, skipping", correlationId, exists.TaskId);
                AppMetrics.Controllers.TasksAccepted.WithLabels("duplicate").Inc();
                return Accepted(exists.TaskId);
            }

            context.Tasks.Add(item);

            try
            {
                await context.SaveChangesAsync(token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CorrelationId={CorrelationId} TaskId={TaskId} Failed to persist task", correlationId, item.TaskId);
                AppMetrics.Controllers.TasksAccepted.WithLabels("error").Inc();
                return StatusCode(500, $"Internal server error. {ex.Message}"); //String interpolation not very performant.
            }

            _logger.LogInformation("CorrelationId={CorrelationId} TaskId={TaskId} Task created", correlationId, item.TaskId);
            AppMetrics.Controllers.TasksAccepted.WithLabels("accepted").Inc();
            return Accepted(item.TaskId);
        }
    }

    #endregion
}