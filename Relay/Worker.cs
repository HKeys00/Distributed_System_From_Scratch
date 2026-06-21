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

namespace Relay
{
    public class Worker : BackgroundService
    {
        #region Constants

        // private const int HeartbeatIntervalSeconds = 3;
        // private const int HeartbeatStaleSeconds = 5;

        private const int HeartbeatIntervalSeconds = 7;
        private const int HeartbeatStaleSeconds = 21;
        private const long LockValue = 0x7E1A7L;

        #endregion

        #region Fields

        private NpgsqlConnection? _dbConnection;
        private readonly SemaphoreSlim _dbConnectionLock = new(1, 1);
        private IConnection? _rabbitConnection;
        private IChannel? _rabbitChannel;

        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<Worker> _logger;

        private readonly Dictionary<ulong, (IWorkItem item, long publishedAt)> _unAckedTasks;
        private readonly HashSet<Guid> _pendingTasks;


        private readonly Timer _outBoxTimer;
        private readonly Timer _staleTimer;
        private readonly Timer _heartbeatPollTimer;
        //private readonly Timer _heartbeatUpdateTimer;

        private bool _processingOutbox;
        private bool _processingStale;
        private bool _isLeader;

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
            _isLeader = false;

            _outBoxTimer = new Timer(5000);
            _outBoxTimer.AutoReset = true;
            _outBoxTimer.Enabled = false;
            _outBoxTimer.Elapsed += async (_, _) => await TryProcessOutboxQueue();

            _staleTimer = new Timer(10000);
            _staleTimer.AutoReset = true;
            _staleTimer.Enabled = false;
            _staleTimer.Elapsed += async (_, _) => await TryProcessStaleTasks();

            _heartbeatPollTimer = new Timer(HeartbeatStaleSeconds * 1000);
            _heartbeatPollTimer.AutoReset = true;
            _heartbeatPollTimer.Enabled = false;
            _heartbeatPollTimer.Elapsed += async (_, _) => await PollHeartbeat();

            // _heartbeatUpdateTimer = new Timer(3000);
            // _heartbeatUpdateTimer.AutoReset = true;
            // _heartbeatUpdateTimer.Enabled = false;
            // _heartbeatUpdateTimer.Elapsed += async (_, _) => await WriteHeartbeat();

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
            
            _dbConnection = new NpgsqlConnection(_configuration.GetConnectionString("Default"));
            await _dbConnection.OpenAsync(stoppingToken);

            try
            {
                const string seedSql = "INSERT INTO \"Leader\" (\"Id\", \"PID\", \"LastSeenAt\") VALUES (1, 0, 'epoch'::timestamptz) ON CONFLICT (\"Id\") DO NOTHING";
                await using var cmd = new NpgsqlCommand(seedSql, _dbConnection);
                await cmd.ExecuteNonQueryAsync(stoppingToken);
            } catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                //Swallow any exceptions thrown.
            }

            var lockAquired = await TryAquireLock();
            if (lockAquired)
            {
                _logger.LogInformation("Acquired advisory lock on startup, assuming leader role");
                await OnAssignedLeader();
            } else
            {
                _logger.LogInformation("Advisory lock already held, starting as follower");
                _heartbeatPollTimer.Enabled = true;
            }
        }


        /// <summary>
        /// Challenger-side periodic check. Reads the leader heartbeat row and, if it is stale,
        /// terminates the dead leader's backend session and attempts to acquire the advisory lock
        /// to take over leadership. Runs on the same physical session used for LISTEN and the lock,
        /// so any success here is bound to this connection's lifetime.
        /// </summary>
        /// <returns>A task that completes once the heartbeat check (and any takeover attempt) finishes.</returns>
        private async Task PollHeartbeat()
        {
            try
            {
                _logger.LogInformation("Polling leader heartbeat");

                var lockAquired = await TryAquireLock();
                if (lockAquired)
                {
                    _logger.LogInformation("Acquired advisory lock without eviction, promoting to leader");
                    await OnAssignedLeader();
                    return;
                }

                const string evictSql = @"
                    WITH stale AS (
                        SELECT ""PID"" FROM ""Leader""
                        WHERE ""Id"" = 1
                          AND ""PID"" <> 0
                          AND clock_timestamp() - ""LastSeenAt"" > make_interval(secs => @threshold)
                    )
                    SELECT pg_terminate_backend(""PID"") FROM stale";

                int evicted;
                await using (var evict = new NpgsqlCommand(evictSql, _dbConnection))
                {
                    evict.Parameters.AddWithValue("threshold", (double)HeartbeatStaleSeconds);
                    evicted = await evict.ExecuteNonQueryAsync();
                }

                if (evicted > 0)
                {
                    _logger.LogWarning("Detected stale leader, terminated {Count} backend(s)", evicted);
                }

                lockAquired = await TryAquireLock();
                if (lockAquired)
                {
                    _logger.LogInformation("Acquired advisory lock after evicting stale leader, promoting to leader");
                    await OnAssignedLeader();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Heartbeat poll failed");
            }
        }

        /// <summary>
        /// Leader-side heartbeat write. Stamps the singleton Leader row with the current
        /// clock and this session's backend PID so followers can detect liveness and know
        /// which backend to terminate if the leader goes stale.
        /// </summary>
        /// <returns>A task that completes once the update has returned.</returns>
        private async Task WriteHeartbeat()
        {
            await _dbConnectionLock.WaitAsync();
            try
            {
                const string sql = "UPDATE \"Leader\" SET \"LastSeenAt\" = clock_timestamp(), \"PID\" = pg_backend_pid() WHERE \"Id\" = 1";
                await using var cmd = new NpgsqlCommand(sql, _dbConnection);

                try
                {
                    await cmd.ExecuteNonQueryAsync();
                    _logger.LogInformation("Wrote leader heartbeat");
                }
                catch
                {
                    if (_dbConnection?.State != System.Data.ConnectionState.Open)
                    {
                        await OnDemotedToFollower();
                    }
                }
            }
            finally
            {
                _dbConnectionLock.Release();
            }
        }

        /// <summary>
        /// Attempts to acquire the session-level Postgres advisory lock identified by
        /// <see cref="LockValue"/>. Non-blocking: returns immediately with the lock either
        /// acquired or not. The lock is bound to the lifetime of <see cref="_dbConnection"/>
        /// and is released automatically if that session ends for any reason (clean dispose,
        /// pg_terminate_backend, dropped TCP connection). Sets <see cref="_lockAquired"/> to
        /// the boolean result so the main loop can branch into leader or challenger behaviour.
        /// </summary>
        /// <returns>A task that completes once the acquisition attempt has returned.</returns>
        private async Task<bool> TryAquireLock()
        {
            await using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", _dbConnection);
            cmd.Parameters.AddWithValue("key", LockValue);
            return (bool)(await cmd.ExecuteScalarAsync())!;
        }


        /// <summary>
        /// Explicitly releases the advisory lock identified by <see cref="LockValue"/> on
        /// <see cref="_dbConnection"/>. Used for graceful step-down so another replica can
        /// take over without waiting for TCP keepalives or pg_terminate_backend. Note that
        /// session death (clean shutdown, killed backend, dropped connection) already
        /// releases the lock automatically, so this is only needed when the process intends
        /// to keep its session alive but stop being the leader.
        /// </summary>
        /// <returns>A task that completes once the unlock statement has returned.</returns>
        private async Task TryReleaseLock()
        {
            await using var cmd = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", _dbConnection);
            cmd.Parameters.AddWithValue("key", LockValue);

            await OnDemotedToFollower();
        }

        /// <summary>
        /// Leader entry-point. Subscribes to the task_channel LISTEN, kicks off an initial
        /// outbox drain, enables the leader-only timers (outbox, stale reaper), then enters
        /// a loop that writes the heartbeat and waits on notifications. Any failure on the
        /// session is treated as loss of leadership: the lock is released and the loop exits.
        /// </summary>
        /// <returns>A task that completes when leadership is relinquished or the host stops.</returns>
        private async Task OnAssignedLeader()
        {
            _heartbeatPollTimer.Enabled = false;

            if (_dbConnection == null)
            {
                return;
            }

            _logger.LogInformation("Entering leader role");
            // _dbConnection.Notification += OnNotify;
            // await using (var cmd = new NpgsqlCommand("LISTEN task_channel", _dbConnection))
            // {
            //     try
            //     {
            //         await cmd.ExecuteNonQueryAsync();
            //     } catch (Exception ex)
            //     {
            //         _logger.LogError(ex, "Failed to create listener for task_channel");
            //     }
            // }

            await OnProcessOutboxQueue();
            _staleTimer.Enabled = true;
            _outBoxTimer.Enabled = true;
            _isLeader = true;

            while (_isLeader)
            {
                try
                {
                    await WriteHeartbeat();
                    await Task.Delay(TimeSpan.FromSeconds(HeartbeatIntervalSeconds));
                    //await _dbConnection.WaitAsync(TimeSpan.FromSeconds(HeartbeatIntervalSeconds));
                } catch
                {
                    await TryReleaseLock();
                    break;
                }
            }
        }

        /// <summary>
        /// Transition hook invoked when the leader detects it has lost its session (e.g. the
        /// backend was terminated by a challenger via <c>pg_terminate_backend</c>). Disables
        /// leader-only timers, disposes the broken connection, opens a fresh one, and rejoins
        /// the cluster as a follower.
        /// </summary>
        /// <returns>A task that completes once the worker has re-entered the follower state.</returns>
        private async Task OnDemotedToFollower()
        {
            _logger.LogWarning("Demoting to follower, leader session lost");
            
            _outBoxTimer.Enabled = false;
            _staleTimer.Enabled = false;
            _isLeader = false;

            if (_dbConnection != null)
            {
                await _dbConnection.DisposeAsync();
            }

            _dbConnection = new NpgsqlConnection(_configuration.GetConnectionString("Default"));
            await _dbConnection.OpenAsync();
            _heartbeatPollTimer.Enabled = true;
            _logger.LogInformation("Entered follower role");
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
        /// Timer-driven wrapper around <see cref="OnProcessOutboxQueue"/>. Catches any failure
        /// and, if the leader's DB session is no longer open, treats it as lost leadership and
        /// demotes this replica to a follower.
        /// </summary>
        /// <returns>A task that completes once the outbox attempt (and any demotion) has finished.</returns>
        private async Task TryProcessOutboxQueue()
        {
            try
            {
                await OnProcessOutboxQueue();
            } catch
            {
                if (_dbConnection?.State != System.Data.ConnectionState.Open)
                {
                    await OnDemotedToFollower();
                }
            }
        }

        /// <summary>
        /// Timer-driven wrapper around the stale-task sweep. Catches any failure and demotes
        /// this replica to a follower, on the assumption that a failure here means the leader
        /// session is no longer healthy.
        /// </summary>
        /// <returns>A task that completes once the stale sweep (and any demotion) has finished.</returns>
        private async Task TryProcessStaleTasks()
        {
            try
            {
                await OnProcessStaleTasks();
            } catch
            {
                if (_dbConnection?.State != System.Data.ConnectionState.Open)
                {
                    await OnDemotedToFollower();
                }
            }
        }

        /// <summary>
        /// Processes messages from the outbox queue in batches and sends them to the broker.
        /// </summary>
        /// <returns>A task representing the asynchronous operation.</returns>
        private async Task OnProcessOutboxQueue()
        {
            if (_processingOutbox || _dbConnection == null)
            {
                return;
            }

            _processingOutbox = true;

            try
            {
                long depth;
                await using (var countCmd = new NpgsqlCommand("SELECT COUNT(*) FROM outbox", _dbConnection))
                {
                    depth = (long)(await countCmd.ExecuteScalarAsync())!;
                }
                AppMetrics.Relay.OutboxDepth.Set(depth);

                DateTime? oldest;
                await using (var oldestCmd = new NpgsqlCommand("SELECT MIN(\"CreatedAt\") FROM outbox", _dbConnection))
                {
                    var result = await oldestCmd.ExecuteScalarAsync();
                    oldest = result is null or DBNull ? null : (DateTime?)result;
                }
                AppMetrics.Relay.OutboxOldestUnpublishedSeconds.Set(
                    oldest is null ? 0 : (DateTime.UtcNow - oldest.Value).TotalSeconds);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read outbox metrics");
            }

            int page = 1;
            const int pageSize = 5;

            while (true)
            {
                List<OutboxWorkItem> tasks = new();

                try
                {
                    await using var readCmd = new NpgsqlCommand("SELECT * FROM outbox ORDER BY \"Id\" LIMIT @limit",_dbConnection);
                    readCmd.Parameters.AddWithValue("limit", pageSize * page);
                    await using var reader = await readCmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        tasks.Add(new OutboxWorkItem
                        {
                            Id = reader.GetInt64(reader.GetOrdinal("Id")),
                            TaskId = reader.GetGuid(reader.GetOrdinal("TaskId")),
                            CorrelationId = reader.GetGuid(reader.GetOrdinal("CorrelationId")),
                            IdempotencyId = reader.GetString(reader.GetOrdinal("IdempotencyId")),
                            Url = reader.GetString(reader.GetOrdinal("Url")),
                            CreatedAt = reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
                            SentAt = reader.IsDBNull(reader.GetOrdinal("SentAt")) ? null : reader.GetDateTime(reader.GetOrdinal("SentAt")),
                            PublishedAt = reader.IsDBNull(reader.GetOrdinal("PublishedAt")) ? null : reader.GetDateTime(reader.GetOrdinal("PublishedAt")),
                            NextAttemptAt = reader.IsDBNull(reader.GetOrdinal("NextAttemptAt")) ? null : reader.GetDateTime(reader.GetOrdinal("NextAttemptAt")),
                            Attempt = reader.GetInt32(reader.GetOrdinal("Attempt"))
                        });
                    }
                }
                catch (Exception ex)
                {
                    //Database not ready yet.
                    _logger.LogError(ex, "Error trying to read outbox, skipping process");
                    break;
                }

                if (tasks.Count == 0)
                {
                    break;
                }

                try
                {
                    var ids = await SendMessagesToBroker(tasks);
                    await using var tx = await _dbConnection.BeginTransactionAsync();
                    foreach (var id in ids)
                    {
                        await using var updateCmd = new NpgsqlCommand(
                            "UPDATE \"Tasks\" SET \"SentAt\" = clock_timestamp() WHERE \"TaskId\" = @taskId",
                            _dbConnection, tx);
                        updateCmd.Parameters.AddWithValue("taskId", id);
                        await updateCmd.ExecuteNonQueryAsync();
                    }
                    await tx.CommitAsync();
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
            if (_processingStale || _dbConnection == null)
            {
                return;
            }

            _processingStale = true;

            try
            {
                long depth;
                await using (var countCmd = new NpgsqlCommand("SELECT COUNT(*) FROM staletasks", _dbConnection))
                {
                    depth = (long)(await countCmd.ExecuteScalarAsync())!;
                }
                AppMetrics.Relay.StaleDepth.Set(depth);

                DateTime? oldest;
                await using (var oldestCmd = new NpgsqlCommand("SELECT MIN(\"SentAt\") FROM staletasks", _dbConnection))
                {
                    var result = await oldestCmd.ExecuteScalarAsync();
                    oldest = result is null or DBNull ? null : (DateTime?)result;
                }
                AppMetrics.Relay.StaleOldestSeconds.Set(oldest is null ? 0 : (DateTime.UtcNow - oldest.Value).TotalSeconds);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read stale-task metrics");
            }

            int page = 1;
            const int pageSize = 5;

            while (true)
            {
                List<StaleWorkItem> staleTasks = new();

                try
                {
                    await using var readCmd = new NpgsqlCommand("SELECT * FROM staletasks ORDER BY \"Id\" LIMIT @limit",_dbConnection);
                    readCmd.Parameters.AddWithValue("limit", pageSize * page);
                    await using var reader = await readCmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        staleTasks.Add(new StaleWorkItem
                        {
                            Id = reader.GetInt64(reader.GetOrdinal("Id")),
                            TaskId = reader.GetGuid(reader.GetOrdinal("TaskId")),
                            CorrelationId = reader.GetGuid(reader.GetOrdinal("CorrelationId")),
                            IdempotencyId = reader.GetString(reader.GetOrdinal("IdempotencyId")),
                            Url = reader.GetString(reader.GetOrdinal("Url")),
                            CreatedAt = reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
                            SentAt = reader.IsDBNull(reader.GetOrdinal("SentAt")) ? null : reader.GetDateTime(reader.GetOrdinal("SentAt")),
                            PublishedAt = reader.IsDBNull(reader.GetOrdinal("PublishedAt")) ? null : reader.GetDateTime(reader.GetOrdinal("PublishedAt")),
                            NextAttemptAt = reader.IsDBNull(reader.GetOrdinal("NextAttemptAt")) ? null : reader.GetDateTime(reader.GetOrdinal("NextAttemptAt")),
                            Attempt = reader.GetInt32(reader.GetOrdinal("Attempt"))
                        });
                    }
                }
                catch (Exception ex)
                {
                    //Database not ready yet.
                    _logger.LogError(ex, "Error trying to read {QueueName}, skipping process", "staletasks");
                    break;
                }

                if (staleTasks.Count == 0)
                {
                    break;
                }

                try
                {
                    var ids = await SendMessagesToBroker(staleTasks);
                    await using var tx = await _dbConnection.BeginTransactionAsync();
                    foreach (var staleTask in staleTasks.Where(t => ids.Contains(t.TaskId)))
                    {
                        await using (var updateCmd = new NpgsqlCommand(
                            "UPDATE \"Tasks\" SET \"SentAt\" = clock_timestamp() WHERE \"TaskId\" = @taskId",
                            _dbConnection, tx))
                        {
                            updateCmd.Parameters.AddWithValue("taskId", staleTask.TaskId);
                            await updateCmd.ExecuteNonQueryAsync();
                        }

                        //Raising conflict for stale task retry.
                        await using var insertCmd = new NpgsqlCommand(
                            "INSERT INTO \"Conflicts\" (\"TaskId\", \"CorrelationId\", \"IdempotencyId\", \"Reason\", \"Attempt\") VALUES (@taskId, @correlationId, @idempotencyId, @reason, @attempt)",
                            _dbConnection, tx);
                        insertCmd.Parameters.AddWithValue("taskId", staleTask.TaskId);
                        insertCmd.Parameters.AddWithValue("correlationId", staleTask.CorrelationId);
                        insertCmd.Parameters.AddWithValue("idempotencyId", staleTask.IdempotencyId);
                        insertCmd.Parameters.AddWithValue("reason", "Stale reaper picked up and resubmitted task.");
                        insertCmd.Parameters.AddWithValue("attempt", staleTask.Attempt);
                        await insertCmd.ExecuteNonQueryAsync();
                    }
                    await tx.CommitAsync();
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
                    _logger.LogWarning("CorrelationId={CorrelationId} TaskId={TaskId} Published task to broker",
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
