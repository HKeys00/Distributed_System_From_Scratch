using Microsoft.AspNetCore.Mvc;
using Distributed_System_From_Scratch.Services;
using System.Diagnostics;

namespace Distributed_System_From_Scratch.Controllers
{
    [ApiController]
    [Route("/operations")]
    public class OperationController : ControllerBase
    {
        private readonly NodeMetricsService _metrics;

        public OperationController(NodeMetricsService metrics)
        {
            _metrics = metrics;
        }

        [HttpPost("cpu")]
        public IActionResult PostCPUBound()
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 100_000_000; i++)
            {
                Math.Sqrt(i);
            }
            sw.Stop();
            _metrics.RecordExecution(sw.ElapsedMilliseconds);
            return Ok();
        }

        [HttpPost("io")]
        public async Task<IActionResult> PostIOBound()
        {
            var sw = Stopwatch.StartNew();
            await Task.Delay(5000);
            sw.Stop();
            _metrics.RecordExecution(sw.ElapsedMilliseconds);
            return Ok();
        }
    }
}
