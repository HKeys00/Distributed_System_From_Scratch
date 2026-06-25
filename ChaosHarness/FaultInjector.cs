using System.Diagnostics;

namespace ChaosHarness;

internal static class FaultInjector
{
    public static Task SigkillAsync(string container) => DockerAsync("kill", container);
    public static Task SigtermAsync(string container) => DockerAsync("kill", "--signal=SIGTERM", container);
    public static Task StartAsync(string container) => DockerAsync("start", container);

    public static async Task<List<string>> GetServiceContainersAsync(string serviceName)
    {
        var (stdout, _) = await DockerCaptureAsync(
            "ps",
            "--filter", $"label=com.docker.compose.service={serviceName}",
            "--format", "{{.Names}}");
        return stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    public static async Task<string?> FindLeaderAsync(IEnumerable<string> relayContainers)
    {
        foreach (var relay in relayContainers)
        {
            var (stdout, stderr) = await DockerCaptureAsync("logs", "--since", "10s", "--tail", "30", relay);
            if ((stdout + stderr).Contains("Heartbeat OK"))
            {
                return relay;
            }
        }
        return null;
    }

    private static async Task DockerAsync(params string[] args)
    {
        var psi = BuildPsi(args);
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
        {
            var err = await p.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"docker {string.Join(' ', args)} failed: {err}");
        }
    }

    private static async Task<(string stdout, string stderr)> DockerCaptureAsync(params string[] args)
    {
        var psi = BuildPsi(args);
        using var p = Process.Start(psi)!;
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"docker {string.Join(' ', args)} failed: {stderr}");
        }
        return (stdout, stderr);
    }

    private static ProcessStartInfo BuildPsi(string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }
}
