using System.Diagnostics;

namespace ChaosHarness;

internal static class FaultInjector
{
    public static Task SigkillAsync(string container) => DockerAsync("kill", container);
    public static Task StartAsync(string container) => DockerAsync("start", container);

    private static async Task DockerAsync(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
        {
            var err = await p.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"docker {string.Join(' ', args)} failed: {err}");
        }
    }
}
