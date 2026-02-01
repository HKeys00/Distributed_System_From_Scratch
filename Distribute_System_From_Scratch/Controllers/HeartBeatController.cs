using Microsoft.AspNetCore.Mvc;

namespace Distributed_System_From_Scratch.Controllers
{
    [ApiController]
    [Route("/heartbeat")]
    public class HeartBeatController : ControllerBase
    {
        #region Methods

        [HttpPost]
        public IActionResult Ping() => Ok();

        #endregion
    }
}
