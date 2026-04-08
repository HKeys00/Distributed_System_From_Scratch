using Data;
using Data.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Relay.Models;
using System.Text;
using Timer = System.Timers.Timer;

namespace Relay
{
    public class Worker : BackgroundService
    {
        #region Constructor

        private const int _baselineMinutesBeforeResend = 1;

        #endregion

        #region Fields

        private IConnection? _rabbitConnection;
        private IChannel? _rabbitChannel;

        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<Worker> _logger;

        private readonly Dictionary<ulong, PendingTask> _unAckedTasks;
        private readonly HashSet<Guid> _pendingTasks;


        private readonly Timer _timer;
        private bool _processing;

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
            _processing = false;

            _timer = new Timer(5000);
            _timer.AutoReset = true;
            _timer.Enabled = true;
            _timer.Elapsed += async (_, _) => await OnProcessOutboxQueue();

            _unAckedTasks = new Dictionary<ulong, PendingTask>();
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
                await cmd.ExecuteNonQueryAsync(stoppingToken);
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
            if (args.DeliveryTag % 5 == 0)
            {
                await Task.Yield();
                return;
            }

            var removed = _unAckedTasks.Remove(args.DeliveryTag, out var task);
            if (!removed)
            {
                //Acking a task that has already either been re sent or marked as acked, either way ignore and keep going.
                await Task.Yield();
                return;
            }


            await using var context = await _dbContextFactory.CreateDbContextAsync();
            try
            {
                await context.Database.BeginTransactionAsync();
                await context.Database.ExecuteSqlRawAsync("UPDATE \"Tasks\" SET \"AckedAt\" = clock_timestamp() WHERE \"TaskId\" = {0}", task.TaskId);
                await context.Database.CommitTransactionAsync();
            } catch
            {
                //Handle error.
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
            if (_processing)
            {
                return;
            }

            _processing = true;
            await using var context = await _dbContextFactory.CreateDbContextAsync();

            int page = 1;
            const int pageSize = 5;

            while (true)
            {
                List<OutboxWorkItem>? messages = null;
                try
                {
                    messages = await context.Outbox.Take(pageSize * page)
                    .AsNoTracking()
                    .ToListAsync();
                } catch
                {
                    //Database not ready yet.
                    break;
                }
                

                if (messages == null || messages.Count == 0)
                {
                    break;
                }

                try
                {
                    await SendMessagesToBroker(messages);
                }
                catch (Exception ex)
                {
                    break;
                }
                
                page++;
            }

            _processing = false;
        }

        /// <summary>
        /// Gets a list of stale messages that are fit to be resent to the broker.
        /// </summary>
        /// <returns></returns>
        private async Task<List<OutboxWorkItem>?> GetStaleMessagesToResend()
        {
            List<OutboxWorkItem>? list = null;

            var staleTasks = _unAckedTasks.Where(p => p.Value.SentAt.AddMinutes(_baselineMinutesBeforeResend) < DateTime.UtcNow).ToList();
            var staleIds = staleTasks.Select(t => t.Value.TaskId);

            if (!staleIds.Any())
            {
                return null;
            }

            await using var context = await _dbContextFactory.CreateDbContextAsync();
            var tasks = context.Outbox.Where(t => staleIds.Contains(t.TaskId));
            foreach (var task in tasks)
            {

            }

            return list;
        }

        /// <summary>
        /// Sends a batch of outbox work items to the RabbitMQ broker, ensuring the channel and queue are properly
        /// initialized.
        /// </summary>
        /// <param name="workItems">The list of outbox work items to be sent to the broker.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        /// <exception cref="Exception">Thrown if the connection to RabbitMQ does not exist.</exception>
        private async Task SendMessagesToBroker(List<OutboxWorkItem> workItems)
        {
            await ConfirmRabbitInitialized();

            var batchStartingNumber = await _rabbitChannel!.GetNextPublishSequenceNumberAsync();
            try
            {
                int offset = 0; //Offset caused by existing tasks being skipped.
                for (int i = 0; i < workItems.Count; i++)
                {
                    var workItem = workItems[i];
                    if (_pendingTasks.Contains(workItem.TaskId))
                    {
                        offset++;
                        continue;    
                    }

                    _pendingTasks.Add(workItem.TaskId);
                    _unAckedTasks.Add(batchStartingNumber + (ulong)(i - offset), new PendingTask() { SentAt = DateTime.UtcNow, TaskId = workItem.TaskId });
                    await _rabbitChannel.BasicPublishAsync(exchange: string.Empty, routingKey: "outbox", body: Encoding.UTF8.GetBytes("AHHHH"));
                }
            }
            catch (Exception ex)
            {
                return;
                //Message failed to send across rabbit channel. Potentially add to retry value
            }
        }

        /// <summary>
        /// Sends a batch of outbox work items to the RabbitMQ broker, ensuring the channel and queue are properly
        /// initialized.
        /// </summary>
        /// <param name="workItems">The list of outbox work items to be sent to the broker.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        /// <exception cref="Exception">Thrown if the connection to RabbitMQ does not exist.</exception>
        private async Task ResendMessagesToBroker(List<OutboxWorkItem> workItems)
        {
            await ConfirmRabbitInitialized();

            var batchStartingNumber = await _rabbitChannel!.GetNextPublishSequenceNumberAsync();
            try
            {
                for (int i = 0; i < workItems.Count; i++)
                {
                    var workItem = workItems[i];

                    _unAckedTasks.Add(batchStartingNumber + (ulong)i, new PendingTask() { SentAt = DateTime.UtcNow, TaskId = workItem.TaskId });
                    await _rabbitChannel.BasicPublishAsync(exchange: string.Empty, routingKey: "outbox", body: Encoding.UTF8.GetBytes("AHHHH"));
                }
            }
            catch (Exception ex)
            {
                return;
                //Message failed to send across rabbit channel. Potentially add to retry value
            }
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
                    arguments: new Dictionary<string, object?> { { "x-queue-type", "quorum" } });
                return;
            }

            await Task.Yield();
        }

        #endregion
    }
}
