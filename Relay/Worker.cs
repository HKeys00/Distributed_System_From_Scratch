using Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Text;
using RabbitMQ.Client;

namespace Relay
{
    public class Worker : BackgroundService
    {
        #region Fields

        private IConnection? _rabbitConnection;
        private IChannel? _rabbitChannel;

        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<Worker> _logger;

        private int _batchedItems;
        private int _maxBatchedItems = 10;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the relay worker.
        /// </summary>
        /// <param name="dbContextFactory">The injected dbContext factory.</param>
        /// <param name="configuration">The injected configuration</param>
        /// <param name="logger">The injected logger instance.</param>
        public Worker(IDbContextFactory<ApplicationDbContext> dbContextFactory, IConfiguration configuration, ILogger<Worker> logger)
        {
            _configuration = configuration;
            _dbContextFactory = dbContextFactory;
            _logger = logger;
            _batchedItems = 0;
            _maxBatchedItems = 10;
        }

        #endregion

        #region Methods

        /// <inheritdoc />
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
             var factory = new ConnectionFactory() { HostName = "rabbitmq", Port = 5672 };
            _rabbitConnection = await factory.CreateConnectionAsync(stoppingToken);
            _rabbitChannel = await _rabbitConnection.CreateChannelAsync(null, stoppingToken);

            await _rabbitChannel.QueueDeclareAsync("outbox", true, true, cancellationToken: stoppingToken);
            
            await using var connection = new NpgsqlConnection(_configuration.GetConnectionString("Default"));
            await connection.OpenAsync();
            connection.Notification += OnNotify;
            await using (var cmd = new NpgsqlCommand("LISTEN task_channel", connection))
            {
                await cmd.ExecuteNonQueryAsync(stoppingToken);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                await connection
                    .WaitAsync(stoppingToken)
                    .ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Handles a notification from the Postgres DB
        /// </summary>
        /// <param name="obj">The object data.</param>
        /// <param name="args">The event arguments.</param>
        private async void OnNotify(object obj, NpgsqlNotificationEventArgs args)
        {
            await using var context = await _dbContextFactory.CreateDbContextAsync();
            var tasks = await context.Tasks.Where(t => t.PublishedAt == null).ToArrayAsync();

            if (_rabbitConnection == null)
            {
                throw new Exception("Connection to rabbitMQ doesn't exist");
            }

            if (_rabbitChannel == null || _rabbitChannel.IsClosed)
            {
                _rabbitChannel = await _rabbitConnection.CreateChannelAsync();
            }

            foreach (var task in tasks)
            {
                await _rabbitChannel.BasicPublishAsync(string.Empty, "outbox", Encoding.UTF8.GetBytes(task.Payload));
            }

            var m = 0;
        }

        #endregion
    }
}
