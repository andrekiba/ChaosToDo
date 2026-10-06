# Target and identity are rendered from ARM deployment outputs, not caller parameters.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$databaseId = '__SQL_DATABASE_RESOURCE_ID__'
$identityClientId = '__FAILOVER_IDENTITY_CLIENT_ID__'
$apiVersion = '2023-08-01'
$deadline = [DateTimeOffset]::UtcNow.AddMinutes(10)

if ($databaseId -notmatch '^/subscriptions/[0-9a-f-]+/resourceGroups/[^/]+/providers/Microsoft.Sql/servers/[^/]+/databases/[^/]+$' -or
    $identityClientId -notmatch '^[0-9a-f-]{36}$') {
    throw 'Unrendered or invalid target/identity. Refusing failover.'
}
if (-not $env:IDENTITY_ENDPOINT -or -not $env:IDENTITY_HEADER) {
    throw 'This runbook requires the Azure Automation cloud sandbox managed identity endpoint.'
}

$handler = [System.Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect = $false
$client = [System.Net.Http.HttpClient]::new($handler)
$client.Timeout = [System.Threading.Timeout]::InfiniteTimeSpan

function Send-Request([string] $method, [uri] $uri, [hashtable] $headers) {
    $remaining = ($deadline - [DateTimeOffset]::UtcNow).TotalSeconds
    if ($remaining -le 0) { throw 'Runbook timed out; operation may still be running. Do not repeat the POST.' }
    $cancel = [System.Threading.CancellationTokenSource]::new(
        [TimeSpan]::FromSeconds([Math]::Min(60, $remaining)))
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($method), $uri)
    foreach ($key in $headers.Keys) { [void] $request.Headers.TryAddWithoutValidation($key, $headers[$key]) }
    try {
        # HttpClient has no automatic HTTP retries. An ambiguous POST failure is fatal.
        $response = $client.SendAsync($request, $cancel.Token).GetAwaiter().GetResult()
        try {
            $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            $result = @{
                Status = [int] $response.StatusCode
                Body = $body
                Headers = @{}
            }
            foreach ($header in $response.Headers) { $result.Headers[$header.Key] = ($header.Value -join ',') }
            if (-not $response.IsSuccessStatusCode) {
                throw "ARM/identity request failed: $method $uri HTTP $($result.Status): $body. No failover POST retry."
            }
            return $result
        } finally { $response.Dispose() }
    } finally {
        $request.Dispose()
        $cancel.Dispose()
    }
}

function Get-PollUri([string] $value) {
    $uri = [uri]::new([uri]'https://management.azure.com/', $value)
    $rgPrefix = $databaseId.Substring(0, $databaseId.IndexOf('/providers/', [StringComparison]::OrdinalIgnoreCase))
    $regionalPattern = '^' + [regex]::Escape($rgPrefix) + '/providers/Microsoft.Sql/locations/[^/]+/(databaseOperationResults|databaseAzureAsyncOperation)/[^/]+$'
    $databasePattern = '^' + [regex]::Escape($databaseId) + '/operationResults/[^/]+$'
    if ($uri.Scheme -ne 'https' -or $uri.Host -ne 'management.azure.com' -or
        -not $uri.IsDefaultPort -or $uri.UserInfo -or $uri.Fragment -or
        ($uri.AbsolutePath -notmatch $regionalPattern -and $uri.AbsolutePath -notmatch $databasePattern)) {
        throw "Unexpected LRO URI; refusing to send credentials: $($uri.GetLeftPart([UriPartial]::Path))"
    }
    if (-not $uri.Query) {
        $uri = [uri]::new($uri.AbsoluteUri + "?api-version=$apiVersion")
    }
    return $uri
}

try {
    $tokenUri = $env:IDENTITY_ENDPOINT + '?resource=https%3A%2F%2Fmanagement.azure.com%2F&client_id=' + $identityClientId
    $tokenResponse = Send-Request 'GET' $tokenUri @{
        Metadata = 'True'
        'X-IDENTITY-HEADER' = $env:IDENTITY_HEADER
    }
    $token = $tokenResponse.Body | ConvertFrom-Json
    if (-not $token.access_token) { throw 'Managed identity endpoint did not return an access token.' }
    $auth = @{ Authorization = 'Bearer ' + $token.access_token }
    $databaseUri = "https://management.azure.com${databaseId}?api-version=$apiVersion"
    $database = (Send-Request 'GET' $databaseUri $auth).Body | ConvertFrom-Json
    if ($database.sku.tier -ne 'BusinessCritical' -or $database.properties.status -ne 'Online') {
        throw 'Target must be an Online Business Critical database. No failover submitted.'
    }
    Write-Output "Requesting one primary local HA failover on $databaseId (not zone loss or geo-failover)."
    $response = Send-Request 'POST' "https://management.azure.com${databaseId}/failover?replicaType=Primary&api-version=$apiVersion" $auth
    Write-Output "SQL failover response: HTTP $($response.Status)"
    foreach ($headerName in @('Location', 'Azure-AsyncOperation')) {
        if ($response.Headers.ContainsKey($headerName)) {
            $diagnosticUri = [uri]::new([uri]'https://management.azure.com/', $response.Headers[$headerName])
            $queryKeys = ($diagnosticUri.Query.TrimStart('?') -split '&' |
                Where-Object { $_ } | ForEach-Object { ($_ -split '=', 2)[0] }) -join ', '
            Write-Output "SQL failover $headerName header path: $($diagnosticUri.GetLeftPart([UriPartial]::Path)); query keys: $queryKeys"
        }
    }
    $asyncStatus = $response.Headers.ContainsKey('Azure-AsyncOperation')
    if ($response.Status -eq 202) {
        $header = if ($asyncStatus) { 'Azure-AsyncOperation' } else { 'Location' }
        if (-not $response.Headers.ContainsKey($header)) { throw '202 without an LRO header. Outcome unknown; do not repeat POST.' }
        $pollUri = Get-PollUri $response.Headers[$header]
        Write-Output "Polling accepted operation: $($pollUri.GetLeftPart([UriPartial]::Path))"
        while ($true) {
            $delay = 5
            if ($response.Headers.ContainsKey('Retry-After')) {
                $parsed = 0
                if (-not [int]::TryParse($response.Headers['Retry-After'], [ref] $parsed) -or $parsed -lt 0) {
                    throw 'Invalid Retry-After header.'
                }
                $delay = [Math]::Max(1, $parsed)
            }
            if ([DateTimeOffset]::UtcNow.AddSeconds($delay) -ge $deadline) {
                throw "LRO timeout at $($pollUri.GetLeftPart([UriPartial]::Path)). The operation may continue; do not repeat POST."
            }
            Start-Sleep -Seconds $delay
            $response = Send-Request 'GET' $pollUri $auth
            $payload = if ($response.Body) { $response.Body | ConvertFrom-Json } else { $null }
            if ($payload -and $payload.PSObject.Properties['error']) { throw "SQL operation error: $($response.Body)" }
            $status = if ($payload -and $payload.PSObject.Properties['status']) { [string] $payload.status } else { '' }
            if ($status -in @('Failed', 'Canceled', 'Cancelled')) { throw "SQL operation $status : $($response.Body)" }
            if ($status -eq 'Succeeded') { break }
            if ($status -and $status -notin @('InProgress', 'Running', 'Accepted', 'Pending')) {
                throw "Unexpected SQL operation status: $($response.Body)"
            }
            if (-not $asyncStatus -and -not $status -and $response.Status -in @(200, 204)) { break }
            if ($asyncStatus -and -not $status) { throw 'Async status response has no status; cannot prove completion.' }
            if (-not $asyncStatus -and -not $status -and $response.Status -ne 202) { throw 'Unexpected LRO result.' }
        }
    } elseif ($response.Status -ne 200) {
        throw "Unexpected failover response HTTP $($response.Status). Do not repeat POST."
    }
    $database = (Send-Request 'GET' $databaseUri $auth).Body | ConvertFrom-Json
    if ($database.properties.status -ne 'Online') { throw 'ARM operation completed but the database is not Online.' }
    Write-Output "Primary local HA failover completed; database Online. Wait at least 15 minutes before another request."
} finally {
    $client.Dispose()
    $handler.Dispose()
}
