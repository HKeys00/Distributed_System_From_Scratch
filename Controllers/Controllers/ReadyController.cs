using Controllers.Requests;
using Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Controllers.Controllers
{
    /// <summary>
    /// Handles image-related HTTP requests in the application.
    /// </summary>
    [ApiController]
    [Route("ready")]
    public class ReadyController : ControllerBase
    {
        #region Fields

        private readonly ILogger<CrawlerController> _logger;
        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the CrawlerController class with the specified ingress queue service.
        /// </summary>
        /// <param name="logger">The injected logger instance.</param>
        /// <param name="dbContextFactory">The injected db context factory.</param>
        public ReadyController(ILogger<CrawlerController> logger, IDbContextFactory<ApplicationDbContext> dbContextFactory)
        {
            _logger = logger;
            _dbContextFactory = dbContextFactory;
        }

        #endregion

        #region Methods

        [HttpGet]
        public async Task<IActionResult> Ready(CancellationToken token)
        {
            try
            {
                var context = await _dbContextFactory.CreateDbContextAsync(token);
                return Ok();
            } catch
            {
                _logger.LogWarning("Failed to reach database, not ready");
                return Forbid();
            }
        }

        #endregion
    }
}
