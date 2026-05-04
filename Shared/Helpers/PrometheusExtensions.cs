using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Prometheus;

namespace Shared.Helpers;

public static class PrometheusExtensions
{
    public static IServiceCollection AddPrometheusMetrics(this IServiceCollection services, int port)
    {
        services.AddHostedService(sp => new MetricServerHostedService(
            port,
            sp.GetRequiredService<ILogger<MetricServerHostedService>>()));
        return services;
    }
}

internal sealed class MetricServerHostedService : IHostedService
{
    private readonly MetricServer _server;
    private readonly int _port;
    private readonly ILogger<MetricServerHostedService> _logger;

    public MetricServerHostedService(int port, ILogger<MetricServerHostedService> logger)
    {
        _port = port;
        _logger = logger;
        _server = new MetricServer(port: port);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _server.Start();
        _logger.LogInformation("Prometheus metrics listening on port {Port}/metrics", _port);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => _server.StopAsync();
}
