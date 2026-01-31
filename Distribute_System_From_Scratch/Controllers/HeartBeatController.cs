using Microsoft.AspNetCore.Mvc;

namespace Distributed_System_From_Scratch.Controllers
{
    public class HeartBeatController
    {
        [ApiController]
        [Route("/heartbeat")]
        public class DataStoreController : ControllerBase
        {
            #region Methods

            [HttpPost]
            public IActionResult Ping() => Ok();

            #endregion
        }
    }
}
