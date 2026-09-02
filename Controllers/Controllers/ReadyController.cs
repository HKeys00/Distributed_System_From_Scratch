using Microsoft.AspNetCore.Mvc;

namespace Controllers.Controllers
{
    /// <summary>
    /// Handles readiness probe requests.
    /// </summary>
    [ApiController]
    [Route("ready")]
    public class ReadyController : ControllerBase
    {
        #region Methods

        [HttpGet]
        public IActionResult Ready()
        {
            return Ok();
        }

        #endregion
    }
}
