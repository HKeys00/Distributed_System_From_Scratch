using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Timers;

namespace Shared.Services
{
    public class HeartBeatService : IHostedService, IHeartBeatService
    {
        #region Fields

        private System.Timers.Timer _timer;
        private readonly ILogger<HeartBeatService> _logger;
        private string[] peers;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the heart beat service class.
        /// </summary>
        /// <param name="logger">The injected logger instance.</param>
        /// <param name="configuration">The docker configuration parameters.</param>
        public HeartBeatService(ILogger<HeartBeatService> logger, IConfiguration configuration)
        {
            _timer = new System.Timers.Timer();
            _logger = logger;

            peers = configuration.GetSection("PEERS")?.Value?.Split(",") ?? [];
        }

        #endregion

        #region Methods

        /// <inheritdoc />
        public Task StartAsync(CancellationToken token)
        {
            _timer = new System.Timers.Timer(5000);
            _timer.Elapsed += OnTimerElapsed;
            _timer.AutoReset = true;
            _timer.Enabled = true;

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken)
        {
            _timer.Dispose();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Callback when the timer elapses.
        /// </summary>
        /// <param name="source">The source object.</param>
        /// <param name="args">The elapsed arguments.</param>
        private void OnTimerElapsed(Object? source, ElapsedEventArgs args)
        {
            _ = SendHeartBeat(peers);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="peers"></param>
        /// <returns></returns>
        public async Task SendHeartBeat(string[] peers)
        {

        }

        #endregion
    }
}