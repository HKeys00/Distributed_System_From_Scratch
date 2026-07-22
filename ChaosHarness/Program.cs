using ChaosHarness;

const string ConnectionString = "Host=localhost;Port=5432;Database=mydatabase;Username=myuser;Password=mypassword";
const string ControllerBase = "http://localhost:5001";
const string StubBaseFromContainer = "http://host.docker.internal:6000";
const int StubPort = 6000;
const string WorkerService = "worker-node";
const string RelayService = "relay";
const string BrokerService = "rabbitmq";

var full = args.Contains("--full");

await using var stub = new StubTargetServer(StubPort);
stub.Start();
Console.WriteLine($"[stub] listening on :{StubPort}");

var db = new DbAccess(ConnectionString);
var workload = new Workload(ControllerBase);

// Every scenario below except worker_scaling assumes a single worker (rate_limit_same_domain
// in particular). Reset in case a previous run was aborted mid-scale.
Console.WriteLine($"[setup] scaling {WorkerService} to 1 replica");
await FaultInjector.ScaleServiceAsync(WorkerService, 1);
await FaultInjector.WaitForServiceContainersAsync(WorkerService, 1, TimeSpan.FromSeconds(60));

var failures = 0;

failures += await Run("baseline_ok", () => BaselineAsync(
    db, workload, count: 100, urlTemplate: $"{StubBaseFromContainer}/ok?n={{n}}",
    expect: Expectation.AllSuccess, timeout: TimeSpan.FromMinutes(5)));

failures += await Run("baseline_500", () => BaselineAsync(
    db, workload, count: 5, urlTemplate: $"{StubBaseFromContainer}/500?n={{n}}",
    expect: Expectation.AllDeadLettered, timeout: TimeSpan.FromMinutes(10)));

failures += await Run("chaos_sigkill", () => ChaosSigkillAsync(
    db, workload, count: 100, urlTemplate: $"{StubBaseFromContainer}/ok?n={{n}}",
    timeout: TimeSpan.FromMinutes(5)));

failures += await Run("chaos_sigterm_graceful_shutdown", () => ChaosSigtermGracefulAsync(
    db, workload, count: 50, urlTemplate: $"{StubBaseFromContainer}/ok?n={{n}}",
    timeout: TimeSpan.FromMinutes(5)));

failures += await Run("chaos_sigkill_leader", () => ChaosSigkillLeaderAsync(
    db, workload, count: 50, urlTemplate: $"{StubBaseFromContainer}/ok?n={{n}}",
    timeout: TimeSpan.FromMinutes(5)));

if (full)
{
    failures += await Run("chaos_sigkill_rabbitmq", () => ChaosSigkillRabbitAsync(
        db, workload, BrokerService, count: 100, urlTemplate: $"{StubBaseFromContainer}/ok?n={{n}}",
        timeout: TimeSpan.FromMinutes(10)));
}

failures += await Run("rate_limit_same_domain", () => RateLimitAsync(
    db, workload, count: 100, urlTemplate: $"{StubBaseFromContainer}/ok?id={{n}}&v={{n}}",
    minElapsed: TimeSpan.FromSeconds(60), timeout: TimeSpan.FromMinutes(5)));

if (full)
{
    failures += await Run("worker_scaling", () => WorkerScalingAsync(
        db, workload, WorkerService, count: 300, urlTemplate: $"{StubBaseFromContainer}/ok?n={{n}}",
        workerCounts: new[] { 1, 2, 3 }, timeout: TimeSpan.FromMinutes(15)));

    failures += await Run("soak_random_kills", () => SoakAsync(
        db, workload, WorkerService, RelayService, BrokerService,
        urlBase: StubBaseFromContainer,
        duration: TimeSpan.FromMinutes(20),
        killInterval: TimeSpan.FromSeconds(30),
        submitInterval: TimeSpan.FromSeconds(15),
        batchSize: 10,
        workers: 2,
        seed: 20260720,
        drainTimeout: TimeSpan.FromMinutes(15)));
}
else
{
    Console.WriteLine();
    Console.WriteLine("skipped (run with --full): chaos_sigkill_rabbitmq, worker_scaling, soak_random_kills");
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL SCENARIOS PASSED" : $"{failures} SCENARIO(S) FAILED");
return failures == 0 ? 0 : 1;

static async Task<int> Run(string name, Func<Task<bool>> scenario)
{
    Console.WriteLine();
    Console.WriteLine($"=== {name} ===");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        var ok = await scenario();
        Console.WriteLine($"=== {name}: {(ok ? "PASS" : "FAIL")} ({sw.Elapsed.TotalSeconds:F1}s) ===");
        return ok ? 0 : 1;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"=== {name}: ERROR ({sw.Elapsed.TotalSeconds:F1}s) — {ex.Message} ===");
        return 1;
    }
}

static async Task<bool> BaselineAsync(
    DbAccess db, Workload workload, int count, string urlTemplate, Expectation expect, TimeSpan timeout)
{
    await db.TruncateAllAsync();
    var ids = await workload.SubmitAsync(count, urlTemplate);
    Console.WriteLine($"submitted {ids.Count} tasks");

    var counts = await PollUntilTerminalAsync(db, ids, timeout);
    return AssertExpectation(ids.Count, counts, expect)
        && await AssertNoDuplicatesAsync(db, ids);
}

static async Task<bool> ChaosSigkillAsync(
    DbAccess db, Workload workload, int count, string urlTemplate, TimeSpan timeout)
{
    await db.TruncateAllAsync();
    var ids = await workload.SubmitAsync(count, urlTemplate);
    Console.WriteLine($"submitted {ids.Count} tasks");

    var workers = await FaultInjector.GetServiceContainersAsync(WorkerService);
    if (workers.Count == 0)
    {
        Console.WriteLine($"  invariant FAIL: no {WorkerService} containers found");
        return false;
    }
    var worker = workers[0];
    Console.WriteLine($"target worker: {worker}");

    var killThreshold = count / 2;
    var deadline = DateTime.UtcNow + timeout;
    var killed = false;

    while (DateTime.UtcNow < deadline)
    {
        var counts = await db.GetTerminalCountsAsync(ids);
        if (!killed && counts.Successes >= killThreshold)
        {
            Console.WriteLine($"reached {counts.Successes} successes — SIGKILL {worker}");
            await FaultInjector.SigkillAsync(worker);
            await Task.Delay(TimeSpan.FromSeconds(2));
            await FaultInjector.StartAsync(worker);
            Console.WriteLine($"restarted {worker}");
            killed = true;
        }
        if (counts.AllSettled) break;
        await Task.Delay(TimeSpan.FromSeconds(1));
    }

    var final = await db.GetTerminalCountsAsync(ids);
    Console.WriteLine($"final: successes={final.Successes} dlq={final.DeadLettered} stuck={final.Stuck}");
    return AssertExpectation(ids.Count, final, Expectation.AllSuccess)
        && await AssertNoDuplicatesAsync(db, ids)
        && killed;
}

static async Task<bool> ChaosSigtermGracefulAsync(
    DbAccess db, Workload workload, int count, string urlTemplate, TimeSpan timeout)
{
    await db.TruncateAllAsync();
    var ids = await workload.SubmitAsync(count, urlTemplate);
    Console.WriteLine($"submitted {ids.Count} tasks");

    // Give the current leader time to emit at least one "Heartbeat OK" so FindLeaderAsync
    // can spot it in the recent log window.
    await Task.Delay(TimeSpan.FromSeconds(8));

    var relays = await FaultInjector.GetServiceContainersAsync("relay");
    if (relays.Count == 0)
    {
        Console.WriteLine("  invariant FAIL: no relay containers found");
        return false;
    }
    Console.WriteLine($"found {relays.Count} relay replicas: {string.Join(", ", relays)}");

    var leader = await FaultInjector.FindLeaderAsync(relays);
    if (leader is null)
    {
        Console.WriteLine("  invariant FAIL: could not identify current leader from recent logs");
        return false;
    }
    Console.WriteLine($"current leader: {leader}");

    var initialToken = await db.GetLeaderTokenAsync();
    Console.WriteLine($"initial leader token: {initialToken}");

    var sw = System.Diagnostics.Stopwatch.StartNew();
    Console.WriteLine($"SIGTERM {leader}");
    await FaultInjector.SigtermAsync(leader);

    var detectDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
    long newToken = initialToken;
    while (DateTime.UtcNow < detectDeadline)
    {
        newToken = await db.GetLeaderTokenAsync();
        if (newToken > initialToken) break;
        await Task.Delay(250);
    }
    sw.Stop();

    var bumped = newToken > initialToken;
    if (!bumped)
    {
        Console.WriteLine($"  invariant FAIL: no token bump within {sw.Elapsed.TotalSeconds:F1}s after SIGTERM");
    }
    else
    {
        Console.WriteLine($"failover: token {initialToken} -> {newToken} in {sw.Elapsed.TotalSeconds:F1}s");
    }

    // With the graceful-shutdown LastSeenAt backdate, a follower's next poll (~5s)
    // claims leadership. Without the backdate it would wait out the 15s stale interval.
    // 10s leaves headroom for poll jitter while still proving we're well under 15s.
    var fastFailover = bumped && sw.Elapsed.TotalSeconds < 10;
    if (bumped && !fastFailover)
    {
        Console.WriteLine($"  invariant FAIL: failover took {sw.Elapsed.TotalSeconds:F1}s, expected < 10s with graceful shutdown");
    }

    await FaultInjector.StartAsync(leader);
    Console.WriteLine($"restarted {leader}");

    var counts = await PollUntilTerminalAsync(db, ids, timeout);
    return AssertExpectation(ids.Count, counts, Expectation.AllSuccess)
        && await AssertNoDuplicatesAsync(db, ids)
        && fastFailover;
}

static async Task<bool> ChaosSigkillLeaderAsync(
    DbAccess db, Workload workload, int count, string urlTemplate, TimeSpan timeout)
{
    await db.TruncateAllAsync();
    var ids = await workload.SubmitAsync(count, urlTemplate);
    Console.WriteLine($"submitted {ids.Count} tasks");

    // Give the current leader time to run TryClaimLeadership at least once
    // so the ContainerId column is populated.
    await Task.Delay(TimeSpan.FromSeconds(5));

    var leader = await db.GetLeaderContainerAsync();
    if (string.IsNullOrEmpty(leader))
    {
        Console.WriteLine("  invariant FAIL: Leader.ContainerId is null - no leader claim observed");
        return false;
    }
    Console.WriteLine($"current leader (from Leader.ContainerId): {leader}");

    var initialToken = await db.GetLeaderTokenAsync();
    Console.WriteLine($"initial leader token: {initialToken}");

    var sw = System.Diagnostics.Stopwatch.StartNew();
    Console.WriteLine($"SIGKILL {leader}");
    await FaultInjector.SigkillAsync(leader);

    // SIGKILL has no graceful backdate, so failover has to wait out the full
    // HeartbeatStaleSeconds (15s) interval before a follower claims, plus up to
    // one HeartbeatIntervalSeconds (3s) of follower-poll jitter.
    var detectDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(18);
    long newToken = initialToken;
    while (DateTime.UtcNow < detectDeadline)
    {
        newToken = await db.GetLeaderTokenAsync();
        if (newToken > initialToken) break;
        await Task.Delay(250);
    }
    sw.Stop();

    var bumped = newToken > initialToken;
    if (!bumped)
    {
        Console.WriteLine($"  invariant FAIL: no token bump within {sw.Elapsed.TotalSeconds:F1}s after SIGKILL");
    }
    else
    {
        Console.WriteLine($"failover: token {initialToken} -> {newToken} in {sw.Elapsed.TotalSeconds:F1}s");
    }

    await FaultInjector.StartAsync(leader);
    Console.WriteLine($"restarted {leader}");

    var counts = await PollUntilTerminalAsync(db, ids, timeout);
    return AssertExpectation(ids.Count, counts, Expectation.AllSuccess)
        && await AssertNoDuplicatesAsync(db, ids)
        && bumped;
}

static async Task<bool> ChaosSigkillRabbitAsync(
    DbAccess db, Workload workload, string brokerService, int count, string urlTemplate, TimeSpan timeout)
{
    await db.TruncateAllAsync();
    var ids = await workload.SubmitAsync(count, urlTemplate);
    Console.WriteLine($"submitted {ids.Count} tasks");

    var brokers = await FaultInjector.GetServiceContainersAsync(brokerService);
    if (brokers.Count == 0)
    {
        Console.WriteLine($"  invariant FAIL: no {brokerService} containers found");
        return false;
    }
    var broker = brokers[0];

    // Kill only once there is real in-flight work, otherwise the outage lands on an
    // idle broker and proves nothing.
    var killThreshold = Math.Max(1, count / 4);
    var deadline = DateTime.UtcNow + timeout;
    var drainedEarly = false;
    while (DateTime.UtcNow < deadline)
    {
        var counts = await db.GetTerminalCountsAsync(ids);
        if (counts.Successes >= killThreshold) break;
        if (counts.AllSettled)
        {
            drainedEarly = true;
            break;
        }
        await Task.Delay(500);
    }

    if (drainedEarly)
    {
        Console.WriteLine($"  invariant FAIL: workload drained before reaching {killThreshold} successes — no fault injected");
        return false;
    }

    Console.WriteLine($"SIGKILL {broker}");
    await FaultInjector.SigkillAsync(broker);

    var sw = System.Diagnostics.Stopwatch.StartNew();

    // compose sets restart: always, so the broker normally comes back on its own.
    // Starting it explicitly is a no-op if the restart policy already fired.
    await Task.Delay(TimeSpan.FromSeconds(5));
    await FaultInjector.StartAsync(broker);

    var recovered = await FaultInjector.WaitForHealthyAsync(broker, TimeSpan.FromMinutes(2));
    sw.Stop();

    if (!recovered)
    {
        Console.WriteLine($"  invariant FAIL: {broker} did not report healthy within {sw.Elapsed.TotalSeconds:F1}s");
        return false;
    }
    Console.WriteLine($"{broker} healthy again after {sw.Elapsed.TotalSeconds:F1}s");

    // The real assertion: consumers re-subscribe and the backlog drains. If the worker's
    // consumer does not survive the reconnect, these tasks stay Stuck until timeout.
    var final = await PollUntilTerminalAsync(db, ids, timeout);
    return AssertExpectation(ids.Count, final, Expectation.AllSuccess)
        && await AssertNoDuplicatesAsync(db, ids);
}

static async Task<bool> RateLimitAsync(
    DbAccess db, Workload workload, int count, string urlTemplate,
    TimeSpan minElapsed, TimeSpan timeout)
{
    await db.TruncateAllAsync();
    var ids = await workload.SubmitAsync(count, urlTemplate);
    Console.WriteLine($"submitted {ids.Count} same-domain tasks with varying URLs");

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var counts = await PollUntilTerminalAsync(db, ids, timeout);
    sw.Stop();
    Console.WriteLine($"drain time: {sw.Elapsed.TotalSeconds:F1}s (expected >= {minElapsed.TotalSeconds:F0}s under rate limit)");

    var throttled = sw.Elapsed >= minElapsed;
    if (!throttled)
    {
        Console.WriteLine($"  invariant FAIL: drained in {sw.Elapsed.TotalSeconds:F1}s — rate limit not enforced");
    }
    return AssertExpectation(ids.Count, counts, Expectation.AllSuccess)
        && await AssertNoDuplicatesAsync(db, ids)
        && throttled;
}

static async Task<bool> WorkerScalingAsync(
    DbAccess db, Workload workload, string service, int count, string urlTemplate,
    int[] workerCounts, TimeSpan timeout)
{
    var results = new List<(int Workers, TimeSpan Submit, TimeSpan Total)>();
    var ok = true;

    try
    {
        foreach (var n in workerCounts)
        {
            Console.WriteLine();
            Console.WriteLine($"--- {n} worker(s) ---");
            await FaultInjector.ScaleServiceAsync(service, n);

            var running = await FaultInjector.WaitForServiceContainersAsync(service, n, TimeSpan.FromSeconds(60));
            if (running.Count != n)
            {
                Console.WriteLine($"  invariant FAIL: expected {n} {service} container(s), saw {running.Count}");
                return false;
            }
            Console.WriteLine($"running: {string.Join(", ", running)}");

            // New replicas need a moment to register their RabbitMQ consumer before
            // they will take any share of the work.
            await Task.Delay(TimeSpan.FromSeconds(5));

            await db.TruncateAllAsync();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ids = await workload.SubmitAsync(count, urlTemplate);
            var submit = sw.Elapsed;
            Console.WriteLine($"submitted {ids.Count} tasks in {submit.TotalSeconds:F1}s");

            var counts = await PollUntilTerminalAsync(db, ids, timeout);
            sw.Stop();

            ok &= AssertExpectation(ids.Count, counts, Expectation.AllSuccess)
                && await AssertNoDuplicatesAsync(db, ids);

            Console.WriteLine($"{n} worker(s): {sw.Elapsed.TotalSeconds:F1}s total");
            results.Add((n, submit, sw.Elapsed));
        }
    }
    finally
    {
        Console.WriteLine();
        Console.WriteLine($"restoring {service} to 1 replica");
        await FaultInjector.ScaleServiceAsync(service, 1);
        await FaultInjector.WaitForServiceContainersAsync(service, 1, TimeSpan.FromSeconds(60));
    }

    Console.WriteLine();
    Console.WriteLine($"  workers |   submit |    total |  speedup");
    Console.WriteLine($"  --------+----------+----------+---------");
    var baselineTotal = results[0].Total.TotalSeconds;
    foreach (var r in results)
    {
        var speedup = r.Total.TotalSeconds > 0 ? baselineTotal / r.Total.TotalSeconds : 0;
        Console.WriteLine($"  {r.Workers,7} | {r.Submit.TotalSeconds,7:F1}s | {r.Total.TotalSeconds,7:F1}s | {speedup,7:F2}x");
    }

    return ok;
}

static async Task<bool> SoakAsync(
    DbAccess db, Workload workload, string workerService, string relayService, string brokerService,
    string urlBase, TimeSpan duration, TimeSpan killInterval, TimeSpan submitInterval,
    int batchSize, int workers, int seed, TimeSpan drainTimeout)
{
    var ids = new List<Guid>();
    var idsLock = new object();
    var kills = new List<string>();
    var submitFailures = 0;
    var killFailures = 0;

    try
    {
        await FaultInjector.ScaleServiceAsync(workerService, workers);
        var running = await FaultInjector.WaitForServiceContainersAsync(workerService, workers, TimeSpan.FromSeconds(60));
        if (running.Count != workers)
        {
            Console.WriteLine($"  invariant FAIL: expected {workers} {workerService} container(s), saw {running.Count}");
            return false;
        }
        await Task.Delay(TimeSpan.FromSeconds(5));

        await db.TruncateAllAsync();

        Console.WriteLine($"soak: {duration.TotalMinutes:F0}m, kill every {killInterval.TotalSeconds:F0}s, " +
                          $"{batchSize} tasks every {submitInterval.TotalSeconds:F0}s, {workers} workers, seed={seed}");

        var rng = new Random(seed);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var soakDeadline = DateTime.UtcNow + duration;

        var submitter = Task.Run(async () =>
        {
            var batch = 0;
            while (DateTime.UtcNow < soakDeadline)
            {
                try
                {
                    var posted = await workload.SubmitAsync(batchSize, $"{urlBase}/ok?b={batch}&n={{n}}");
                    lock (idsLock) ids.AddRange(posted);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref submitFailures);
                    Console.WriteLine($"  [{sw.Elapsed.TotalSeconds,6:F0}s] submit batch {batch} failed: {ex.Message}");
                }
                batch++;
                await Task.Delay(submitInterval);
            }
        });

        var killer = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(killInterval);
                if (DateTime.UtcNow >= soakDeadline) break;

                var candidates = new List<string>();
                try
                {
                    candidates.AddRange(await FaultInjector.GetServiceContainersAsync(workerService));
                    candidates.AddRange(await FaultInjector.GetServiceContainersAsync(relayService));
                    candidates.AddRange(await FaultInjector.GetServiceContainersAsync(brokerService));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [{sw.Elapsed.TotalSeconds,6:F0}s] could not enumerate containers: {ex.Message}");
                    continue;
                }

                if (candidates.Count == 0) continue;
                var victim = candidates[rng.Next(candidates.Count)];

                try
                {
                    await FaultInjector.SigkillAsync(victim);
                    await Task.Delay(TimeSpan.FromSeconds(2));
                    await FaultInjector.StartAsync(victim);
                    kills.Add(victim);
                    Console.WriteLine($"  [{sw.Elapsed.TotalSeconds,6:F0}s] killed+restarted {victim}");
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref killFailures);
                    Console.WriteLine($"  [{sw.Elapsed.TotalSeconds,6:F0}s] kill of {victim} failed: {ex.Message}");
                }
            }
        });

        await Task.WhenAll(submitter, killer);
        sw.Stop();

        Console.WriteLine();
        Console.WriteLine($"soak window closed after {sw.Elapsed.TotalMinutes:F1}m — " +
                          $"{ids.Count} tasks submitted, {kills.Count} kills, " +
                          $"{submitFailures} submit failure(s), {killFailures} kill failure(s)");
        foreach (var g in kills.GroupBy(k => k).OrderByDescending(g => g.Count()))
        {
            Console.WriteLine($"    {g.Count(),3}x {g.Key}");
        }
    }
    finally
    {
        Console.WriteLine($"restoring {workerService} to 1 replica");
        await FaultInjector.ScaleServiceAsync(workerService, 1);
        await FaultInjector.WaitForServiceContainersAsync(workerService, 1, TimeSpan.FromSeconds(60));
    }

    if (ids.Count == 0)
    {
        Console.WriteLine("  invariant FAIL: no tasks were submitted during the soak");
        return false;
    }
    if (kills.Count == 0)
    {
        Console.WriteLine("  invariant FAIL: no containers were killed during the soak");
        return false;
    }

    Console.WriteLine("draining backlog with churn stopped...");
    var final = await PollUntilTerminalAsync(db, ids, drainTimeout);
    return AssertExpectation(ids.Count, final, Expectation.AllSuccess)
        && await AssertNoDuplicatesAsync(db, ids)
        && submitFailures == 0;
}

static async Task<TerminalCounts> PollUntilTerminalAsync(
    DbAccess db, IReadOnlyCollection<Guid> ids, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    TerminalCounts last = default;
    while (DateTime.UtcNow < deadline)
    {
        last = await db.GetTerminalCountsAsync(ids);
        if (last.AllSettled) break;
        await Task.Delay(TimeSpan.FromSeconds(1));
    }
    Console.WriteLine($"final: successes={last.Successes} dlq={last.DeadLettered} stuck={last.Stuck}");
    return last;
}

static bool AssertExpectation(int submitted, TerminalCounts counts, Expectation expect)
{
    bool ok = expect switch
    {
        Expectation.AllSuccess        => counts.Successes == submitted && counts.DeadLettered == 0 && counts.Stuck == 0,
        Expectation.AllDeadLettered   => counts.DeadLettered == submitted && counts.Successes == 0 && counts.Stuck == 0,
        _ => false
    };
    if (!ok)
    {
        Console.WriteLine($"  invariant FAIL: expected {expect}, got successes={counts.Successes} dlq={counts.DeadLettered} stuck={counts.Stuck} (submitted={submitted})");
    }
    return ok;
}

static async Task<bool> AssertNoDuplicatesAsync(DbAccess db, IReadOnlyCollection<Guid> ids)
{
    var dups = await db.CountDuplicateSuccessesAsync(ids);
    if (dups > 0) Console.WriteLine($"  invariant FAIL: {dups} TaskId(s) have multiple Success rows");
    return dups == 0;
}

internal enum Expectation { AllSuccess, AllDeadLettered }
