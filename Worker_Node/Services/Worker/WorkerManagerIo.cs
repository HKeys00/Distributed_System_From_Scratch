namespace Worker_Node.Services.Worker
{
    /// <summary>
    /// Manages the lifecycle of a background worker as a hosted service.
    /// </summary>
    public class WorkerManagerIo : IHostedService
    {
        #region Methods

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        #endregion
    }
}
