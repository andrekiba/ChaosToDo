using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace ChaosToDo.Api;

/// <summary>
/// Resolves a "machine/zone" label once at startup so the demos can show which instance
/// served each request. On App Service (no IMDS) the label uses the worker instance id;
/// on the VM scale set the zone comes from Azure IMDS when available.
/// </summary>
internal static class ServedByIdentity
{
    static readonly Uri ImdsComputeUri = new("http://169.254.169.254/metadata/instance/compute?api-version=2021-02-01");

    static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(2)
    };

    public static async Task<string> ResolveAsync()
    {
        var appServiceInstanceId = Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID");
        if (!string.IsNullOrWhiteSpace(appServiceInstanceId))
        {
            return $"{Environment.MachineName}/instance-{appServiceInstanceId[..Math.Min(8, appServiceInstanceId.Length)]}";
        }

        var zone = "unknown";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ImdsComputeUri);
            request.Headers.Add("Metadata", "true");
            using var response = await Client.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var compute = await response.Content.ReadFromJsonAsync<ImdsCompute>();
                if (!string.IsNullOrWhiteSpace(compute?.Zone))
                {
                    zone = compute.Zone;
                }
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
        }

        return $"{Environment.MachineName}/zone-{zone}";
    }

    sealed record ImdsCompute([property: JsonPropertyName("zone")] string? Zone);
}
