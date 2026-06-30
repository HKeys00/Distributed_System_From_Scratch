using Data;
using Data.Models.Task;
using Shared.Constants;
using Shared.DTOs;
using Shared.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Timer = System.Timers.Timer;
using Data.Models.Status;

namespace Relay
{
    public class Worker : BackgroundService
    {
        #region Constants

        private const int HeartbeatIntervalSeconds = 10;
        private const int HeartbeatStaleSeconds = 1;

        #endregion

        #region Fields

        private NpgsqlConnection? _dbConnection;
        private IConnection? _rabbitConnection;
        private IChannel? _rabbitChannel;

        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
        private readonly IConfiguration _configuration;
        private readonly ILogger<Worker> _logger;

        private readonly ConcurrentDictionary<ulong, (IWorkItem item, long publishedAt)> _unAckedTasks;

        private readonly Timer _outBoxTimer;
        private readonly Timer _staleTimer;
        private readonly Timer _pollTimer;

        private bool _processingOutbox;
        private bool _processingStale;
        private bool _isLeader;
        private long _myToken;
        private CancellationTokenSource? _leaderLoopCts;

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

            _pollTimer = new Timer(HeartbeatIntervalSeconds * 1000);
            _pollTimer.AutoReset = true;
            _pollTimer.Enabled = true;
            _pollTimer.Elapsed += async (_, _) => await TryClaimLeadership();

            _unAckedTasks = new ConcurrentDictionary<ulong, (IWorkItem, long)>();
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
        /// Host shutdown hook. Demotes eagerly so the leader heartbeat loop's
        /// <c>while (_isLeader)</c> condition flips before <see cref="BackgroundService.StopAsync"/>
        /// cancels <c>stoppingToken</c>. Without this, an <see cref="ExecuteAsync"/> that's
        /// stuck inside <see cref="OnAssignedLeader"/>'s loop never reaches the cancellation
        /// catch and gets SIGKILLed when the Docker grace period expires.
        /// </summary>
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Stop signal received, beginning graceful shutdown");
            if (_isLeader)
            {
                await OnDemotedToFollower();
            }
            await base.StopAsync(cancellationToken);
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
            _leaderLoopCts = new CancellationTokenSource();

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
                    await _dbConnection.WaitAsync(TimeSpan.FromSeconds(HeartbeatIntervalSeconds), _leaderLoopCts.Token);
                    await WriteHeartbeat();
                }
                catch (OperationCanceledException)
                {
                    break;
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
        /// also releases the LISTEN), and re-enables the follower poll. If our token is
        /// still the current one in the DB, backdate <c>LastSeenAt</c> so the next
        /// candidate can claim immediately instead of waiting out the stale interval.
        /// </summary>
        private async Task OnDemotedToFollower()
        {
            var prevToken = _myToken;
            _logger.LogWarning("Demoting from LEADER role (was token={Token})", prevToken);

            _isLeader = false;
            _myToken = 0;
            _leaderLoopCts?.Cancel();

            _outBoxTimer.Enabled = false;
            _staleTimer.Enabled = false;
            _pollTimer.Enabled = true;

            await using (var context = await _dbContextFactory.CreateDbContextAsync())
            {
                try
                {
                    var rows = await context.Database.ExecuteSqlRawAsync(
                        "UPDATE \"Leader\" SET \"LastSeenAt\" = 'epoch'::timestamptz WHERE \"Id\" = 1 AND \"Token\" = {0}",
                        prevToken);
                    if (rows == 1)
                    {
                        _logger.LogInformation("Relinquished leadership cleanly (token={Token}) - LastSeenAt backdated", prevToken);
                    }
                    else
                    {
                        _logger.LogInformation("Skipped LastSeenAt clear, token={Token} no longer current", prevToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to clear LastSeenAt on demotion (token={Token})", prevToken);
                }
            }

            if (_dbConnection is not null)
            {
                _dbConnection.Notification -= OnNotify;
                await _dbConnection.DisposeAsync();
                _dbConnection = null;
                _logger.LogInformation("Disposed leader DB session and unsubscribed from task_channel");
            }

            _leaderLoopCts?.Dispose();
            _leaderLoopCts = null;

            _logger.LogWarning("Now FOLLOWER - poll timer re-enabled (interval={Interval}s)", HeartbeatIntervalSeconds);
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
            var drained = DrainConfirmedTags(args.DeliveryTag, args.Multiple);
            if (drained.Count == 0)
            {
                await Task.Yield();
                return;
            }

            await using var context = await _dbContextFactory.CreateDbContextAsync();
            foreach (var (_, entry) in drained)
            {
                var (task, publishedAt) = entry;
                AppMetrics.Relay.OutboxPublishAckSeconds.Observe(Stopwatch.GetElapsedTime(publishedAt).TotalSeconds);

                using var taskScope = _logger.BeginScope(new Dictionary<string, object>
                {
                    [CorrelationConstants.LogScopeKey] = task.CorrelationId,
                    ["TaskId"] = task.TaskId
                });

                try
                {
                    await context.Database.BeginTransactionAsync();
                    await context.Database.ExecuteSqlRawAsync("UPDATE \"Tasks\" SET \"PublishedAt\" = clock_timestamp() WHERE \"TaskId\" = {0}", task.TaskId);
                    await context.Database.CommitTransactionAsync();
                    _logger.LogInformation("CorrelationId={CorrelationId} TaskId={TaskId} Task received by broker",
                        task.CorrelationId, task.TaskId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "CorrelationId={CorrelationId} TaskId={TaskId} Failed to mark task as published",
                        task.CorrelationId, task.TaskId);
                }
            }
        }

        /// <summary>
        /// Handles a broker nack for a previously published outbox message. Drops the
        /// in-memory tracking so the slot doesn't leak; the row's <c>PublishedAt</c> stays
        /// null so the stale reaper will eventually re-dispatch it.
        /// </summary>
        private async Task OnNackReturn(object sender, BasicNackEventArgs args)
        {
            var drained = DrainConfirmedTags(args.DeliveryTag, args.Multiple);
            if (drained.Count == 0)
            {
                await Task.Yield();
                return;
            }

            foreach (var (_, entry) in drained)
            {
                AppMetrics.Relay.OutboxNacks.Inc();
                var (task, _) = entry;
                _logger.LogWarning("CorrelationId={CorrelationId} TaskId={TaskId} Broker nacked publish - stale reaper will retry",
                    task.CorrelationId, task.TaskId);
            }
            await Task.Yield();
        }

        /// <summary>
        /// Removes and returns the set of un-acked entries covered by a broker confirm.
        /// When <paramref name="multiple"/> is true the broker is confirming every delivery
        /// tag up to and including <paramref name="deliveryTag"/> in a single frame; otherwise
        /// only the exact tag is drained.
        /// </summary>
        private List<(ulong tag, (IWorkItem item, long publishedAt) entry)> DrainConfirmedTags(ulong deliveryTag, bool multiple)
        {
            var drained = new List<(ulong, (IWorkItem, long))>();
            if (multiple)
            {
                foreach (var tag in _unAckedTasks.Keys.Where(t => t <= deliveryTag).ToList())
                {
                    if (_unAckedTasks.TryRemove(tag, out var entry))
                    {
                        drained.Add((tag, entry));
                    }
                }
            }
            else if (_unAckedTasks.TryRemove(deliveryTag, out var entry))
            {
                drained.Add((deliveryTag, entry));
            }
            return drained;
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
                bool tokenLost = false;

                try
                {
                    await using var fetchTx = await context.Database.BeginTransactionAsync();
                    if (!await TryLockLeaderRowAsync(context, fetchTx))
                    {
                        tokenLost = true;
                        tasks = new List<OutboxWorkItem>();
                    }
                    else
                    {
                        tasks = await context.Outbox.OrderBy(t => t.Id).Take(pageSize * page).AsNoTracking().ToListAsync();
                        await fetchTx.CommitAsync();
                    }
                }
                catch (Exception ex)
                {
                    //Database not ready yet.
                    _logger.LogError(ex, "Error trying to read {QueueName}, skipping process", nameof(context.Outbox));
                    break;
                }

                if (tokenLost)
                {
                    _logger.LogWarning("Outbox processing aborted - token {Token} no longer current", _myToken);
                    _processingOutbox = false;
                    await OnDemotedToFollower();
                    return;
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
                        var rows = await context.Database.ExecuteSqlRawAsync("UPDATE \"Tasks\" SET \"SentAt\" = clock_timestamp(), \"SentByToken\" = {0} WHERE \"SentByToken\" <= {0} AND \"TaskId\" = {1}", _myToken, id);
                        if (rows == 0)
                        {
                            AppMetrics.Relay.StaleTokenTaskUpdates.Inc();
                            _logger.LogWarning("Outbox task update rejected - token {Token} superseded (TaskId={TaskId})", _myToken, id);
                            tokenLost = true;
                            break;
                        }
                    }
                    await context.Database.CommitTransactionAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error occurred while sending outbox tasks");
                    break;
                }

                if (tokenLost)
                {
                    _processingOutbox = false;
                    await OnDemotedToFollower();
                    return;
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
                bool tokenLost = false;

                try
                {
                    await using var fetchTx = await context.Database.BeginTransactionAsync();
                    if (!await TryLockLeaderRowAsync(context, fetchTx))
                    {
                        tokenLost = true;
                        staleTasks = new List<StaleWorkItem>();
                    }
                    else
                    {
                        staleTasks = await context.StaleTasks.OrderBy(t => t.Id).Take(pageSize * page).AsNoTracking().ToListAsync();
                        await fetchTx.CommitAsync();
                    }
                }
                catch (Exception ex)
                {
                    //Database not ready yet.
                    _logger.LogError(ex, "Error trying to read {QueueName}, skipping process", nameof(context.StaleTasks));
                    break;
                }

                if (tokenLost)
                {
                    _logger.LogWarning("Stale processing aborted - token {Token} no longer current", _myToken);
                    _processingStale = false;
                    await OnDemotedToFollower();
                    return;
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
                        var rows = await context.Database.ExecuteSqlRawAsync("UPDATE \"Tasks\" SET \"SentAt\" = clock_timestamp(), \"SentByToken\" = {0} WHERE \"SentByToken\" <= {0} AND \"TaskId\" = {1}", _myToken, id);
                        if (rows == 0)
                        {
                            AppMetrics.Relay.StaleTokenTaskUpdates.Inc();
                            _logger.LogWarning("Stale task update rejected - token {Token} superseded (TaskId={TaskId})", _myToken, id);
                            tokenLost = true;
                            break;
                        }
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

                if (tokenLost)
                {
                    _processingStale = false;
                    await OnDemotedToFollower();
                    return;
                }

                page++;
            }

            _processingStale = false;
        }
        
        /// <summary>
        /// Acquires a row lock on the singleton Leader row scoped to <see cref="_myToken"/>.
        /// Returns true if we still hold leadership, false if our token has been superseded.
        /// The caller releases the lock by committing or rolling back <paramref name="transaction"/>.
        /// While the lock is held, concurrent <c>TryClaimLeadership</c> attempts block on the
        /// row, preventing a new leader from being elected during the fetch.
        /// </summary>
        private async Task<bool> TryLockLeaderRowAsync(ApplicationDbContext context, IDbContextTransaction transaction)
        {
            var conn = (NpgsqlConnection)context.Database.GetDbConnection();
            var tx = (NpgsqlTransaction)transaction.GetDbTransaction();
            await using var cmd = new NpgsqlCommand(
                "SELECT \"Token\" FROM \"Leader\" WHERE \"Id\" = 1 AND \"Token\" = @token FOR UPDATE",
                conn, tx);
            cmd.Parameters.AddWithValue("token", _myToken);
            var result = await cmd.ExecuteScalarAsync();
            return result is not null and not DBNull;
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
                _unAckedTasks.TryAdd(deliveryTag, (workItem, Stopwatch.GetTimestamp()));
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
                    _unAckedTasks.TryRemove(deliveryTag, out _);
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
                _rabbitChannel = await _rabbitConnection.CreateChannelAsync(new CreateChannelOptions(true, false));
                _rabbitChannel.BasicAcksAsync += OnAckReturn;
                _rabbitChannel.BasicNacksAsync += OnNackReturn;

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
