using ChaosHarness;

const string ConnectionString = "Host=localhost;Port=5432;Database=mydatabase;Username=myuser;Password=mypassword";
const string ControllerBase = "http://localhost:5001";
const string StubBaseFromContainer = "http://host.docker.internal:6000";
const int StubPort = 6000;
const string WorkerContainer = "worker-node";

await using var stub = new StubTargetServer(StubPort);
stub.Start();
Console.WriteLine($"[stub] listening on :{StubPort}");

var db = new DbAccess(ConnectionString);
var workload = new Workload(ControllerBase);

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

failures += await Run("rate_limit_same_domain", () => RateLimitAsync(
    db, workload, count: 100, urlTemplate: $"{StubBaseFromContainer}/ok?id={{n}}&v={{n}}",
    minElapsed: TimeSpan.FromSeconds(60), timeout: TimeSpan.FromMinutes(5)));

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

    var killThreshold = count / 2;
    var deadline = DateTime.UtcNow + timeout;
    var killed = false;

    while (DateTime.UtcNow < deadline)
    {
        var counts = await db.GetTerminalCountsAsync(ids);
        if (!killed && counts.Successes >= killThreshold)
        {
            Console.WriteLine($"reached {counts.Successes} successes — SIGKILL {WorkerContainer}");
            await FaultInjector.SigkillAsync(WorkerContainer);
            await Task.Delay(TimeSpan.FromSeconds(2));
            await FaultInjector.StartAsync(WorkerContainer);
            Console.WriteLine($"restarted {WorkerContainer}");
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
