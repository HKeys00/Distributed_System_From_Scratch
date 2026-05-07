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
