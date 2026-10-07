using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Core;
using Azure.Identity;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Logging;

#pragma warning disable ASPIREPIPELINES001
namespace ChaosToDo.AppHost;

/// <summary>Publishes the SQL HA failover runbook and provides authenticated ARM request handling.</summary>
internal static partial class SqlFailoverRunbookPublisher
{
    const string ArmEndpoint = "https://management.azure.com";
    const string TokenScope = "https://management.azure.com/.default";
    const string IdentityApiVersion = "2023-01-31";
    const string AutomationApiVersion = "2023-11-01";
    const string PublishApiVersion = "2015-10-31";
    const string RunbookName = "sql-local-ha-failover";
    static readonly TimeSpan PublishTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Resolves the SQL target and failover identity, renders the runbook, imports its draft
    /// and publishes it. Verifies both draft and published content without starting an Automation job.
    /// </summary>
    public static async Task PublishAsync(
        PipelineStepContext context,
        string subscriptionId,
        string resourceGroupName,
        string automationAccountName,
        string identityName,
        string sqlServerName,
        string sqlDatabaseName,
        string runbookSource)
    {
        if (!Guid.TryParse(subscriptionId, out var parsedSubscriptionId))
        {
            throw new InvalidOperationException("Azure:SubscriptionId must be a GUID to publish the SQL failover runbook.");
        }

        var escapedSubscriptionId = parsedSubscriptionId.ToString();
        var escapedResourceGroupName = Uri.EscapeDataString(resourceGroupName);
        var identityUri = new Uri(
            $"{ArmEndpoint}/subscriptions/{escapedSubscriptionId}/resourceGroups/{escapedResourceGroupName}/providers/Microsoft.ManagedIdentity/userAssignedIdentities/{Uri.EscapeDataString(identityName)}?api-version={IdentityApiVersion}");
        var databaseId =
            $"/subscriptions/{escapedSubscriptionId}/resourceGroups/{resourceGroupName}/providers/Microsoft.Sql/servers/{sqlServerName}/databases/{sqlDatabaseName}";
        var databaseUri = new Uri($"{ArmEndpoint}{databaseId}?api-version=2023-08-01");
        var runbookPath =
            $"/subscriptions/{escapedSubscriptionId}/resourceGroups/{escapedResourceGroupName}/providers/Microsoft.Automation/automationAccounts/{Uri.EscapeDataString(automationAccountName)}/runbooks/{RunbookName}";
        var draftUri = new Uri($"{ArmEndpoint}{runbookPath}/draft/content?api-version={AutomationApiVersion}");
        var publishUri = new Uri($"{ArmEndpoint}{runbookPath}/draft/publish?api-version={PublishApiVersion}");
        var publishedContentUri = new Uri($"{ArmEndpoint}{runbookPath}/content?api-version={AutomationApiVersion}");

        var credential = new DefaultAzureCredential();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        client.Timeout = TimeSpan.FromSeconds(60);
        var token = await credential.GetTokenAsync(
            new TokenRequestContext([TokenScope]),
            context.CancellationToken);

        var database = await SendArmAsync(client, token, HttpMethod.Get, databaseUri, null, context.CancellationToken);
        using (var databaseDocument = JsonDocument.Parse(database.Body))
        {
            var actualDatabaseId = databaseDocument.RootElement.GetProperty("id").GetString();
            if (!string.Equals(actualDatabaseId, databaseId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"SQL database lookup returned an unexpected resource ID: {actualDatabaseId ?? "(missing)"}.");
            }
            #pragma warning restore ASPIREPIPELINES001
        }

        var identity = await SendArmAsync(client, token, HttpMethod.Get, identityUri, null, context.CancellationToken);
        using var identityDocument = JsonDocument.Parse(identity.Body);
        var identityClientId = identityDocument.RootElement
            .GetProperty("properties").GetProperty("clientId").GetString();
        if (!Guid.TryParse(identityClientId, out var parsedIdentityClientId))
        {
            throw new InvalidOperationException("The SQL failover user-assigned identity returned an invalid client ID.");
        }

        const string databaseToken = "__SQL_DATABASE_RESOURCE_ID__";
        const string identityToken = "__FAILOVER_IDENTITY_CLIENT_ID__";
        if (!runbookSource.Contains(databaseToken, StringComparison.Ordinal) ||
            !runbookSource.Contains(identityToken, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The SQL failover runbook is missing a required ARM target placeholder.");
        }

        var content = runbookSource
            .Replace(databaseToken, databaseId, StringComparison.Ordinal)
            .Replace(identityToken, parsedIdentityClientId.ToString(), StringComparison.Ordinal);
        if (content.Contains("__SQL_DATABASE_RESOURCE_ID__", StringComparison.Ordinal) ||
            content.Contains("__FAILOVER_IDENTITY_CLIENT_ID__", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The SQL failover runbook contains an unresolved ARM target placeholder.");
        }

        context.Logger.LogInformation(
            "Importing and publishing runbook {RunbookName} for SQL database {DatabaseId}.",
            RunbookName,
            databaseId);

        await SendArmAsync(
            client,
            token,
            HttpMethod.Put,
            draftUri,
            new StringContent(content, Encoding.UTF8, "text/powershell"),
            context.CancellationToken);
        var draft = await SendArmAsync(
            client,
            token,
            HttpMethod.Get,
            draftUri,
            null,
            context.CancellationToken,
            "text/powershell");
        EnsureContentMatches(draft.Body, content, "draft");

        var publishResponse = await SendArmAsync(
            client,
            token,
            HttpMethod.Post,
            publishUri,
            null,
            context.CancellationToken);

        if (publishResponse.StatusCode == HttpStatusCode.Accepted)
        {
            if (publishResponse.Location is null)
            {
                throw new InvalidOperationException(
                    "Azure Automation accepted runbook publication without a Location header; publication status is unknown.");
            }

            var publishLocation = publishResponse.Location.IsAbsoluteUri
                ? publishResponse.Location
                : new Uri(new Uri(ArmEndpoint), publishResponse.Location);
            var operationUri = ValidatePublishOperationUri(
                publishLocation,
                escapedSubscriptionId,
                escapedResourceGroupName,
                automationAccountName);
            await WaitForPublishAsync(
                client,
                token,
                operationUri,
                publishResponse.RetryAfter,
                context.CancellationToken);
        }
        else if (publishResponse.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.NoContent))
        {
            throw new InvalidOperationException(
                $"Azure Automation returned unexpected runbook publish status {(int)publishResponse.StatusCode}: {publishResponse.Body}");
        }

        var publishedContent = await SendArmAsync(
            client,
            token,
            HttpMethod.Get,
            publishedContentUri,
            null,
            context.CancellationToken,
            "text/powershell");
        EnsureContentMatches(publishedContent.Body, content, "published");

        context.Summary.Add("SQL failover runbook", "Imported, published, and content-verified; no Automation job was started.");
    }

    /// <summary>
    /// Sends one authenticated ARM request, capturing body, Location and Retry-After for polling.
    /// Rejects unsuccessful responses and omits the URI query from the request error message.
    /// </summary>
    internal static async Task<ArmResponse> SendArmAsync(
        HttpClient client,
        AccessToken token,
        HttpMethod method,
        Uri uri,
        HttpContent? content,
        CancellationToken cancellationToken,
        string accept = "application/json")
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        request.Content = content;

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var location = response.Headers.Location;
        var retryAfter = response.Headers.RetryAfter?.Delta
            ?? (response.Headers.RetryAfter?.Date is { } retryDate
                ? retryDate - DateTimeOffset.UtcNow
                : null);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Azure ARM request failed: {method} {uri.GetLeftPart(UriPartial.Path)} returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }

        return new ArmResponse(response.StatusCode, body, location, retryAfter);
    }

    /// <summary>
    /// Polls a validated publication URI with Retry-After delays and a ten-minute deadline.
    /// Accepts documented terminal success responses and fails on operation errors or unknown states.
    /// </summary>
    static async Task WaitForPublishAsync(
        HttpClient client,
        AccessToken token,
        Uri operationUri,
        TimeSpan? initialRetryAfter,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + PublishTimeout;
        var delay = initialRetryAfter ?? TimeSpan.FromSeconds(5);

        while (true)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero || delay >= remaining)
            {
                throw new TimeoutException(
                    "Azure Automation runbook publication exceeded the 10-minute limit; inspect the publish operation before retrying.");
            }

            await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1), cancellationToken);
            var response = await SendArmAsync(
                client,
                token,
                HttpMethod.Get,
                operationUri,
                null,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(response.Body))
            {
                if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent)
                {
                    return;
                }

                delay = response.RetryAfter ?? TimeSpan.FromSeconds(5);
                continue;
            }

            using var document = JsonDocument.Parse(response.Body);
            var payload = document.RootElement;
            if (payload.TryGetProperty("error", out var error))
            {
                throw new InvalidOperationException($"Azure Automation publish operation failed: {error.GetRawText()}");
            }

            var status = payload.TryGetProperty("status", out var statusElement)
                ? statusElement.GetString()
                : null;
            switch (status)
            {
                case "Succeeded" or "Completed":
                    return;
                case "Failed" or "Canceled" or "Cancelled":
                    throw new InvalidOperationException(
                        $"Azure Automation publish operation ended with status {status}: {response.Body}");
            }

            if (status is not ("InProgress" or "Running" or "Accepted" or "Pending"))
            {
                throw new InvalidOperationException(
                    $"Azure Automation publish operation returned an unknown status: {response.Body}");
            }

            delay = response.RetryAfter ?? TimeSpan.FromSeconds(5);
        }
    }

    /// <summary>
    /// Restricts publication polling to HTTPS ARM operation results on the expected runbook.
    /// Requires an operation GUID and the publication API version before forwarding credentials.
    /// </summary>
    static Uri ValidatePublishOperationUri(
        Uri location,
        string subscriptionId,
        string resourceGroupName,
        string automationAccountName)
    {
        var runbookPath =
            $"/subscriptions/{subscriptionId}/resourceGroups/{Uri.UnescapeDataString(resourceGroupName)}/providers/Microsoft.Automation/automationAccounts/{Uri.UnescapeDataString(Uri.EscapeDataString(automationAccountName))}/runbooks/{RunbookName}";
        var path = Uri.UnescapeDataString(location.AbsolutePath);
        // Azure returns the runbook-level path; the REST example also documents a draft-level path.
        var operationPrefixes = new[]
        {
            $"{runbookPath}/operationResults/",
            $"{runbookPath}/draft/publish/operationResults/"
        };
        var validOperationPath = operationPrefixes.Any(prefix =>
            path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParse(path[prefix.Length..], out _));

        if (location.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(location.Host, "management.azure.com", StringComparison.OrdinalIgnoreCase) ||
            !location.IsDefaultPort ||
            !string.IsNullOrEmpty(location.UserInfo) ||
            !string.IsNullOrEmpty(location.Fragment) ||
            !validOperationPath)
        {
            throw new InvalidOperationException(
                $"Azure Automation returned an unexpected publish-operation URI; refusing to send credentials: {location.GetLeftPart(UriPartial.Path)}");
        }

        if (!string.IsNullOrEmpty(location.Query))
            return !MyRegex().IsMatch(location.Query)
                ? throw new InvalidOperationException(
                    "Azure Automation publish-operation URI has an unexpected API version.")
                : location;
        var builder = new UriBuilder(location) { Query = $"api-version={PublishApiVersion}" };
        return builder.Uri;
    }

    /// <summary>Checks the remote script against the rendered source, ignoring only line-ending differences.</summary>
    static void EnsureContentMatches(string actual, string expected, string stage)
    {
        if (!string.Equals(NormalizeLineEndings(actual), NormalizeLineEndings(expected), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Azure Automation {stage} runbook content does not match the checked-in source.");
        }

        return;

        // Normalize Windows and legacy CR line endings before the ordinal content comparison.
        static string NormalizeLineEndings(string value) =>
            value.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
    }

    internal sealed record ArmResponse(
        HttpStatusCode StatusCode,
        string Body,
        Uri? Location,
        TimeSpan? RetryAfter);

    /// <summary>Matches the required publication API version as a complete query parameter.</summary>
    [GeneratedRegex("(?:\\?|&)api-version=2015-10-31(?:&|$)", RegexOptions.IgnoreCase, "en-IT")]
    private static partial Regex MyRegex();
}
