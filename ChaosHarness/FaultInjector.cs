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

    public static async Task<string> GetHealthAsync(string container)
    {
        var (stdout, _) = await DockerCaptureAsync(
            "inspect",
            "--format", "{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}",
            container);
        return stdout.Trim();
    }

    public static async Task<bool> WaitForHealthyAsync(string container, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await GetHealthAsync(container) == "healthy") return true;
            }
            catch (InvalidOperationException)
            {
                // Container is mid-restart and not yet inspectable.
            }
            await Task.Delay(1000);
        }
        return false;
    }

    public static async Task ScaleServiceAsync(string serviceName, int replicas)
    {
        await ComposeAsync("up", "-d", "--no-build", "--no-recreate",
            "--scale", $"{serviceName}={replicas}", serviceName);
    }

    public static async Task<List<string>> WaitForServiceContainersAsync(
        string serviceName, int expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var containers = new List<string>();
        while (DateTime.UtcNow < deadline)
        {
            containers = await GetServiceContainersAsync(serviceName);
            if (containers.Count == expected) return containers;
            await Task.Delay(500);
        }
        return containers;
    }

    private static async Task ComposeAsync(params string[] args)
    {
        var psi = BuildPsi(new[] { "compose" }.Concat(args).ToArray());
        psi.WorkingDirectory = FindRepoRoot();
        using var p = Process.Start(psi)!;
        var stderrTask = p.StandardError.ReadToEndAsync();
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        var err = await stderrTask;
        await stdoutTask;
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"docker compose {string.Join(' ', args)} failed: {err}");
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "docker-compose.yml"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            $"could not locate docker-compose.yml above {AppContext.BaseDirectory}");
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
