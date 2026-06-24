using Data;
using Data.Models.Task;
using Shared.Constants;
using Shared.DTOs;
using Shared.Helpers;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Diagnostics;
using System.Text.Json;
using Timer = System.Timers.Timer;
using Data.Models.Status;

namespace Relay
{
    public class Worker : BackgroundService
    {
        #region Constants

        private const int HeartbeatIntervalSeconds = 5;
        private const int HeartbeatStaleSeconds = 15;
        private const int PollIntervalSeconds = 5;

        #endregion

        #region Fields

        private NpgsqlConnection? _dbConnection;
        private IConnection? _rabbitConnection;
        private IChannel? _rabbitChannel;

        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<Worker> _logger;

        private readonly Dictionary<ulong, (IWorkItem item, long publishedAt)> _unAckedTasks;
        private readonly HashSet<Guid> _pendingTasks;

        private readonly Timer _outBoxTimer;
        private readonly Timer _staleTimer;
        private readonly Timer _pollTimer;

        private bool _processingOutbox;
        private bool _processingStale;
        private bool _isLeader;
        private long _myToken;

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
            _outBoxTimer.Enabled = false;
            _outBoxTimer.Elapsed += async (_, _) => await OnProcessOutboxQueue();

            _staleTimer = new Timer(10000);
            _staleTimer.AutoReset = true;
            _staleTimer.Enabled = false;
            _staleTimer.Elapsed += async (_, _) => await OnProcessStaleTasks();

            _pollTimer = new Timer(PollIntervalSeconds * 1000);
            _pollTimer.AutoReset = true;
            _pollTimer.Enabled = true;
            _pollTimer.Elapsed += async (_, _) => await TryClaimLeadership();

            _unAckedTasks = new Dictionary<ulong, (IWorkItem, long)>();
            _pendingTasks = new HashSet<Guid>();
        }

        #endregion

        #region Methods

        /// <inheritdoc />
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var factory = new ConnectionFactory() { HostName = "rabbitmq", Port = 5672 };
            _rabbitConnection = await factory.CreateConnectionAsync(stoppingToken);

            await SeedLeaderRow(stoppingToken);
            await TryClaimLeadership();

            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException) { }

            if (_isLeader)
            {
                await OnDemotedToFollower();
            }
        }

        /// <summary>
        /// Idempotently seeds the singleton Leader row with <c>Id=1</c>, <c>Token=0</c>
        /// and an epoch <c>LastSeenAt</c> so the first claim attempt has something to match.
        /// </summary>
        private async Task SeedLeaderRow(CancellationToken cancellationToken)
        {
            const string sql = "INSERT INTO \"Leader\" (\"Id\", \"Token\", \"LastSeenAt\") VALUES (1, 0, 'epoch'::timestamptz) ON CONFLICT (\"Id\") DO NOTHING";

            await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            try
            {
                await context.Database.ExecuteSqlRawAsync(sql, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to seed Leader row");
            }
        }

        /// <summary>
        /// Leader-side heartbeat write. Refreshes <c>LastSeenAt</c> on the singleton
        /// Leader row, scoped by <c>Token = _myToken</c> so a deposed leader's heartbeat
        /// hits 0 rows and triggers self-demotion.
        /// </summary>
        /// <returns>A task that completes once the update has returned.</returns>
        private async Task WriteHeartbeat()
        {
            if (!_isLeader)
            {
                return;
            }

            const string sql = "UPDATE \"Leader\" SET \"LastSeenAt\" = clock_timestamp() WHERE \"Id\" = 1 AND \"Token\" = {0}";

            await using var context = await _dbContextFactory.CreateDbContextAsync();
            try
            {
                var rows = await context.Database.ExecuteSqlRawAsync(sql, _myToken);
                if (rows == 0)
                {
                    _logger.LogWarning("Heartbeat affected 0 rows, leadership lost (myToken={MyToken})", _myToken);
                    await OnDemotedToFollower();
                    return;
                }
                _logger.LogInformation("Heartbeat OK, token={Token}", _myToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write leader heartbeat (token={Token})", _myToken);
            }
        }

        /// <summary>
        /// Follower-side election attempt. Atomically bumps the fencing token and
        /// refreshes <c>LastSeenAt</c> iff the current leader has gone stale
        /// (<c>LastSeenAt &lt; now() - HeartbeatStaleSeconds</c>). On success transitions
        /// this replica into the leader role.
        /// </summary>
        /// <returns>The new fencing token if leadership was claimed, null otherwise.</returns>
        private async Task<long?> TryClaimLeadership()
        {
            if (_isLeader)
            {
                return null;
            }

            const string sql = @"
                UPDATE ""Leader""
                SET ""Token"" = ""Token"" + 1, ""LastSeenAt"" = clock_timestamp()
                WHERE ""Id"" = 1 AND ""LastSeenAt"" < now() - make_interval(secs => @stale)
                RETURNING ""Token""";

            await using var context = await _dbContextFactory.CreateDbContextAsync();
            try
            {
                var conn = (NpgsqlConnection)context.Database.GetDbConnection();
                if (conn.State != System.Data.ConnectionState.Open)
                {
                    await conn.OpenAsync();
                }

                await using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("stale", (double)HeartbeatStaleSeconds);

                var result = await cmd.ExecuteScalarAsync();
                if (result is null or DBNull)
                {
                    _logger.LogInformation("Leadership claim declined - current leader still alive");
                    return null;
                }

                var token = (long)result;
                _logger.LogInformation("Leadership claim won - current leader was stale, new token={Token}", token);
                await OnAssignedLeader(token);
                return token;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to attempt leadership claim");
                return null;
            }
        }

        /// <summary>
        /// Transitions this replica into the leader role: caches the fencing token,
        /// disables the follower poll, enables leader-side timers, opens a dedicated
        /// Postgres session for <c>LISTEN task_channel</c>, drains the outbox, and
        /// runs the wait/heartbeat loop until demotion disposes the connection.
        /// </summary>
        private async Task OnAssignedLeader(long token)
        {
            _myToken = token;
            _isLeader = true;

            _pollTimer.Enabled = false;
            _outBoxTimer.Enabled = true;
            _staleTimer.Enabled = true;

            _logger.LogInformation("Promoted to LEADER (token={Token}) - enabling outbox/stale timers, disabling poll", token);

            _dbConnection = new NpgsqlConnection(_configuration.GetConnectionString("Default"));
            await _dbConnection.OpenAsync();
            _dbConnection.Notification += OnNotify;

            await using (var listenCmd = new NpgsqlCommand("LISTEN task_channel", _dbConnection))
            {
                try
                {
                    await listenCmd.ExecuteNonQueryAsync();
                    _logger.LogInformation("Subscribed to task_channel notifications");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to create listener for task_channel");
                }
            }

            await OnProcessOutboxQueue();

            _logger.LogInformation("Entering leader heartbeat loop (interval={Interval}s)", HeartbeatIntervalSeconds);
            while (_isLeader)
            {
                try
                {
                    await _dbConnection.WaitAsync(TimeSpan.FromSeconds(HeartbeatIntervalSeconds));
                    await WriteHeartbeat();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Leader loop iteration failed (token={Token})", _myToken);
                    break;
                }
            }
            _logger.LogInformation("Exited leader heartbeat loop");
        }

        /// <summary>
        /// Transitions this replica back to the follower role: clears the cached token,
        /// disables leader-side timers, disposes the dedicated leader connection (which
        /// also releases the LISTEN), and re-enables the follower poll.
        /// </summary>
        private async Task OnDemotedToFollower()
        {
            var prevToken = _myToken;
            _logger.LogWarning("Demoting from LEADER role (was token={Token})", prevToken);

            _isLeader = false;
            _myToken = 0;

            _outBoxTimer.Enabled = false;
            _staleTimer.Enabled = false;
            _pollTimer.Enabled = true;

            if (_dbConnection is not null)
            {
                _dbConnection.Notification -= OnNotify;
                await _dbConnection.DisposeAsync();
                _dbConnection = null;
                _logger.LogInformation("Disposed leader DB session and unsubscribed from task_channel");
            }

            _logger.LogWarning("Now FOLLOWER - poll timer re-enabled (interval={Interval}s)", PollIntervalSeconds);
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
            using var deliveryScope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["DeliveryTag"] = args.DeliveryTag
            });

            var removed = _unAckedTasks.Remove(args.DeliveryTag, out var entry);
            if (!removed)
            {
                //Acking a task that has already either been re sent or marked as acked, either way ignore and keep going.
                //_logger.LogWarning("Ack received for task that has already been resent or marked as acked");
                await Task.Yield();
                return;
            }

            var (task, publishedAt) = entry;
            AppMetrics.Relay.OutboxPublishAckSeconds.Observe(Stopwatch.GetElapsedTime(publishedAt).TotalSeconds);

            using var taskScope = _logger.BeginScope(new Dictionary<string, object>
            {
                [CorrelationConstants.LogScopeKey] = task.CorrelationId,
                ["TaskId"] = task.TaskId
            });

            await using var context = await _dbContextFactory.CreateDbContextAsync();
            try
            {
                await context.Database.BeginTransactionAsync();
                await context.Database.ExecuteSqlRawAsync("UPDATE \"Tasks\" SET \"PublishedAt\" = clock_timestamp() WHERE \"TaskId\" = {0}", task.TaskId);
                await context.Database.CommitTransactionAsync();
                _logger.LogInformation("CorrelationId={CorrelationId} TaskId={TaskId} Task received by broker",
                    task.CorrelationId, task.TaskId);
            } catch (Exception ex)
            {
                _logger.LogError(ex, "CorrelationId={CorrelationId} TaskId={TaskId} Failed to mark task as published",
                    task.CorrelationId, task.TaskId);
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

            try
            {
                AppMetrics.Relay.OutboxDepth.Set(await context.Outbox.CountAsync());
                var oldest = await context.Outbox.MinAsync(t => (DateTime?)t.CreatedAt);
                AppMetrics.Relay.OutboxOldestUnpublishedSeconds.Set(
                    oldest is null ? 0 : (DateTime.UtcNow - oldest.Value).TotalSeconds);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read outbox metrics");
            }

            int page = 1;
            const int pageSize = 5;

            while (true)
            {
                List<OutboxWorkItem> tasks;

                try
                {
                    tasks = await context.Outbox.OrderBy(t => t.Id).Take(pageSize * page).AsNoTracking().ToListAsync();
                } 
                catch (Exception ex)
                {
                    //Database not ready yet.
                    _logger.LogError(ex, "Error trying to read {QueueName}, skipping process", nameof(context.Outbox));
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
                    _logger.LogError(ex, "Unexpected error occurred while sending outbox tasks");
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

            try
            {
                AppMetrics.Relay.StaleDepth.Set(await context.StaleTasks.CountAsync());
                var oldest = await context.StaleTasks.MinAsync(t => t.SentAt);
                AppMetrics.Relay.StaleOldestSeconds.Set(
                    oldest is null ? 0 : (DateTime.UtcNow - oldest.Value).TotalSeconds);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read stale-task metrics");
            }

            int page = 1;
            const int pageSize = 5;

            while (true)
            {
                List<StaleWorkItem> staleTasks;

                try
                {
                    staleTasks = await context.StaleTasks.OrderBy(t => t.Id).Take(pageSize * page).AsNoTracking().ToListAsync();
                }
                catch (Exception ex)
                {
                    //Database not ready yet.
                    _logger.LogError(ex, "Error trying to read {QueueName}, skipping process", nameof(context.StaleTasks));
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
                        await context.Database.ExecuteSqlRawAsync("UPDATE \"Tasks\" SET \"SentAt\" = clock_timestamp() WHERE \"TaskId\" = {0}", id);
                        //Raising conflict for stale task retry.

                        var job = await context.Tasks.FirstOrDefaultAsync(t => t.TaskId == id);
                        if (job == null)
                        {
                            _logger.LogError("Failed to resubmit task {id}, task doesn't exist", id);
                            continue;
                        }

                        context.Add(new Conflict()
                        {
                            TaskId = job.TaskId,
                            CorrelationId = job.CorrelationId,
                            IdempotencyId = job.IdempotencyId,
                            Reason = "Stale reaper picked up and resubmitted task.",
                            Attempt = job.Attempt
                        });
                    }
                    await context.Database.CommitTransactionAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error occurred while sending stale tasks");
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
                var message = new CrawlMessage(workItem.TaskId, workItem.CorrelationId, workItem.IdempotencyId, workItem.Url, workItem.Attempt);

                using var itemScope = _logger.BeginScope(new Dictionary<string, object>
                {
                    [CorrelationConstants.LogScopeKey] = workItem.CorrelationId,
                    ["TaskId"] = workItem.TaskId,
                    ["IdempotencyId"] = workItem.IdempotencyId,
                    ["Url"] = workItem.Url,
                    ["Attempt"] = workItem.Attempt
                });

                var deliveryTag = batchStartingNumber + (ulong)i;
                _unAckedTasks.Add(deliveryTag, (workItem, Stopwatch.GetTimestamp()));
                try
                {
                    await _rabbitChannel.BasicPublishAsync(exchange: string.Empty, routingKey: "outbox",
                        body: JsonSerializer.SerializeToUtf8Bytes(message));
                    AppMetrics.Relay.OutboxPublishes.WithLabels("success").Inc();
                    _logger.LogDebug("CorrelationId={CorrelationId} TaskId={TaskId} Published task to broker",
                        workItem.CorrelationId, workItem.TaskId);
                }
                catch (Exception ex)
                {
                    _unAckedTasks.Remove(deliveryTag);
                    AppMetrics.Relay.OutboxPublishes.WithLabels("fail").Inc();
                    _logger.LogError(ex, "CorrelationId={CorrelationId} TaskId={TaskId} Message failed to send across rabbit channel",
                        workItem.CorrelationId, workItem.TaskId);
                    continue;
                }
                sentMessages.Add(workItem.TaskId);
            }

            AppMetrics.Relay.OutboxPublishBatchSize.Observe(workItems.Count);
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
