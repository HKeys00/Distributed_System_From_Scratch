using Distributed_System_From_Scratch.Services;
using Microsoft.AspNetCore.Mvc;

namespace Distributed_System_From_Scratch.Controllers
{
    [ApiController]
    [Route("/heartbeat")]
    public class HeartBeatController(INodeInformationService nodeInformationService) : ControllerBase
    {
        #region Methods

        [HttpGet]
        public IActionResult Ping()
        {
            return Ok(nodeInformationService.IncarnationNumber.Ticks);
        }

        #endregion
    }
}
