using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Pipelines;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;

namespace ChaosToDo.AppHost;

#pragma warning disable ASPIREPIPELINES001
internal static class ChaosWorkspacePublisher
{
    const string ApiVersion = "2026-08-01-preview";

    /// <summary>
    /// Refreshes discovery and evaluation, reapplies IaC default configurations and validates
    /// their persisted parameters, actions and resolved targets. Never executes a scenario.
    /// </summary>
    public static async Task RefreshAsync(
        PipelineStepContext context,
        string subscriptionId,
        string resourceGroup,
        BicepOutputReference workspaceName,
        BicepOutputReference configurations)
    {
        var name = await workspaceName.GetValueAsync(context.CancellationToken);
        var configurationJson = await configurations.GetValueAsync(context.CancellationToken);
        using var desired = JsonDocument.Parse(configurationJson
            ?? throw new InvalidOperationException("Chaos Bicep did not return default configurations."));
        var baseUri = new Uri(
            $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{Uri.EscapeDataString(resourceGroup)}" +
            $"/providers/Microsoft.Chaos/workspaces/{Uri.EscapeDataString(name ?? throw new InvalidOperationException("Missing workspace name."))}/");
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        client.Timeout = TimeSpan.FromSeconds(60);
        var token = await new DefaultAzureCredential().GetTokenAsync(
            new TokenRequestContext(["https://management.azure.com/.default"]), context.CancellationToken);

        // Scenario evaluation uses a persisted discovery snapshot, not the current ARM inventory.
        await RunOperationAsync("discover", "discoveries/latest");
        await RunOperationAsync("evaluate", "evaluations/latest");
        foreach (var configuration in desired.RootElement.EnumerateArray())
        {
            var scenario = configuration.GetProperty("scenarioId").GetString()
                ?? throw new InvalidOperationException("Missing configuration scenarioId.");
            var path = $"scenarios/{Uri.EscapeDataString(scenario)}/configurations/default";
            // Preserve IaC targeting after discovery/evaluation, including single-resource filters.
            var payload = $"{{\"properties\":{configuration.GetRawText()}}}";
            var put = await SendAsync(HttpMethod.Put, path, payload);
            if (put.StatusCode == HttpStatusCode.Accepted)
            {
                await WaitAsync(put.Location ?? UriFor(path), "provisioningState");
            }
            var persisted = await SendAsync(HttpMethod.Get, path);
            using var actual = JsonDocument.Parse(persisted.Body);
            var actualProperties = actual.RootElement.GetProperty("properties");
            var allowed = ReadTargets(configuration);
            if (!allowed.SetEquals(ReadTargets(actualProperties)) ||
                !ReadParameters(configuration).SetEquals(ReadParameters(actualProperties)))
                throw new InvalidOperationException($"Chaos configuration {scenario} does not match IaC targeting/parameters.");
            var definition = await SendAsync(HttpMethod.Get, $"scenarios/{Uri.EscapeDataString(scenario)}");
            using var scenarioDocument = JsonDocument.Parse(definition.Body);
            var expectedActions = scenarioDocument.RootElement.GetProperty("properties").GetProperty("actions")
                .EnumerateArray().Select(action => action.GetProperty("name").GetString()).ToHashSet(StringComparer.Ordinal);
            var validation = await SendAsync(HttpMethod.Post, $"{path}/validate", "{}");
            var result = await WaitAsync(validation.Location ?? UriFor($"{path}/validations/latest"), "status");
            using var plan = JsonDocument.Parse(result.GetProperty("executionPlanJson").GetString()
                ?? throw new InvalidOperationException($"Missing execution plan for {scenario}."));
            var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var actions = plan.RootElement.GetProperty("actions").EnumerateObject().ToArray();
            if (actions.Length == 0 || !expectedActions.SetEquals(actions.Select(action => action.Name)))
                throw new InvalidOperationException($"Chaos scenario {scenario} has missing or unexpected actions.");
            foreach (var action in actions)
            {
                if (action.Value.GetProperty("skip").GetBoolean())
                    throw new InvalidOperationException($"Chaos scenario {scenario} skips action {action.Name}.");
                var targets = action.Value.GetProperty("properties").GetProperty("targetResources").EnumerateArray().ToArray();
                if (targets.Length == 0)
                    throw new InvalidOperationException($"Chaos action {action.Name} has no targets.");
                foreach (var target in targets)
                {
                    var id = target.GetProperty("fullyQualifiedIdentifier").GetString();
                    if (id is null || !allowed.Contains(id))
                        throw new InvalidOperationException($"Chaos action {action.Name} resolved an unexpected target: {id}.");
                    resolved.Add(id);
                }
                if (scenario == "compute-zone-down")
                {
                    var parameters = configuration.GetProperty("parameters").EnumerateArray()
                        .ToDictionary(item => item.GetProperty("key").GetString()!,
                            item => item.GetProperty("value").GetString()!);
                    var faults = action.Value.GetProperty("properties").GetProperty("faultParameters");
                    using var zones = JsonDocument.Parse(faults.GetProperty("Zones").GetString()!);
                    if (!zones.RootElement.EnumerateArray().Select(zone => zone.GetString())
                            .SequenceEqual([parameters["zone"]]) ||
                        faults.GetProperty("GracefulShutdown").GetString() != "false" ||
                        TimeSpan.Parse(action.Value.GetProperty("stopCondition").GetProperty("duration").GetString()!,
                            System.Globalization.CultureInfo.InvariantCulture) != XmlConvert.ToTimeSpan(parameters["duration"]))
                        throw new InvalidOperationException("Compute Zone Down resolved unexpected zone, shutdown mode, or duration.");
                }
            }
            if (!resolved.SetEquals(allowed))
                throw new InvalidOperationException($"Chaos scenario {scenario} did not resolve every configured target.");
            context.Logger.LogInformation("Chaos configuration {Scenario}/default ready; no actions skipped.", scenario);
        }
        context.Summary.Add("Chaos configurations", "Discovery refreshed; all default configurations ready. No faults started.");
        return;

        // Read explicitly included resource IDs for case-insensitive targeting comparisons.
        static HashSet<string> ReadTargets(JsonElement properties) =>
            properties.GetProperty("resourceTargeting").GetProperty("include").GetProperty("resources")
                .EnumerateArray().Select(resource => resource.GetString()
                    ?? throw new InvalidOperationException("Chaos target ID must be a string."))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Preserve exact parameter key/value pairs when comparing desired and persisted defaults.
        static HashSet<(string Key, string Value)> ReadParameters(JsonElement properties) =>
        [
            .. properties.GetProperty("parameters").EnumerateArray()
                .Select(item => (
                    item.GetProperty("key").GetString() ??
                    throw new InvalidOperationException("Missing Chaos parameter key."),
                    item.GetProperty("value").GetString() ??
                    throw new InvalidOperationException("Missing Chaos parameter value.")))
        ];

        // Resolve workspace-relative paths with the preview API version.
        Uri UriFor(string path) => new(baseUri, $"{path}?api-version={ApiVersion}");

        // Share ARM authentication/error handling and serialize optional JSON request bodies.
        Task<SqlFailoverRunbookPublisher.ArmResponse> SendAsync(HttpMethod method, string path, string? body = null) =>
            SqlFailoverRunbookPublisher.SendArmAsync(client, token, method, UriFor(path),
                body is null ? null : new StringContent(body, Encoding.UTF8, "application/json"), context.CancellationToken);

        // Start discovery/evaluation and wait on the returned operation or its latest-result endpoint.
        async Task RunOperationAsync(string action, string resultPath)
        {
            context.Logger.LogInformation("Refreshing Chaos workspace: {Operation}.", action);
            var response = await SendAsync(HttpMethod.Post, action, "{}");
            await WaitAsync(response.Location ?? UriFor(resultPath), "status");
        }

        // Validate the workspace polling scope, then wait for success within ten minutes.
        // Return detached result properties so the response JsonDocument can be disposed.
        async Task<JsonElement> WaitAsync(Uri uri, string statusProperty)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != baseUri.Host ||
                !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) ||
                !uri.AbsolutePath.StartsWith(baseUri.AbsolutePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Chaos returned an unexpected operation URI.");
            var deadline = DateTimeOffset.UtcNow.AddMinutes(10);
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), context.CancellationToken);
                var response = await SqlFailoverRunbookPublisher.SendArmAsync(
                    client, token, HttpMethod.Get, uri, null, context.CancellationToken);
                using var document = JsonDocument.Parse(response.Body);
                var properties = document.RootElement.GetProperty("properties");
                var status = properties.GetProperty(statusProperty).GetString();
                if (status == "Succeeded") return properties.Clone();
                if (status is not ("Pending" or "Queued" or "InProgress" or "Creating" or "Updating" or "Running"))
                    throw new InvalidOperationException($"Chaos operation ended with {status}: {properties.GetRawText()}");
            }
            throw new TimeoutException("Chaos workspace operation exceeded 10 minutes.");
        }
    }
}
