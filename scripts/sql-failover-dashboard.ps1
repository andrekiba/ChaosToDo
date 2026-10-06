#requires -Version 7.0
<#
.SYNOPSIS
    Monitors direct database reads during the SQL local HA failover demo.
.DESCRIPTION
    Calls /api/todos/nocache with fixed concurrency and no application-level retries.
    Prints one row per second: completions, successes, HTTP/network errors, timeouts,
    pending requests and latency of successful responses. Does not start any fault.
    Users wait for each response before making another request (closed-loop load).
.EXAMPLE
    .\scripts\sql-failover-dashboard.ps1
.EXAMPLE
    .\scripts\sql-failover-dashboard.ps1 -Users 10 -DurationSeconds 300
#>
param(
    [string]$BaseUrl = 'https://chaos-todo-dev-api-win.azurewebsites.net',
    [ValidateRange(1, 100)]
    [int]$Users = 10,
    [ValidateRange(0, 10000)]
    [int]$ThinkTimeMs = 200,
    [ValidateRange(1, 120)]
    [int]$TimeoutSeconds = 15,
    [ValidateRange(0, 86400)]
    [int]$DurationSeconds = 0
)

$ErrorActionPreference = 'Stop'
$uri = [Uri]::new([Uri]$BaseUrl.TrimEnd('/'), '/api/todos/nocache')
$handler = [System.Net.Http.SocketsHttpHandler]::new()
$handler.UseCookies = $false
$handler.AllowAutoRedirect = $false
$client = [System.Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)
$clock = [Diagnostics.Stopwatch]::StartNew()

function New-Stats {
    @{
        Completed = 0; Ok = 0; HttpErrors = 0; NetworkErrors = 0; Timeouts = 0
        Latencies = [System.Collections.Generic.List[double]]::new()
        Details = [System.Collections.Generic.HashSet[string]]::new()
    }
}

function Start-Call {
    [pscustomobject]@{
        StartedMs = $clock.Elapsed.TotalMilliseconds
        Task = $client.GetAsync($uri)
        NotBeforeMs = 0
    }
}

function Get-Percentile([double[]]$Sorted, [double]$Percentile) {
    if ($Sorted.Count -eq 0) { return '-' }
    '{0:F0}' -f $Sorted[[math]::Ceiling($Percentile * $Sorted.Count) - 1]
}

function Show-Stats($Stats, [double]$Seconds) {
    $pending = @($slots | Where-Object { $null -ne $_.Task }).Count
    $sorted = [double[]]@($Stats.Latencies | Sort-Object)
    $max = if ($sorted.Count) { '{0:F0}' -f $sorted[-1] } else { '-' }
    $color = if ($Stats.HttpErrors + $Stats.NetworkErrors + $Stats.Timeouts -gt 0) {
        'Red'
    } elseif (($sorted.Count -gt 0 -and $sorted[-1] -ge 1500) -or $Stats.Completed -eq 0) {
        'Yellow'
    } else { 'Green' }
    Write-Host ('{0,-8} {1,5:F1} {2,4} {3,4} {4,4} {5,4} {6,7} {7,7} {8,7} {9,7}  {10}' -f
        [DateTime]::Now.ToString('HH:mm:ss'), ($Stats.Completed / $Seconds),
        $Stats.Ok, $Stats.HttpErrors, $Stats.NetworkErrors, $Stats.Timeouts,
        $pending, (Get-Percentile $sorted 0.50), (Get-Percentile $sorted 0.95),
        $max, ($Stats.Details -join '; ')) -ForegroundColor $color
}

$slots = [System.Collections.Generic.List[object]]::new()
$stats = New-Stats
$totals = New-Stats
$windowStartedMs = 0

Write-Host "SQL HA dashboard  $uri  users=$Users  timeout=${TimeoutSeconds}s  (Ctrl+C to stop)" -ForegroundColor Cyan
Write-Host 'Read-only traffic; start sql-local-ha-failover separately in Chaos Studio.' -ForegroundColor DarkGray
Write-Host 'Latency = successful requests only; includes the simulated 800 ms database delay.' -ForegroundColor DarkGray
Write-Host ('{0,-8} {1,5} {2,4} {3,4} {4,4} {5,4} {6,7} {7,7} {8,7} {9,7}  {10}' -f
    'time', 'req/s', 'ok', 'http', 'net', 'tout', 'pending', 'p50ms', 'p95ms', 'maxms', 'errors') -ForegroundColor DarkGray

try {
    foreach ($i in 1..$Users) { $slots.Add((Start-Call)) }
    while ($DurationSeconds -eq 0 -or $clock.Elapsed.TotalSeconds -lt $DurationSeconds) {
        for ($i = 0; $i -lt $slots.Count; $i++) {
            $call = $slots[$i]
            if ($null -eq $call.Task) {
                if ($clock.Elapsed.TotalMilliseconds -ge $call.NotBeforeMs) { $slots[$i] = Start-Call }
                continue
            }
            if (-not $call.Task.IsCompleted) { continue }

            $stats.Completed++
            $totals.Completed++
            if ($call.Task.IsCanceled) {
                $stats.Timeouts++; $totals.Timeouts++
                $null = $stats.Details.Add("client timeout (${TimeoutSeconds}s)")
            } elseif ($call.Task.IsFaulted) {
                $errorType = $call.Task.Exception.GetBaseException().GetType().Name
                $stats.NetworkErrors++; $totals.NetworkErrors++
                $null = $stats.Details.Add($errorType)
            } else {
                $response = $call.Task.Result
                try {
                    if ($response.IsSuccessStatusCode) {
                        $stats.Ok++; $totals.Ok++
                        $stats.Latencies.Add($clock.Elapsed.TotalMilliseconds - $call.StartedMs)
                    } else {
                        $stats.HttpErrors++; $totals.HttpErrors++
                        $null = $stats.Details.Add("HTTP $([int]$response.StatusCode)")
                    }
                } finally { $response.Dispose() }
            }
            $slots[$i] = [pscustomobject]@{
                Task = $null; StartedMs = 0
                NotBeforeMs = $clock.Elapsed.TotalMilliseconds + $ThinkTimeMs
            }
        }
        $windowMs = $clock.Elapsed.TotalMilliseconds - $windowStartedMs
        if ($windowMs -ge 1000) {
            Show-Stats $stats ($windowMs / 1000)
            $stats = New-Stats
            $windowStartedMs = $clock.Elapsed.TotalMilliseconds
        }
        Start-Sleep -Milliseconds 10
    }
} finally {
    $client.CancelPendingRequests()
    $pending = @($slots | Where-Object { $null -ne $_.Task }).Count
    $client.Dispose()
    $handler.Dispose()
    Write-Host ("Total: {0} completed, {1} OK, {2} HTTP errors, {3} network errors, {4} timeouts; {5} pending at stop. Duration: {6:F1}s" -f
        $totals.Completed, $totals.Ok, $totals.HttpErrors, $totals.NetworkErrors,
        $totals.Timeouts, $pending, $clock.Elapsed.TotalSeconds) -ForegroundColor Cyan
}
