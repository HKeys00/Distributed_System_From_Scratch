using Distributed_System_From_Scratch.Services;
using System.Timers;

namespace Distributed_System_From_Scratch.BackgroundWorkers
{
    /// <summary>
    /// Hosted service for monitoring metrics for each node.
    /// </summary>
    public class MetricsHostedService : IHostedService
    {
        #region Fields

        private System.Timers.Timer _timer;
        private readonly NodeMetricsService _nodeMetricsService;
        private readonly ILogger<MetricsHostedService> _logger;

        #endregion

        #region Constructor

        public MetricsHostedService(NodeMetricsService nodeMetricsService, ILogger<MetricsHostedService> logger)
        {
            _timer = new System.Timers.Timer();
            _nodeMetricsService = nodeMetricsService;
            _logger = logger;
        }

        #endregion

        #region Methods

        public Task StartAsync(CancellationToken token)
        {
            _timer = new System.Timers.Timer(10000);
            _timer.Elapsed += DoWork;
            _timer.AutoReset = true;
            _timer.Enabled = true;

            return Task.CompletedTask;
        }

        public void DoWork(Object? source, ElapsedEventArgs e)
        {
            var totalRequests = _nodeMetricsService.GetTotalRequests();
            var avgExecTime = _nodeMetricsService.GetAverageExecutionTime();
            var throughput = _nodeMetricsService.GetRequestThroughput();
            var threadCount = _nodeMetricsService.GetThreadCount();
            var cpuUsage = _nodeMetricsService.GetCpuUsage();

            _logger.LogWarning(
                "Node Metrics: TotalRequests={TotalRequests}, AvgExecTimeMs={AvgExecTime}, Throughput={Throughput}, ThreadCount={ThreadCount}, CpuUsage={CpuUsage}%",
                totalRequests,
                avgExecTime,
                throughput,
                threadCount,
                cpuUsage
            );
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _timer.Dispose(); 
            return Task.CompletedTask;
        }

        #endregion
    }
}
