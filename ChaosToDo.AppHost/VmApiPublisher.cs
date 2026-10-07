using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Logging;

#pragma warning disable ASPIREPIPELINES001
namespace ChaosToDo.AppHost;

/// <summary>
/// Builds a self-contained linux-x64 API package, uploads it to the package storage account and
/// installs it on every VM scale set instance through Run Command (one instance at a time).
/// </summary>
internal static class VmApiPublisher
{
    internal const string PackageStep = "build-vm-api-package";
    internal const string DeployStep = "deploy-vm-api";
    internal const string VerifyStep = "verify-vm-api";
    const string CodeOnlyDeployStep = "deploy-vm-api-code";
    const string DeployOkMarker = "CHAOSTODO_DEPLOY_OK";
    static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(15);
    static readonly TimeSpan ReadinessTimeout = TimeSpan.FromMinutes(10);
    static readonly string[] ExpectedZones = ["2", "3"];

    internal sealed record Settings(
        string VmssName,
        string PublicIpName,
        string StorageAccountName,
        string PackageContainerName,
        string IdentityName,
        string KeyVaultName,
        string SqlServerName,
        string SqlDatabaseName,
        string BootstrapTemplatePath,
        string SubscriptionId,
        string ResourceGroupName);

    /// <summary>
    /// Registers package, deployment, readiness and code-only steps. Serializes the Linux
    /// publish after the Windows publish to avoid concurrent writes to the project's obj directory.
    /// </summary>
    public static void Register(IResourceBuilder<ProjectResource> api, Settings settings)
    {
        if (!Guid.TryParse(settings.SubscriptionId, out _))
        {
            throw new InvalidOperationException("Azure:SubscriptionId must be a GUID for VM scale set deployment.");
        }
        if (!File.Exists(settings.BootstrapTemplatePath))
        {
            throw new FileNotFoundException("The VM bootstrap script template is missing.", settings.BootstrapTemplatePath);
        }

        var packageDirectory = Path.Combine(
            Path.GetTempPath(), "ChaosToDo", "vm-api", Guid.NewGuid().ToString("N"));
        var packagePath = Path.Combine(packageDirectory, "chaos-todo-api-linux-x64.tar.gz");

        // Depends on the Windows package step so the two RID-specific publishes never share obj/ concurrently.
        api.WithPipelineStepFactory(
            PackageStep,
            context => BuildPackageAsync(context, api.Resource, packageDirectory, packagePath),
            dependsOn: [WindowsApiPublisher.PackageStep],
            requiredBy: [WellKnownPipelineSteps.Publish, WellKnownPipelineSteps.Deploy],
            description: "Publish the API as self-contained linux-x64 output and package the tar.gz.");
        api.WithPipelineStepFactory(
            DeployStep,
            context => DeployAsync(context, settings, packagePath),
            dependsOn: [PackageStep],
            requiredBy: [WellKnownPipelineSteps.Deploy],
            description: "Upload the API package and install it on every VM scale set instance via Run Command.");
        api.WithPipelineStepFactory(
            VerifyStep,
            context => VerifyAsync(context, settings),
            dependsOn: [DeployStep],
            requiredBy: [WellKnownPipelineSteps.Deploy],
            description: "Verify one healthy API instance per zone behind the load balancer.");
        api.WithPipelineStepFactory(
            CodeOnlyDeployStep,
            async context =>
            {
                await DeployAsync(context, settings, packagePath);
                await VerifyAsync(context, settings);
            },
            dependsOn: [PackageStep],
            description: "Deploy API code to the existing VM scale set without provisioning infrastructure.");
    }

    /// <summary>Reads the shell template with Unix line endings for cloud-init and Run Command.</summary>
    public static string ReadBootstrapTemplate(string path) =>
        File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>
    /// Publishes self-contained Linux binaries and creates a tar.gz with the API executable's
    /// execute permissions preserved; records the archive path for subsequent deployment steps.
    /// </summary>
    static async Task BuildPackageAsync(
        PipelineStepContext context,
        ProjectResource api,
        string outputRoot,
        string packagePath)
    {
        var projectPath = api.GetProjectMetadata().ProjectPath;
        var publishDirectory = Path.Combine(outputRoot, "publish");
        Directory.CreateDirectory(publishDirectory);

        await WindowsApiPublisher.RunProcessAsync(
            "dotnet",
            [
                "publish", projectPath,
                "--configuration", "Release",
                "--runtime", "linux-x64",
                "--self-contained", "true",
                "--output", publishDirectory
            ],
            context.CancellationToken,
            ProcessTimeout);

        var executable = Path.Combine(publishDirectory, "ChaosToDo.Api");
        if (!File.Exists(executable))
        {
            throw new InvalidOperationException("The linux-x64 publish output is missing the ChaosToDo.Api executable.");
        }

        await using (var file = File.Create(packagePath))
        await using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
        await using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false))
        {
            foreach (var path in Directory.EnumerateFiles(publishDirectory, "*", SearchOption.AllDirectories))
            {
                var entryName = Path.GetRelativePath(publishDirectory, path).Replace('\\', '/');
                var entry = new PaxTarEntry(TarEntryType.RegularFile, entryName)
                {
                    Mode = entryName == "ChaosToDo.Api"
                        ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                          UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                          UnixFileMode.OtherRead | UnixFileMode.OtherExecute
                        : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                    ModificationTime = File.GetLastWriteTimeUtc(path)
                };
                await using var content = File.OpenRead(path);
                entry.DataStream = content;
                await tar.WriteEntryAsync(entry, context.CancellationToken);
            }
        }

        context.Summary.Add("VM API package", packagePath);
        context.Logger.LogInformation("Created self-contained linux-x64 API package at {PackagePath}.", packagePath);
    }

    /// <summary>
    /// Uploads versioned and latest packages, then reconciles and installs each VM sequentially.
    /// Requires the bootstrap success marker so a successful ARM command alone cannot imply readiness.
    /// </summary>
    static async Task DeployAsync(PipelineStepContext context, Settings settings, string packagePath)
    {
        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException("The VM API package step did not produce its expected archive.", packagePath);
        }

        var script = await RenderBootstrapAsync(context, settings);
        var scriptPath = Path.Combine(Path.GetDirectoryName(packagePath)!, "bootstrap.sh");
        await File.WriteAllTextAsync(scriptPath, script, context.CancellationToken);

        var release = $"releases/{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.tar.gz";
        await UploadBlobAsync(context, settings, packagePath, release);
        await UploadBlobAsync(context, settings, packagePath, "latest.tar.gz");

        var instances = await ReadInstancesAsync(context, settings);
        foreach (var instance in instances)
        {
            if (!instance.LatestModelApplied)
            {
                context.Logger.LogInformation("Applying the latest VMSS model to instance {InstanceId}.", instance.InstanceId);
                await WindowsApiPublisher.RunProcessAsync("az",
                [
                    "vmss", "update-instances",
                    "--subscription", settings.SubscriptionId,
                    "--resource-group", settings.ResourceGroupName,
                    "--name", settings.VmssName,
                    "--instance-ids", instance.InstanceId,
                    "--only-show-errors", "--output", "none"
                ], context.CancellationToken, ProcessTimeout);
            }
            if (!instance.IsRunning)
            {
                context.Logger.LogInformation("Starting VMSS instance {InstanceId}.", instance.InstanceId);
                await WindowsApiPublisher.RunProcessAsync("az",
                [
                    "vmss", "start",
                    "--subscription", settings.SubscriptionId,
                    "--resource-group", settings.ResourceGroupName,
                    "--name", settings.VmssName,
                    "--instance-ids", instance.InstanceId,
                    "--only-show-errors", "--output", "none"
                ], context.CancellationToken, ProcessTimeout);
            }
            context.Logger.LogInformation(
                "Installing the API on VMSS instance {InstanceId} ({ComputerName}, zone {Zone}).",
                instance.InstanceId,
                instance.ComputerName,
                instance.Zone);
            var result = await RunAzJsonAsync(
                context,
                [
                    "vmss", "run-command", "invoke",
                    "--subscription", settings.SubscriptionId,
                    "--resource-group", settings.ResourceGroupName,
                    "--name", settings.VmssName,
                    "--instance-id", instance.InstanceId,
                    "--command-id", "RunShellScript",
                    "--scripts", $"@{scriptPath}",
                    "--only-show-errors",
                    "--output", "json"
                ]);
            var message = string.Join(
                "\n",
                result.TryGetProperty("value", out var values)
                    ? values.EnumerateArray().Select(value => GetString(value, "message") ?? string.Empty)
                    : []);
            if (!message.Contains(DeployOkMarker, StringComparison.Ordinal))
            {
                var excerpt = message.Length > 2000 ? message[^2000..] : message;
                throw new InvalidOperationException(
                    $"VMSS instance {instance.InstanceId} did not report a healthy API after install. Run Command output: {excerpt}");
            }
        }

        context.Summary.Add("VM API release", $"{settings.StorageAccountName}/{settings.PackageContainerName}/{release}");
    }

    /// <summary>
    /// Resolves the managed identity client ID and vault URI, then fills the shell template
    /// with package and SQL connection settings without retrieving Redis secret values.
    /// </summary>
    static async Task<string> RenderBootstrapAsync(PipelineStepContext context, Settings settings)
    {
        var identity = await RunAzJsonAsync(
            context,
            [
                "identity", "show",
                "--subscription", settings.SubscriptionId,
                "--resource-group", settings.ResourceGroupName,
                "--name", settings.IdentityName,
                "--query", "{clientId:clientId}",
                "--only-show-errors",
                "--output", "json"
            ]);
        var vault = await RunAzJsonAsync(
            context,
            [
                "keyvault", "show",
                "--subscription", settings.SubscriptionId,
                "--resource-group", settings.ResourceGroupName,
                "--name", settings.KeyVaultName,
                "--query", "{vaultUri:properties.vaultUri}",
                "--only-show-errors",
                "--output", "json"
            ]);

        var sqlConnection =
            $"Server=tcp:{settings.SqlServerName}.database.windows.net,1433;Initial Catalog={settings.SqlDatabaseName};Encrypt=True;TrustServerCertificate=False;Authentication=Active Directory Default;";
        var script = ReadBootstrapTemplate(settings.BootstrapTemplatePath)
            .Replace("{{STORAGE_ACCOUNT}}", settings.StorageAccountName, StringComparison.Ordinal)
            .Replace("{{PACKAGE_CONTAINER}}", settings.PackageContainerName, StringComparison.Ordinal)
            .Replace("{{CLIENT_ID}}", GetRequiredString(identity, "clientId"), StringComparison.Ordinal)
            .Replace("{{SQL_CONNECTION}}", sqlConnection, StringComparison.Ordinal)
            .Replace("{{KEYVAULT_URI}}", GetRequiredString(vault, "vaultUri"), StringComparison.Ordinal);
        if (script.Contains("{{", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The VM bootstrap script still contains unrendered placeholders.");
        }
        return script;
    }

    /// <summary>
    /// Uploads a package using the deployer's Entra credentials. Retries only recognized
    /// authorization failures while the newly assigned storage role propagates.
    /// </summary>
    static async Task UploadBlobAsync(PipelineStepContext context, Settings settings, string packagePath, string blobName)
    {
        // The deployer's Storage Blob Data Contributor assignment is created by the same deployment
        // and can take a few minutes to propagate, so authorization failures are retried.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await WindowsApiPublisher.RunProcessAsync(
                    "az",
                    [
                        "storage", "blob", "upload",
                        "--subscription", settings.SubscriptionId,
                        "--auth-mode", "login",
                        "--account-name", settings.StorageAccountName,
                        "--container-name", settings.PackageContainerName,
                        "--name", blobName,
                        "--file", packagePath,
                        "--overwrite", "true",
                        "--only-show-errors",
                        "--output", "none"
                    ],
                    context.CancellationToken,
                    ProcessTimeout);
                return;
            }
            catch (InvalidOperationException exception) when (attempt < 12 && IsAuthorizationFailure(exception))
            {
                context.Logger.LogWarning(
                    "Blob upload not yet authorized (attempt {Attempt}); waiting for RBAC propagation.",
                    attempt);
                await Task.Delay(TimeSpan.FromSeconds(20), context.CancellationToken);
            }
        }
    }

    /// <summary>Identifies CLI authorization errors eligible for the bounded storage RBAC retry.</summary>
    static bool IsAuthorizationFailure(Exception exception) =>
        exception.Message.Contains("AuthorizationPermissionMismatch", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("AuthorizationFailure", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("not authorized", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("403", StringComparison.Ordinal);

    /// <summary>
    /// Reads VM model and power state and requires exactly one instance in each expected zone.
    /// Returns instances ordered by zone for deterministic deployment.
    /// </summary>
    static async Task<IReadOnlyList<VmssInstance>> ReadInstancesAsync(PipelineStepContext context, Settings settings)
    {
        var json = await RunAzJsonAsync(
            context,
            [
                "vmss", "list-instances",
                "--subscription", settings.SubscriptionId,
                "--resource-group", settings.ResourceGroupName,
                "--name", settings.VmssName,
                "--expand", "instanceView",
                "--only-show-errors",
                "--output", "json"
            ]);
        var instances = json.EnumerateArray()
            .Select(item => new VmssInstance(
                GetRequiredString(item, "instanceId"),
                item.TryGetProperty("zones", out var instanceZones) && instanceZones.GetArrayLength() == 1
                    ? instanceZones[0].GetString() ?? "none" : "none",
                item.TryGetProperty("osProfile", out var osProfile)
                    ? GetString(osProfile, "computerName") ?? "unknown" : "unknown",
                item.TryGetProperty("latestModelApplied", out var latestModel) && latestModel.ValueKind == JsonValueKind.True,
                item.TryGetProperty("instanceView", out var instanceView) &&
                    instanceView.TryGetProperty("statuses", out var statuses) &&
                    statuses.EnumerateArray().Any(status => GetString(status, "code") == "PowerState/running")))
            .OrderBy(instance => instance.Zone, StringComparer.Ordinal)
            .ToList();

        var zones = instances.Select(instance => instance.Zone).OrderBy(zone => zone, StringComparer.Ordinal).ToArray();
        if (!zones.SequenceEqual(ExpectedZones))
        {
            throw new InvalidOperationException(
                $"VM scale set '{settings.VmssName}' must have exactly one instance in each of zones {string.Join(", ", ExpectedZones)} (found: {string.Join(",", zones)}).");
        }
        return instances;
    }

    /// <summary>
    /// Resolves the load balancer DNS and waits for healthy API responses from both zones.
    /// Fresh TCP connections exercise backend selection rather than a single pooled connection.
    /// </summary>
    static async Task VerifyAsync(PipelineStepContext context, Settings settings)
    {
        var publicIp = await RunAzJsonAsync(
            context,
            [
                "network", "public-ip", "show",
                "--subscription", settings.SubscriptionId,
                "--resource-group", settings.ResourceGroupName,
                "--name", settings.PublicIpName,
                "--query", "{fqdn:dnsSettings.fqdn}",
                "--only-show-errors",
                "--output", "json"
            ]);
        var baseUri = new UriBuilder(Uri.UriSchemeHttp, GetRequiredString(publicIp, "fqdn")).Uri;
        var instances = await ReadInstancesAsync(context, settings);

        using var client = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.Zero });
        client.Timeout = TimeSpan.FromSeconds(10);
        var servedBy = new HashSet<string>(StringComparer.Ordinal);
        var healthOk = false;
        var deadline = DateTimeOffset.UtcNow + ReadinessTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            healthOk |= await GetStatusAsync(client, new Uri(baseUri, "health"), context.CancellationToken) == HttpStatusCode.OK;

            // New TCP connection per request so the load balancer hash spreads requests across instances.
            for (var i = 0; i < 20; i++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "health"));
                request.Headers.ConnectionClose = true;
                try
                {
                    using var response = await client.SendAsync(request, context.CancellationToken);
                    if (response.StatusCode == HttpStatusCode.OK &&
                        response.Headers.TryGetValues("X-Served-By", out var values))
                    {
                        servedBy.UnionWith(values);
                    }
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException &&
                                                  !context.CancellationToken.IsCancellationRequested)
                {
                    context.Logger.LogDebug(exception, "Waiting for healthy VM API responses from every zone.");
                }
            }

            var zonesSeen = servedBy.Select(value => value[(value.LastIndexOf("/zone-", StringComparison.Ordinal) + 6)..])
                .ToHashSet(StringComparer.Ordinal);
            if (healthOk && ExpectedZones.All(zonesSeen.Contains))
            {
                context.Summary.Add("VM API endpoint", baseUri.ToString());
                context.Summary.Add("VM API instances", string.Join(", ", servedBy.Order(StringComparer.Ordinal)));
                context.Logger.LogInformation(
                    "VM API healthy behind {Endpoint}; served by {Instances} (VMSS instances: {Count}).",
                    baseUri,
                    string.Join(", ", servedBy),
                    instances.Count);
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(10), context.CancellationToken);
        }

        throw new TimeoutException(
            $"The VM API at {baseUri} did not serve healthy responses from zones {string.Join(", ", ExpectedZones)} within 10 minutes (health={healthOk}, seen={string.Join(",", servedBy)}).");
    }

    /// <summary>
    /// Reads an endpoint status for readiness polling; transient HTTP failures return no status,
    /// while caller cancellation still propagates.
    /// </summary>
    static async Task<HttpStatusCode?> GetStatusAsync(HttpClient client, Uri endpoint, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(endpoint, cancellationToken);
            return response.StatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException &&
                                          !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Runs Azure CLI with the process timeout and returns detached JSON or a parsing error.</summary>
    static async Task<JsonElement> RunAzJsonAsync(PipelineStepContext context, string[] arguments)
    {
        var result = await WindowsApiPublisher.RunProcessAsync("az", arguments, context.CancellationToken, ProcessTimeout);
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Azure CLI returned invalid JSON during the VM API deployment.", exception);
        }
    }

    /// <summary>Reads an optional JSON string without treating missing or differently typed values as strings.</summary>
    static string? GetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    /// <summary>Reads a required Azure response string and fails explicitly when it is absent.</summary>
    static string GetRequiredString(JsonElement element, string propertyName) =>
        GetString(element, propertyName)
        ?? throw new InvalidOperationException($"Azure returned no '{propertyName}' value during the VM API deployment.");

    sealed record VmssInstance(string InstanceId, string Zone, string ComputerName, bool LatestModelApplied, bool IsRunning);
}
