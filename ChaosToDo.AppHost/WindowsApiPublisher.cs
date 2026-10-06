using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Xml.Linq;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Logging;

#pragma warning disable ASPIREPIPELINES001
namespace ChaosToDo.AppHost;

internal static class WindowsApiPublisher
{
    internal const string PackageStep = "build-windows-api-package";
    const string ValidateStep = "validate-windows-api-site";
    const string DeployStep = "deploy-windows-api";
    const string VerifyStep = "verify-windows-api";
    const string CodeOnlyDeployStep = "deploy-windows-api-code";
    static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(15);
    static readonly TimeSpan DeploymentTimeout = TimeSpan.FromMinutes(10);
    static readonly TimeSpan ReadinessTimeout = TimeSpan.FromMinutes(10);

    public static void Register(
        IResourceBuilder<ProjectResource> api,
        string siteName,
        string planName,
        string identityName,
        string keyVaultName,
        int expectedWorkerCount,
        string subscriptionId,
        string resourceGroupName)
    {
        if (!Guid.TryParse(subscriptionId, out var parsedSubscriptionId))
        {
            throw new InvalidOperationException("Azure:SubscriptionId must be a GUID for Windows App Service deployment.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceGroupName);

        var subscription = parsedSubscriptionId.ToString();
        var packageDirectory = Path.Combine(
            Path.GetTempPath(), "ChaosToDo", "windows-api", Guid.NewGuid().ToString("N"));
        var packagePath = Path.Combine(packageDirectory, "chaos-todo-api-win-x64.zip");
        api.WithPipelineStepFactory(
            PackageStep,
            context => BuildPackageAsync(context, api.Resource, packageDirectory, packagePath),
            requiredBy: [WellKnownPipelineSteps.Publish, WellKnownPipelineSteps.Deploy],
            description: "Publish the API as framework-dependent win-x64 IIS output and package the ZIP.");
        api.WithPipelineStepFactory(
            ValidateStep,
            context => ValidateExistingSiteAsync(
                context,
                siteName,
                planName,
                identityName,
                keyVaultName,
                expectedWorkerCount,
                subscription,
                resourceGroupName),
            requiredBy: [CodeOnlyDeployStep],
            description: "Read-only validation of the existing Windows App Service site, plan, and identity.");
        api.WithPipelineStepFactory(
            DeployStep,
            context => DeployAsync(
                context,
                siteName,
                planName,
                identityName,
                keyVaultName,
                expectedWorkerCount,
                subscription,
                resourceGroupName,
                packagePath),
            dependsOn: [PackageStep],
            requiredBy: [WellKnownPipelineSteps.Deploy],
            description: "Upload the API ZIP to the provisioned Windows App Service and wait for readiness.");
        api.WithPipelineStepFactory(
            VerifyStep,
            context => VerifyReadinessAsync(context, siteName, subscription, resourceGroupName),
            dependsOn: [DeployStep],
            requiredBy: [WellKnownPipelineSteps.Deploy],
            description: "Verify the deployed API health and liveness endpoints.");
        api.WithPipelineStepFactory(
            CodeOnlyDeployStep,
            context => DeployCodeOnlyAsync(
                context,
                siteName,
                subscription,
                resourceGroupName,
                packagePath),
            dependsOn: [PackageStep, ValidateStep],
            description: "Deploy API code to an existing, validated Windows site without provisioning infrastructure.");

    }

    static async Task BuildPackageAsync(
        PipelineStepContext context,
        ProjectResource api,
        string outputRoot,
        string zipPath)
    {
        var projectPath = api.GetProjectMetadata().ProjectPath;
        if (!File.Exists(projectPath))
        {
            throw new FileNotFoundException("Aspire project metadata resolved to a missing API project.", projectPath);
        }

        var publishDirectory = Path.Combine(outputRoot, "publish");
        Directory.CreateDirectory(outputRoot);
        Directory.CreateDirectory(publishDirectory);

        await RunProcessAsync(
            "dotnet",
            [
                "publish", projectPath,
                "--configuration", "Release",
                "--runtime", "win-x64",
                "--self-contained", "false",
                "--output", publishDirectory,
                "-p:AspNetCoreHostingModel=OutOfProcess",
                "-p:UseAppHost=true"
            ],
            context.CancellationToken,
            ProcessTimeout);

        var webConfig = Path.Combine(publishDirectory, "web.config");
        if (!File.Exists(webConfig))
        {
            throw new InvalidOperationException("The API publish output is missing web.config for IIS hosting.");
        }
        var webConfigDocument = XDocument.Parse(await File.ReadAllTextAsync(webConfig, context.CancellationToken));
        var aspNetCore = webConfigDocument.Descendants("aspNetCore").SingleOrDefault();
        if (!string.Equals((string?)aspNetCore?.Attribute("hostingModel"), "OutOfProcess", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals((string?)aspNetCore?.Attribute("processPath"), @".\ChaosToDo.Api.exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(publishDirectory, "ChaosToDo.Api.exe")))
        {
            throw new InvalidOperationException("The Windows API package must configure IIS out-of-process hosting with ChaosToDo.Api.exe for explicit Chaos process targeting.");
        }

        await ZipFile.CreateFromDirectoryAsync(publishDirectory, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
        await using (var archive = await ZipFile.OpenReadAsync(zipPath))
        {
            if (archive.GetEntry("web.config") is null ||
                archive.Entries.Any(entry =>
                    entry.FullName.StartsWith("/", StringComparison.Ordinal) ||
                    entry.FullName.Split('/').Any(segment => segment is "." or "..")))
            {
                throw new InvalidOperationException("The Windows API ZIP is not rooted at the publish directory or contains an unsafe entry path.");
            }
        }
        context.Summary.Add("Windows API package", zipPath);
        context.Logger.LogInformation("Created framework-dependent win-x64 API package at {PackagePath}.", zipPath);
    }

    static async Task ValidateExistingSiteAsync(
        PipelineStepContext context,
        string siteName,
        string planName,
        string identityName,
        string keyVaultName,
        int expectedWorkerCount,
        string subscriptionId,
        string resourceGroupName)
    {
        var expectedIdentityId =
            $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/Microsoft.ManagedIdentity/userAssignedIdentities/{identityName}";
        await ValidateSiteAsync(
            context,
            siteName,
            planName,
            identityName,
            keyVaultName,
            expectedWorkerCount,
            expectedIdentityId,
            subscriptionId,
            resourceGroupName);
        context.Summary.Add("Existing Windows API site", "Validated without provisioning or modifying Azure resources.");
    }

    static async Task DeployAsync(
        PipelineStepContext context,
        string siteName,
        string planName,
        string identityName,
        string keyVaultName,
        int expectedWorkerCount,
        string subscriptionId,
        string resourceGroupName,
        string packagePath)
    {
        var expectedIdentityId =
            $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/Microsoft.ManagedIdentity/userAssignedIdentities/{identityName}";
        await ValidateSiteAsync(
            context,
            siteName,
            planName,
            identityName,
            keyVaultName,
            expectedWorkerCount,
            expectedIdentityId,
            subscriptionId,
            resourceGroupName);
        await UploadPackageAsync(
            context,
            siteName,
            subscriptionId,
            resourceGroupName,
            packagePath);
    }

    static async Task DeployCodeOnlyAsync(
        PipelineStepContext context,
        string siteName,
        string subscriptionId,
        string resourceGroupName,
        string packagePath)
    {
        await UploadPackageAsync(
            context,
            siteName,
            subscriptionId,
            resourceGroupName,
            packagePath);
        await VerifyReadinessAsync(context, siteName, subscriptionId, resourceGroupName);
    }

    static async Task UploadPackageAsync(
        PipelineStepContext context,
        string siteName,
        string subscriptionId,
        string resourceGroupName,
        string zipPath)
    {
        if (!File.Exists(zipPath))
        {
            throw new FileNotFoundException("The Windows API package step did not produce its expected ZIP.", zipPath);
        }

        context.Logger.LogInformation(
            "Deploying the API ZIP to Windows App Service {SiteName} in resource group {ResourceGroupName}.",
            siteName,
            resourceGroupName);
        await RunProcessAsync(
            "az",
            [
                "webapp", "deploy",
                "--type", "zip",
                "--src-path", zipPath,
                "--subscription", subscriptionId,
                "--resource-group", resourceGroupName,
                "--name", siteName,
                "--async", "false",
                "--timeout", ((int)DeploymentTimeout.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--restart", "true",
                "--only-show-errors",
                "--output", "json"
            ],
            context.CancellationToken,
            DeploymentTimeout);

    }

    static async Task VerifyReadinessAsync(
        PipelineStepContext context,
        string siteName,
        string subscriptionId,
        string resourceGroupName)
    {
        var site = await ReadSiteAsync(context, siteName, subscriptionId, resourceGroupName);
        var hostname = GetRequiredString(site, "defaultHostName");
        var baseUri = new UriBuilder(Uri.UriSchemeHttps, hostname).Uri;
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var deadline = DateTimeOffset.UtcNow + ReadinessTimeout;
        string? lastStatus = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var healthReady = await IsHealthyAsync(client, new Uri(baseUri, "health"), context.CancellationToken);
            var aliveReady = await IsHealthyAsync(client, new Uri(baseUri, "alive"), context.CancellationToken);
            if (healthReady && aliveReady)
            {
                context.Summary.Add("Windows API readiness", $"Healthy at https://{hostname}/health and /alive.");
                return;
            }

            lastStatus = $"health={healthReady}, alive={aliveReady}";
            await Task.Delay(TimeSpan.FromSeconds(10), context.CancellationToken);
        }

        throw new TimeoutException(
            $"Windows API site '{siteName}' did not pass /health and /alive within 10 minutes (last result: {lastStatus ?? "no response"}).");
    }

    static async Task<bool> IsHealthyAsync(HttpClient client, Uri endpoint, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(endpoint, cancellationToken);
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    static async Task ValidateSiteAsync(
        PipelineStepContext context,
        string siteName,
        string planName,
        string identityName,
        string keyVaultName,
        int expectedWorkerCount,
        string expectedIdentityId,
        string subscriptionId,
        string resourceGroupName)
    {
        var site = await ReadSiteAsync(context, siteName, subscriptionId, resourceGroupName);
        EnsureEquals(GetRequiredString(site, "name"), siteName, "site name");
        EnsureEquals(GetRequiredBoolean(site, "httpsOnly"), true, "HTTPS-only setting");
        if (GetOptionalString(site, "kind")?.Contains("linux", StringComparison.OrdinalIgnoreCase) == true ||
            GetOptionalBoolean(site, "reserved") == true)
        {
            throw new InvalidOperationException($"Existing site '{siteName}' is not a Windows App Service site.");
        }

        var serverFarmId = GetRequiredString(site, "serverFarmId");
        if (!serverFarmId.EndsWith($"/serverfarms/{planName}", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Existing site '{siteName}' is not attached to expected plan '{planName}'.");
        }

        var identity = await ReadIdentityAsync(context, identityName, subscriptionId, resourceGroupName);
        var expectedClientId = GetRequiredString(identity, "clientId");
        var expectedPrincipalId = GetRequiredString(identity, "principalId");
        var attachedIdentities = site.GetProperty("identity").GetProperty("userAssignedIdentities");
        if (!attachedIdentities.EnumerateObject().Any(property =>
                string.Equals(property.Name, expectedIdentityId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Existing site '{siteName}' does not have the expected shared user-assigned identity.");
        }

        var referenceIdentity = GetOptionalString(site, "keyVaultReferenceIdentity")
            ?? GetNestedOptionalString(site, "properties", "keyVaultReferenceIdentity");
        EnsureEquals(referenceIdentity, expectedIdentityId, "Key Vault reference identity");

        var vaultId =
            $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/Microsoft.KeyVault/vaults/{keyVaultName}";
        var keyVaultRoles = await ReadKeyVaultSecretsUserAssignmentsAsync(
            context,
            expectedPrincipalId,
            vaultId,
            subscriptionId);
        if (!keyVaultRoles.EnumerateArray().Any(role =>
                string.Equals(role.GetString(), "Key Vault Secrets User", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Shared identity '{identityName}' is missing Key Vault Secrets User on the Redis vault.");
        }

        var config = await ReadWebConfigAsync(context, siteName, subscriptionId, resourceGroupName);
        EnsureEquals(GetRequiredString(config, "netFrameworkVersion"), "v10.0", ".NET runtime stack");
        if (!string.IsNullOrEmpty(GetOptionalString(config, "linuxFxVersion")))
        {
            throw new InvalidOperationException($"Existing site '{siteName}' has Linux container runtime configuration.");
        }
        EnsureEquals(GetRequiredBoolean(config, "alwaysOn"), true, "Always On setting");
        EnsureEquals(GetRequiredBoolean(config, "use32BitWorkerProcess"), false, "64-bit worker setting");
        EnsureEquals(GetRequiredString(config, "healthCheckPath"), "/health", "health check path");
        EnsureEquals(GetRequiredString(config, "ftpsState"), "Disabled", "FTPS setting");
        EnsureEquals(GetRequiredString(config, "minTlsVersion"), "1.2", "minimum TLS version");
        EnsureEquals(GetRequiredString(config, "scmMinTlsVersion"), "1.2", "SCM minimum TLS version");

        var plan = await ReadPlanAsync(context, planName, subscriptionId, resourceGroupName);
        EnsureEquals(GetRequiredString(plan, "name"), planName, "plan name");
        EnsureEquals(GetRequiredString(plan, "sku"), "P1v3", "plan SKU");
        EnsureEquals(GetRequiredBoolean(plan, "reserved"), false, "Windows plan setting");
        EnsureEquals(GetRequiredBoolean(plan, "zoneRedundant"), false, "zone-redundant plan setting");
        if (GetRequiredInt32(plan, "numberOfWorkers") != expectedWorkerCount)
        {
            throw new InvalidOperationException(
                $"Existing plan '{planName}' has a different worker count than the configured expectation ({expectedWorkerCount}).");
        }

        var appSettings = await ReadAppSettingsAsync(context, siteName, subscriptionId, resourceGroupName);
        EnsureAppSetting(appSettings, "ASPNETCORE_ENVIRONMENT", "Production");
        EnsureAppSetting(appSettings, "AZURE_CLIENT_ID", expectedClientId);
        EnsureAppSetting(appSettings, "AZURE_TOKEN_CREDENTIALS", "ManagedIdentityCredential");
        EnsureAppSetting(appSettings, "HealthChecks__ExposeEndpoints", "true");
        EnsureAppSetting(appSettings, "SCM_DO_BUILD_DURING_DEPLOYMENT", "false");
        EnsureAppSetting(appSettings, "Demo__ServedByHeader", "true");
        var connectionSettingNames = await ReadConnectionSettingNamesAsync(
            context,
            siteName,
            subscriptionId,
            resourceGroupName);
        EnsureContains(connectionSettingNames, "ConnectionStrings__database", "SQL connection setting");
        EnsureContains(connectionSettingNames, "ConnectionStrings__cache", "Redis Key Vault reference setting");

        foreach (var policyName in new[] { "ftp", "scm" })
        {
            var policy = await ReadPublishingCredentialsPolicyAsync(
                context,
                siteName,
                policyName,
                subscriptionId,
                resourceGroupName);
            EnsureEquals(GetRequiredBoolean(policy, "allow"), false, $"{policyName} basic publishing authentication");
        }
    }

    static async Task<JsonElement> ReadSiteAsync(
        PipelineStepContext context,
        string siteName,
        string subscriptionId,
        string resourceGroupName) =>
        await RunAzJsonAsync(
            context,
            [
                "webapp", "show",
                "--subscription", subscriptionId,
                "--resource-group", resourceGroupName,
                "--name", siteName,
                "--query", "{name:name,kind:kind,reserved:reserved,httpsOnly:httpsOnly,serverFarmId:serverFarmId,defaultHostName:defaultHostName,keyVaultReferenceIdentity:properties.keyVaultReferenceIdentity || keyVaultReferenceIdentity,identity:identity}",
                "--only-show-errors",
                "--output", "json"
            ]);

    static async Task<JsonElement> ReadWebConfigAsync(
        PipelineStepContext context,
        string siteName,
        string subscriptionId,
        string resourceGroupName) =>
        await RunAzJsonAsync(
            context,
            [
                "webapp", "config", "show",
                "--subscription", subscriptionId,
                "--resource-group", resourceGroupName,
                "--name", siteName,
                "--query", "{netFrameworkVersion:netFrameworkVersion,linuxFxVersion:linuxFxVersion,alwaysOn:alwaysOn,use32BitWorkerProcess:use32BitWorkerProcess,healthCheckPath:healthCheckPath,ftpsState:ftpsState,minTlsVersion:minTlsVersion,scmMinTlsVersion:scmMinTlsVersion}",
                "--only-show-errors",
                "--output", "json"
            ]);

    static async Task<JsonElement> ReadPublishingCredentialsPolicyAsync(
        PipelineStepContext context,
        string siteName,
        string policyName,
        string subscriptionId,
        string resourceGroupName) =>
        await RunAzJsonAsync(
            context,
            [
                "resource", "show",
                "--ids", $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/Microsoft.Web/sites/{siteName}/basicPublishingCredentialsPolicies/{policyName}",
                "--api-version", "2022-03-01",
                "--query", "{allow:properties.allow}",
                "--only-show-errors",
                "--output", "json"
            ]);

    static async Task<JsonElement> ReadPlanAsync(
        PipelineStepContext context,
        string planName,
        string subscriptionId,
        string resourceGroupName) =>
        await RunAzJsonAsync(
            context,
            [
                "rest", "--method", "get",
                "--url", $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{Uri.EscapeDataString(resourceGroupName)}/providers/Microsoft.Web/serverfarms/{Uri.EscapeDataString(planName)}?api-version=2024-04-01",
                "--query", "{name:name,sku:sku.name,reserved:properties.reserved,numberOfWorkers:sku.capacity,zoneRedundant:properties.zoneRedundant}",
                "--only-show-errors",
                "--output", "json"
            ]);

    static async Task<JsonElement> ReadIdentityAsync(
        PipelineStepContext context,
        string identityName,
        string subscriptionId,
        string resourceGroupName) =>
        await RunAzJsonAsync(
            context,
            [
                "identity", "show",
                "--subscription", subscriptionId,
                "--resource-group", resourceGroupName,
                "--name", identityName,
                "--query", "{clientId:clientId,principalId:principalId}",
                "--only-show-errors",
                "--output", "json"
            ]);

    static async Task<JsonElement> ReadKeyVaultSecretsUserAssignmentsAsync(
        PipelineStepContext context,
        string principalId,
        string vaultId,
        string subscriptionId) =>
        await RunAzJsonAsync(
            context,
            [
                "role", "assignment", "list",
                "--subscription", subscriptionId,
                "--assignee-object-id", principalId,
                "--scope", vaultId,
                "--fill-principal-name", "false",
                "--query", "[?roleDefinitionName=='Key Vault Secrets User'].roleDefinitionName",
                "--only-show-errors",
                "--output", "json"
            ]);

    static async Task<JsonElement> ReadConnectionSettingNamesAsync(
        PipelineStepContext context,
        string siteName,
        string subscriptionId,
        string resourceGroupName) =>
        await RunAzJsonAsync(
            context,
            [
                "webapp", "config", "appsettings", "list",
                "--subscription", subscriptionId,
                "--resource-group", resourceGroupName,
                "--name", siteName,
                "--query", "[?name=='ConnectionStrings__database' || name=='ConnectionStrings__cache'].name",
                "--only-show-errors",
                "--output", "json"
            ]);

    static async Task<JsonElement> ReadAppSettingsAsync(
        PipelineStepContext context,
        string siteName,
        string subscriptionId,
        string resourceGroupName) =>
        await RunAzJsonAsync(
            context,
            [
                "webapp", "config", "appsettings", "list",
                "--subscription", subscriptionId,
                "--resource-group", resourceGroupName,
                "--name", siteName,
                "--query", "[?name=='ASPNETCORE_ENVIRONMENT' || name=='AZURE_CLIENT_ID' || name=='AZURE_TOKEN_CREDENTIALS' || name=='HealthChecks__ExposeEndpoints' || name=='SCM_DO_BUILD_DURING_DEPLOYMENT' || name=='Demo__ServedByHeader'].{name:name,value:value}",
                "--only-show-errors",
                "--output", "json"
            ]);

    static async Task<JsonElement> RunAzJsonAsync(PipelineStepContext context, string[] arguments)
    {
        var result = await RunProcessAsync("az", arguments, context.CancellationToken, ProcessTimeout);
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Azure CLI returned invalid JSON while validating the Windows API site.", exception);
        }
    }

    internal static async Task<ProcessResult> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        var startInfo = CreateProcessStartInfo(executable, arguments);
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.CreateNoWindow = true;

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start '{executable}' for the Windows API deployment step.");
        }

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        var outputTask = process.StandardOutput.ReadToEndAsync(linkedSource.Token);
        var errorTask = process.StandardError.ReadToEndAsync(linkedSource.Token);
        try
        {
            await process.WaitForExitAsync(linkedSource.Token);
            var standardOutput = await outputTask;
            var standardError = await errorTask;
            if (process.ExitCode != 0)
            {
                var safeDetails = SanitizeDiagnostics(standardError);
                throw new InvalidOperationException(
                    $"{executable} exited with code {process.ExitCode}." +
                    (safeDetails.Length == 0 ? string.Empty : $" {safeDetails}"));
            }
            return new ProcessResult(standardOutput, standardError);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            throw new TimeoutException($"'{executable}' exceeded the {timeout.TotalMinutes:0}-minute execution limit.");
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            throw;
        }
    }

    static ProcessStartInfo CreateProcessStartInfo(string executable, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (OperatingSystem.IsWindows() && string.Equals(executable, "az", StringComparison.OrdinalIgnoreCase))
        {
            var pathDirectories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(directory => directory.Trim('"'))
                .ToArray();
            var nativeCli = pathDirectories
                .Select(directory => Path.Combine(directory, "az.exe"))
                .FirstOrDefault(File.Exists);
            if (nativeCli is not null)
            {
                startInfo.FileName = nativeCli;
            }
            else
            {
                var cliScript = pathDirectories
                    .Select(directory => Path.Combine(directory, "az.cmd"))
                    .FirstOrDefault(File.Exists);
                if (cliScript is null)
                {
                    throw new InvalidOperationException("Azure CLI was not found on PATH.");
                }

                var cliPython = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(cliScript)!, "..", "python.exe"));
                if (!File.Exists(cliPython))
                {
                    throw new InvalidOperationException(
                        "The Azure CLI Windows launcher was found, but its Python runtime could not be resolved.");
                }

                startInfo.FileName = cliPython;
                startInfo.ArgumentList.Add("-IBm");
                startInfo.ArgumentList.Add("azure.cli");
            }
        }
        else
        {
            startInfo.FileName = executable;
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    static string SanitizeDiagnostics(string diagnostics)
    {
        var compact = diagnostics.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (compact.Length > 1200)
        {
            compact = compact[..1200];
        }
        return System.Text.RegularExpressions.Regex.Replace(
            compact,
            @"(?i)(authorization\s*:\s*bearer\s+|access[_ -]?token\s*[=:]\s*|password\s*[=:]\s*)\S+",
            "$1<redacted>");
    }

    static void EnsureAppSetting(JsonElement settings, string name, string expected)
    {
        var value = settings.EnumerateArray()
            .FirstOrDefault(item => string.Equals(
                GetOptionalString(item, "name"),
                name,
                StringComparison.OrdinalIgnoreCase));
        EnsureEquals(GetOptionalString(value, "value"), expected, $"app setting {name}");
    }

    static void EnsureContains(JsonElement values, string expected, string description)
    {
        if (values.ValueKind != JsonValueKind.Array ||
            !values.EnumerateArray().Any(value =>
                value.ValueKind == JsonValueKind.String &&
                string.Equals(value.GetString(), expected, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"The Windows API site is missing the expected {description}.");
        }
    }

    static string GetRequiredString(JsonElement element, string propertyName) =>
        GetOptionalString(element, propertyName)
        ?? throw new InvalidOperationException($"Azure returned no '{propertyName}' value while validating the Windows API site.");

    static string? GetOptionalString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    static string? GetNestedOptionalString(JsonElement element, string parentName, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(parentName, out var parent)
            ? GetOptionalString(parent, propertyName)
            : null;

    static bool GetRequiredBoolean(JsonElement element, string propertyName) =>
        GetOptionalBoolean(element, propertyName)
        ?? throw new InvalidOperationException($"Azure returned no '{propertyName}' value while validating the Windows API site.");

    static bool? GetOptionalBoolean(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : null;

    static int GetRequiredInt32(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var property) &&
        property.TryGetInt32(out var value)
            ? value
            : throw new InvalidOperationException($"Azure returned no '{propertyName}' value while validating the Windows API site.");

    static void EnsureEquals(string? actual, string expected, string description)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Existing Windows API {description} is '{actual ?? "(missing)"}'; expected '{expected}'.");
        }
    }

    static void EnsureEquals(bool actual, bool expected, string description)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException($"Existing Windows API {description} is '{actual}'; expected '{expected}'.");
        }
    }

    internal sealed record ProcessResult(string StandardOutput, string StandardError);
}
