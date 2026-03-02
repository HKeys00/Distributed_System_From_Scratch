using System.Diagnostics;
using System.Threading;

namespace Distributed_System_From_Scratch.Services
{
    public class NodeMetricsService
    {

        private readonly object _lock = new();
        private readonly Stopwatch _throughputWatch = Stopwatch.StartNew();
        //private readonly PerformanceCounter 

        private long _totalRequests;
        private long _totalExecutions;
        private long _totalExecutionTimeMs;
        private long _totalRequestTimeMs;
        private long _lastThroughputCount;

        private long _numCpuUsageRecords;
        private long _avgCpuUsage;

        public void RecordExecution(long elapsedMs)
        {
            Interlocked.Increment(ref _totalExecutions);
            Interlocked.Add(ref _totalExecutionTimeMs, elapsedMs);
        }

        public void RecordRequest(long elapsedMs)
        {
            Interlocked.Increment(ref _totalRequests);
            Interlocked.Add(ref _totalRequestTimeMs, elapsedMs);
        }

        public double GetAverageRequestTimeMs() =>
            _totalRequests == 0 ? 0 : (double)_totalRequestTimeMs / _totalRequests;

        public double GetAverageExecutionTime() =>
            _totalExecutions == 0 ? 0 : (double)_totalExecutionTimeMs / _totalExecutions;

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
            //_avgCpuUsage += 
            // For demo: returns a dummy value. Use PerformanceCounter or System.Diagnostics.Process for real CPU usage.
            return 0.0;
        }
    }
}