using Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Controllers.Controllers
{
    /// <summary>
    /// Handles startup probe requests, reporting when the application's dependencies are available.
    /// </summary>
    [ApiController]
    [Route("startup")]
    public class StartupController : ControllerBase
    {
        #region Fields

        private readonly ILogger<StartupController> _logger;
        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the StartupController class.
        /// </summary>
        /// <param name="logger">The injected logger instance.</param>
        /// <param name="dbContextFactory">The injected db context factory.</param>
        public StartupController(ILogger<StartupController> logger, IDbContextFactory<ApplicationDbContext> dbContextFactory)
        {
            _logger = logger;
            _dbContextFactory = dbContextFactory;
        }

        #endregion

        #region Methods

        [HttpGet]
        public async Task<IActionResult> Startup(CancellationToken token)
        {
            try
            {
                using var context = await _dbContextFactory.CreateDbContextAsync(token);
                var pending = (await context.Database.GetPendingMigrationsAsync(token)).ToList();

                if (pending.Count == 0)
                {
                    return Ok();
                }

                _logger.LogWarning(
                    "Database schema is out of date, not started. {Count} migration(s) pending: {Pending}",
                    pending.Count,
                    string.Join(", ", pending));
                return BadRequest();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to reach database, not started");
                return BadRequest();
            }
        }

        #endregion
    }
}
