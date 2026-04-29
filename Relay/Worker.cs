using Data;
using Data.Models.Task;
using Shared.DTOs;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text.Json;
using Timer = System.Timers.Timer;

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

        private readonly Dictionary<ulong, IWorkItem> _unAckedTasks;
        private readonly HashSet<Guid> _pendingTasks;


        private readonly Timer _outBoxTimer;
        private readonly Timer _staleTimer;

        private bool _processingOutbox;
        private bool _processingStale;

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

            _processingOutbox = false;
            _processingStale = false;

            _outBoxTimer = new Timer(5000);
            _outBoxTimer.AutoReset = true;
            _outBoxTimer.Enabled = true;
            _outBoxTimer.Elapsed += async (_, _) => await OnProcessOutboxQueue();

            _staleTimer = new Timer(10000);
            _staleTimer.AutoReset = true;
            _staleTimer.Enabled = true;
            _staleTimer.Elapsed += async (_, _) => await OnProcessStaleTasks();

            _unAckedTasks = new Dictionary<ulong, IWorkItem>();
            _pendingTasks = new HashSet<Guid>();
        }

        #endregion

        #region Methods

        /// <inheritdoc />
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
             var factory = new ConnectionFactory() { HostName = "rabbitmq", Port = 5672 };
            
            _rabbitConnection = await factory.CreateConnectionAsync(stoppingToken);
            
            await using var connection = new NpgsqlConnection(_configuration.GetConnectionString("Default"));
            await connection.OpenAsync(stoppingToken);
            connection.Notification += OnNotify;
            await using (var cmd = new NpgsqlCommand("LISTEN task_channel", connection))
            {
                try
                {
                    await cmd.ExecuteNonQueryAsync(stoppingToken);
                } catch (Exception ex)
                {
                    _logger.LogError("Failed to create listener for task_channel, {message}", ex.Message);
                }
            }

            await OnProcessOutboxQueue();
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
            await OnProcessOutboxQueue();
        }

        /// <summary>
        /// Handles the event that occurs when a message is returned by the broker because it could not be routed to a
        /// queue.
        /// </summary>
        /// <param name="sender">The source of the event, typically the channel or connection that raised the return event.</param>
        /// <param name="args">The event data containing information about the returned message, including the reply code, reply text, and
        /// message properties.</param>
        private async Task OnAckReturn(object sender, BasicAckEventArgs args)
        {
            var removed = _unAckedTasks.Remove(args.DeliveryTag, out var task);
            if (!removed || task == null)
            {
                //Acking a task that has already either been re sent or marked as acked, either way ignore and keep going.
                _logger.LogWarning("Task {tag} has already been resent or marked as acked", args.DeliveryTag);
                await Task.Yield();
                return;
            }

            await using var context = await _dbContextFactory.CreateDbContextAsync();
            try
            {
                await context.Database.BeginTransactionAsync();
                await context.Database.ExecuteSqlRawAsync("UPDATE \"Tasks\" SET \"PublishedAt\" = clock_timestamp() WHERE \"TaskId\" = {0}", task.TaskId);
                await context.Database.CommitTransactionAsync();
                _logger.LogInformation("Marked task with id {id} as acked", task.TaskId);
            } catch (Exception ex)
            {
                _logger.LogError("Failed to ack task with id {id}, {message}", task.TaskId, ex.Message);
            } finally
            {
                _pendingTasks.Remove(task.TaskId);
            }            
        }

        /// <summary>
        /// Processes messages from the outbox queue in batches and sends them to the broker.
        /// </summary>
        /// <returns>A task representing the asynchronous operation.</returns>
        private async Task OnProcessOutboxQueue()
        {
            if (_processingOutbox)
            {
                return;
            }

            _processingOutbox = true;
            await using var context = await _dbContextFactory.CreateDbContextAsync();

            int page = 1;
            const int pageSize = 5;

            while (true)
            {
                List<OutboxWorkItem> tasks;

                try
                {
                    tasks = await context.Outbox.Take(pageSize * page).AsNoTracking().ToListAsync();
                } 
                catch (Exception ex)
                {
                    //Database not ready yet.
                    _logger.LogError("Error trying to read {name}, skipping process. {message}", nameof(context.Outbox), ex.Message);
                    break;
                }

                if (tasks.Count == 0)
                {
                    break;
                }

                try
                {
                    var ids = await SendMessagesToBroker(tasks);
                    await context.Database.BeginTransactionAsync();
                    foreach (var id in ids)
                    {
                        await context.Database.ExecuteSqlRawAsync("UPDATE \"Tasks\" SET \"SentAt\" = clock_timestamp() WHERE \"TaskId\" = {0}", id);
                    }
                    await context.Database.CommitTransactionAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError("Unexpected error occured while sending outbox tasks, {message}", ex.Message);
                    break;
                }
                
                page++;
            }

            _processingOutbox = false;
        }

        /// <summary>
        /// Processes stale tasks from the database in pages, handling exceptions and ensuring only one processing
        /// operation runs at a time.
        /// </summary>
        private async Task OnProcessStaleTasks()
        {
            if (_processingStale)
            {
                return;
            }

            _processingStale = true;
            await using var context = await _dbContextFactory.CreateDbContextAsync();

            int page = 1;
            const int pageSize = 5;

            while (true)
            {
                List<StaleWorkItem> staleTasks;

                try
                {
                    staleTasks = await context.StaleTasks.Take(pageSize * page).AsNoTracking().ToListAsync();
                }
                catch (Exception ex)
                {
                    //Database not ready yet.
                    _logger.LogError("Error trying to read {name}, skipping process. {message}", nameof(context.StaleTasks), ex.Message);
                    break;
                }

                if (staleTasks.Count == 0)
                {
                    break;
                }

                try
                {
                    var ids = await SendMessagesToBroker(staleTasks);
                    await context.Database.BeginTransactionAsync();
                    foreach (var id in ids)
                    {
                        await context.Database.ExecuteSqlRawAsync("UPDATE \"Tasks\" SET \"SentAt\" = clock_timestamp(), \"Retries\" = \"Retries\" + 1 WHERE \"TaskId\" = {0}", id);
                    }
                    await context.Database.CommitTransactionAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError("Unexpected error occured while sending stale tasks, {message}", ex.Message);
                    break;
                }

                page++;
            }

            _processingStale = false;
        }
        
        /// <summary>
        /// Sends a batch of outbox work items to the RabbitMQ broker, ensuring the channel and queue are properly
        /// initialized.
        /// </summary>
        /// <param name="workItems">The list of outbox work items to be sent to the broker.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        /// <exception cref="Exception">Thrown if the connection to RabbitMQ does not exist.</exception>
        private async Task<HashSet<Guid>> SendMessagesToBroker<T>(List<T> workItems) where T : IWorkItem
        {
            await ConfirmRabbitInitialized();
            HashSet<Guid> sentMessages = new HashSet<Guid>();

            var batchStartingNumber = await _rabbitChannel!.GetNextPublishSequenceNumberAsync();
            for (int i = 0; i < workItems.Count; i++)
            {
                var workItem = workItems[i];
                var message = new CrawlMessage(workItem.TaskId, workItem.IdempotencyId, workItem.Url, workItem.Retries);
                _unAckedTasks.Add(batchStartingNumber + (ulong)i, workItem);
                try
                {
                    await _rabbitChannel.BasicPublishAsync(exchange: string.Empty, routingKey: "outbox",
                        body: JsonSerializer.SerializeToUtf8Bytes(message));
                }
                catch (Exception ex)
                {
                    _logger.LogError("Message with id {id} failed to send across rabbit channel, {message}", workItem.Id, ex.Message);
                    continue;
                }
                sentMessages.Add(workItem.TaskId);
            }

            return sentMessages;
        }

        /// <summary>
        /// Confirms that the rabbitMq connection and channels have been initialized and are ready to accept messages.
        /// </summary>
        /// <exception cref="Exception"></exception>
        private async Task ConfirmRabbitInitialized()
        {
            if (_rabbitConnection == null)
            {
                throw new Exception("Connection to rabbitMQ doesn't exist");
            }

            if (_rabbitChannel == null || _rabbitChannel.IsClosed)
            {
                _rabbitChannel = await _rabbitConnection.CreateChannelAsync(new CreateChannelOptions(true, true));
                _rabbitChannel.BasicAcksAsync += OnAckReturn;

                await _rabbitChannel.QueueDeclareAsync(queue: "outbox",
                    durable: true, exclusive: false,
                    autoDelete: false,
                    arguments: new Dictionary<string, object?> { 
                        { "x-queue-type", "quorum" },
                        { "x-dead-letter-exchange", "outbox"}
                    });
                return;
            }

            await Task.Yield();
        }

        #endregion
    }
}
