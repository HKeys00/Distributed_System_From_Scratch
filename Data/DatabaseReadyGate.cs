using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Data;

public static class DatabaseReadyExtensions
{
    /// <summary>
    /// Blocks host startup until the data service has finished applying migrations.
    /// </summary>
    /// <remarks>
    /// Compose can only guarantee start order here, not completion: Visual Studio swaps
    /// the data container's entrypoint for DistrolessHelper in both Fast and Regular
    /// mode, so the app does not run - and the container is neither healthy nor exited -
    /// while `compose up` is still going. A `service_healthy` dependency deadlocks there.
    /// See the note on relay's depends_on in docker-compose.yml.
    ///
    /// Register this after the metrics server but before any hosted service that reads
    /// the schema: IHostedService instances start in registration order.
    /// </remarks>
    public static IServiceCollection AddDatabaseReadyGate(this IServiceCollection services, TimeSpan? timeout = null)
    {
        services.AddHostedService(sp => new DatabaseReadyGate(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILogger<DatabaseReadyGate>>(),
            timeout ?? TimeSpan.FromMinutes(2)));
        return services;
    }
}

internal sealed class DatabaseReadyGate : IHostedService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DatabaseReadyGate> _logger;
    private readonly TimeSpan _timeout;

    public DatabaseReadyGate(IServiceScopeFactory scopeFactory, ILogger<DatabaseReadyGate> logger, TimeSpan timeout)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _timeout = timeout;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var attempt = 0;
        string? reason = null;

        while (!linked.IsCancellationRequested)
        {
            attempt++;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                // Also covers "postgres is not up yet" - this throws until it can connect.
                var pending = (await db.Database.GetPendingMigrationsAsync(linked.Token)).ToList();
                if (pending.Count == 0)
                {
                    _logger.LogInformation("Database schema is up to date after {Attempts} attempt(s)", attempt);
                    return;
                }

                reason = $"{pending.Count} migration(s) still pending";
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                reason = ex.GetBaseException().Message;
            }

            _logger.LogWarning(
                "Waiting for database ({Reason}); retrying in {Delay}s",
                reason,
                PollInterval.TotalSeconds);

            try
            {
                await Task.Delay(PollInterval, linked.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // A shutdown request is not a failure; running out of time is.
        cancellationToken.ThrowIfCancellationRequested();
        throw new TimeoutException(
            $"Database was not ready after {_timeout.TotalSeconds:0}s. Last failure: {reason ?? "unknown"}");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
