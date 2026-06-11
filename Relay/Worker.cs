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
using Microsoft.VisualBasic;

namespace Relay
{
    public class Worker : BackgroundService
    {
        #region Constants

        private const int HeartbeatIntervalSeconds = 3;
        private const long LockValue = 0x7E1A7L;

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
        private readonly Timer _heartbeatPollTimer;
        private readonly Timer _heartbeatUpdateTimer;

        private bool _processingOutbox;
        private bool _processingStale;
        private bool _lockAquired;

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
            _lockAquired = false;

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

            _heartbeatPollTimer = new Timer(4000);
            _heartbeatPollTimer.AutoReset = true;
            _heartbeatPollTimer.Enabled = false;
            _heartbeatPollTimer.Elapsed += async (_, _) => await PollHeartbeat();

            _heartbeatUpdateTimer = new Timer(3000);
            _heartbeatUpdateTimer.AutoReset = true;
            _heartbeatUpdateTimer.Enabled = false;
            _heartbeatUpdateTimer.Elapsed += async (_, _) => await WriteHeartbeat();

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
                const string seedSql = "INSERT INTO \"Leader\" (\"Id\", \"PID\", \"LastSeenAt\") VALUES (1, 0, clock_timestamp()) ON CONFLICT (\"Id\") DO NOTHING";
                await using var cmd = new NpgsqlCommand(seedSql, _dbConnection);
                await cmd.ExecuteNonQueryAsync(stoppingToken);
            } catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                //Swallow any exceptions thrown.
            }

            Console.WriteLine("AHHHHHH");
            await TryAquireLock(stoppingToken);
            if (_lockAquired)
            {
                await OnAssignedLeader(stoppingToken);
            } else
            {
                await OnAssignedFollower(stoppingToken);
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
            await using (var cmd = new NpgsqlCommand("", _dbConnection))
            {
                try
                {
                    await cmd.ExecuteNonQueryAsync();
                    _lockAquired = true;
                } catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to aquire lock");
                    _lockAquired = false;
                }
            }
        }

        private async Task WriteHeartbeat()
        {
            await using (var cmd = new NpgsqlCommand("", _dbConnection))
            {
                
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
        /// <param name="stoppingToken">Cancellation token tied to the host lifetime.</param>
        /// <returns>A task that completes once the acquisition attempt has returned.</returns>
        private async Task TryAquireLock(CancellationToken stoppingToken)
        {
            await using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", _dbConnection);
            cmd.Parameters.AddWithValue("key", LockValue);
            _lockAquired = (bool)(await cmd.ExecuteScalarAsync(stoppingToken))!;
        }


        /// <summary>
        /// Explicitly releases the advisory lock identified by <see cref="LockValue"/> on
        /// <see cref="_dbConnection"/>. Used for graceful step-down so another replica can
        /// take over without waiting for TCP keepalives or pg_terminate_backend. Note that
        /// session death (clean shutdown, killed backend, dropped connection) already
        /// releases the lock automatically, so this is only needed when the process intends
        /// to keep its session alive but stop being the leader.
        /// </summary>
        /// <param name="stoppingToken">Cancellation token tied to the host lifetime.</param>
        /// <returns>A task that completes once the unlock statement has returned.</returns>
        private async Task ReleaseLock(CancellationToken stoppingToken)
        {
            await using var cmd = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", _dbConnection);
            cmd.Parameters.AddWithValue("key", LockValue);
            _lockAquired = false;
        }

        
        private async Task OnAssignedLeader(CancellationToken stoppingToken)
        {
            _dbConnection.Notification += OnNotify;
            await using (var cmd = new NpgsqlCommand("LISTEN task_channel", _dbConnection))
            {
                try
                {
                    await cmd.ExecuteNonQueryAsync(stoppingToken);
                } catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to create listener for task_channel");
                }
            }

            await OnProcessOutboxQueue();
            _staleTimer.Enabled = true;
            _outBoxTimer.Enabled = true;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await WriteHeartbeat();
                    await _dbConnection.WaitAsync(TimeSpan.FromSeconds(10), stoppingToken);
                } catch
                {
                    
                }
            }
        }

        private async Task OnAssignedFollower(CancellationToken stoppingToken)
        {
            //_heartbeatPollTimer.Enabled = true;
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
            return;

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
            return;
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
