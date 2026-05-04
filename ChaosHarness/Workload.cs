using System.Net.Http.Json;

namespace ChaosHarness;

internal sealed class Workload
{
    private readonly HttpClient _http;
    private readonly string _controllerBase;

    public Workload(string controllerBase)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _controllerBase = controllerBase.TrimEnd('/');
    }

    public async Task<List<Guid>> SubmitAsync(int count, string urlTemplate)
    {
        var ids = new List<Guid>(count);
        for (int i = 0; i < count; i++)
        {
            var url = urlTemplate.Replace("{n}", i.ToString());
            var resp = await _http.PostAsJsonAsync(
                $"{_controllerBase}/crawl",
                new { Url = url });
            resp.EnsureSuccessStatusCode();
            var taskId = await resp.Content.ReadFromJsonAsync<Guid>();
            ids.Add(taskId);
        }
        return ids;
    }
}
