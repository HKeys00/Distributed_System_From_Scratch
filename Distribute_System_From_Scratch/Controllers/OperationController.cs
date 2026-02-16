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
        public IActionResult PostCPUBound(CancellationToken token)
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 100_000_000; i++)
            {
                Math.Sqrt(i);
                if (token.IsCancellationRequested)
                {
                    sw.Stop();
                    return StatusCode(499);
                }
            }
            sw.Stop();
            _metrics.RecordExecution(sw.ElapsedMilliseconds);
            return Ok();
        }

        [HttpPost("io")]
        public async Task<IActionResult> PostIOBound(CancellationToken token)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await Task.Delay(5000, token);
            } catch (OperationCanceledException)
            {
                return StatusCode(499);
            }
            
            sw.Stop();
            _metrics.RecordExecution(sw.ElapsedMilliseconds);
            return Ok();
        }
    }
}
