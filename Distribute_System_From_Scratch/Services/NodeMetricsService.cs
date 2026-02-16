using System.Diagnostics;
using System.Threading;

namespace Distributed_System_From_Scratch.Services
{
    public class NodeMetricsService
    {
        private long _totalRequests;
        private long _totalExecutionTimeMs;
        private readonly object _lock = new();
        private readonly Stopwatch _throughputWatch = Stopwatch.StartNew();
        private long _lastThroughputCount;

        public void RecordExecution(long elapsedMs)
        {
            Interlocked.Increment(ref _totalRequests);
            Interlocked.Add(ref _totalExecutionTimeMs, elapsedMs);
        }

        public double GetAverageExecutionTime() =>
            _totalRequests == 0 ? 0 : (double)_totalExecutionTimeMs / _totalRequests;

        public long GetTotalRequests() => Interlocked.Read(ref _totalRequests);

        public double GetRequestThroughput()
        {
            lock (_lock)
            {
                var elapsedSeconds = _throughputWatch.Elapsed.TotalSeconds;
                var currentCount = _totalRequests;
                var throughput = (currentCount - _lastThroughputCount) / elapsedSeconds;
                _lastThroughputCount = currentCount;
                _throughputWatch.Restart();
                return throughput;
            }
        }

        public int GetThreadCount() => Process.GetCurrentProcess().Threads.Count;

        public double GetCpuUsage()
        {
            // For demo: returns a dummy value. Use PerformanceCounter or System.Diagnostics.Process for real CPU usage.
            return 0.0;
        }
    }
}