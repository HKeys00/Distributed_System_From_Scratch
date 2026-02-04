using Microsoft.AspNetCore.Mvc;

namespace Distributed_System_From_Scratch.Controllers
{
    [ApiController]
    [Route("/heartbeat")]
    public class HeartBeatController : ControllerBase
    {
        #region Methods

        [HttpGet]
        public IActionResult Ping() => Ok();

        #endregion
    }
}
