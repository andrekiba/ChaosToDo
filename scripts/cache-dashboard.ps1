#requires -Version 7.0
<#
.SYNOPSIS
    Live terminal dashboard for the cache stampede demo.

.DESCRIPTION
    Keeps a fixed number of virtual users calling one todo-list endpoint and prints one
    line per second with request rate, latency, cache HIT/MISS (naive endpoint) and,
    for each API process, the database queries it ran in that second.

    Per-process data comes from the demo headers emitted when Demo:ServedByHeader is
    true: X-Served-By (instance), X-Process-Id and X-Db-Queries (per-process counters).
    Cookies are disabled so App Service ARR affinity does not pin every request to the
    same worker.

    Run two instances side by side (-Endpoint naive and -Endpoint fusion) to compare
    both strategies during the same Chaos Studio flush.

.EXAMPLE
    ./scripts/cache-dashboard.ps1 -Endpoint naive

.EXAMPLE
    ./scripts/cache-dashboard.ps1 -BaseUrl http://localhost:5554 -Endpoint fusion -Users 10
#>
param(
    [string]$BaseUrl = 'https://chaos-todo-dev-api-win.azurewebsites.net',
    [ValidateSet('naive', 'fusion', 'nocache')]
    [string]$Endpoint = 'naive',
    [ValidateRange(1, 200)]
    [int]$Users = 20,
    [ValidateRange(0, 10000)]
    [int]$ThinkTimeMs = 200,
    [ValidateRange(1, 120)]
    [int]$TimeoutSeconds = 10,
    [ValidateRange(0, 86400)]
    [int]$DurationSeconds = 0
)

$ErrorActionPreference = 'Stop'

$path = @{ naive = '/api/todos/naive'; fusion = '/api/todos'; nocache = '/api/todos/nocache' }[$Endpoint]
$uri = [Uri]::new([Uri]$BaseUrl.TrimEnd('/'), $path)

$handler = [System.Net.Http.SocketsHttpHandler]::new()
$handler.UseCookies = $false
$handler.PooledConnectionIdleTimeout = [TimeSpan]::FromMinutes(5)
$client = [System.Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)

function Get-Header($Response, [string]$Name) {
    $values = $null
    if ($Response.Headers.TryGetValues($Name, [ref]$values)) { return ($values -join ',') }
    $null
}

function Get-QueryCount([string]$Header, [string]$Name) {
    if ($Header -match "(?:^|;)$Name=(\d+)") { return [int64]$Matches[1] }
    [int64]0
}

function Get-Percentile([double[]]$Sorted, [double]$P) {
    if (-not $Sorted -or $Sorted.Count -eq 0) { return 0 }
    $Sorted[[math]::Max(0, [math]::Min($Sorted.Count - 1, [math]::Ceiling($P * $Sorted.Count) - 1))]
}

function New-SecondStats {
    @{ Requests = 0; Latencies = [System.Collections.Generic.List[double]]::new(); Errors = 0; Hit = 0; Miss = 0; Workers = @{} }
}

function Start-Call {
    [pscustomobject]@{ Task = $client.GetAsync($uri); Started = [Diagnostics.Stopwatch]::GetTimestamp(); NotBefore = 0 }
}

# Last X-Db-Queries value seen per process ("servedBy|pid") and stable worker aliases.
$processes = @{}
$workers = [ordered]@{}
$newWorkers = [System.Collections.Generic.List[string]]::new()
$totals = @{ Requests = 0; Errors = 0; Queries = 0 }
$started = [DateTime]::Now
$thinkTicks = [long]([Diagnostics.Stopwatch]::Frequency * $ThinkTimeMs / 1000)

Write-Host ("Cache dashboard  {0}  users={1}  think={2}ms  (Ctrl+C to stop)" -f $uri, $Users, $ThinkTimeMs) -ForegroundColor Cyan
Write-Host ('{0,-8} {1,5} {2,4} {3,9} {4,7} {5,7} {6,7}  {7}' -f 'time', 'req', 'err', 'hit/miss', 'p50ms', 'p95ms', 'maxms', 'per worker: requests / db queries') -ForegroundColor DarkGray

$slots = [System.Collections.Generic.List[object]]::new()
foreach ($i in 1..$Users) { $slots.Add((Start-Call)) }

$second = New-SecondStats
$tick = [Diagnostics.Stopwatch]::StartNew()

try {
    while ($DurationSeconds -eq 0 -or ([DateTime]::Now - $started).TotalSeconds -lt $DurationSeconds) {
        $now = [Diagnostics.Stopwatch]::GetTimestamp()
        for ($i = 0; $i -lt $slots.Count; $i++) {
            $call = $slots[$i]
            if ($null -eq $call.Task) {
                if ($now -ge $call.NotBefore) { $slots[$i] = Start-Call }
                continue
            }
            if (-not $call.Task.IsCompleted) { continue }

            $elapsedMs = [Diagnostics.Stopwatch]::GetElapsedTime($call.Started).TotalMilliseconds
            $second.Requests++
            $totals.Requests++
            if ($call.Task.IsFaulted -or $call.Task.IsCanceled) {
                $second.Errors++
                $totals.Errors++
            }
            else {
                $response = $call.Task.Result
                try {
                    if (-not $response.IsSuccessStatusCode) { $second.Errors++; $totals.Errors++ }
                    $second.Latencies.Add($elapsedMs)
                    switch (Get-Header $response 'X-Cache') { 'HIT' { $second.Hit++ } 'MISS' { $second.Miss++ } }

                    $servedBy = Get-Header $response 'X-Served-By'
                    $processId = Get-Header $response 'X-Process-Id'
                    if ($servedBy -and $processId) {
                        if (-not $workers.Contains($servedBy)) {
                            # Same label in every dashboard: the tail of the machine name.
                            $machine = $servedBy.Split('/')[0]
                            $workers[$servedBy] = $machine.Substring([math]::Max(0, $machine.Length - 4))
                            $newWorkers.Add(('  {0} = {1}' -f $workers[$servedBy], $servedBy))
                        }
                        $alias = $workers[$servedBy]
                        if (-not $second.Workers.ContainsKey($alias)) {
                            $second.Workers[$alias] = @{ Requests = 0; Queries = 0; Pid = $processId; Restarted = $false }
                        }
                        $worker = $second.Workers[$alias]
                        $worker.Requests++
                        $worker.Pid = $processId

                        $key = "$servedBy|$processId"
                        $count = Get-QueryCount (Get-Header $response 'X-Db-Queries') $Endpoint
                        if (-not $processes.ContainsKey($key)) {
                            # First process seen on a worker: its current counter is the baseline.
                            # A new PID on a known worker is a restart: its counters started at zero.
                            $restart = @($processes.Keys | Where-Object { $_.StartsWith("$servedBy|") }).Count -gt 0
                            $processes[$key] = if ($restart) { [int64]0 } else { $count }
                            $worker.Restarted = $worker.Restarted -or $restart
                        }
                        if ($count -gt $processes[$key]) {
                            $delta = $count - $processes[$key]
                            $worker.Queries += $delta
                            $totals.Queries += $delta
                            $processes[$key] = $count
                        }
                    }
                }
                finally {
                    $response.Dispose()
                }
            }
            $slots[$i] = [pscustomobject]@{ Task = $null; Started = 0; NotBefore = $now + $thinkTicks }
        }

        if ($tick.ElapsedMilliseconds -ge 1000) {
            $tick.Restart()
            foreach ($line in $newWorkers) { Write-Host $line -ForegroundColor DarkGray }
            $newWorkers.Clear()
            $sorted = [double[]]@($second.Latencies | Sort-Object)
            $p50 = Get-Percentile $sorted 0.50
            $p95 = Get-Percentile $sorted 0.95
            $max = if ($sorted.Count) { $sorted[-1] } else { 0 }
            $cache = if ($Endpoint -eq 'naive') { '{0}/{1}' -f $second.Hit, $second.Miss } else { '-' }
            $queries = ($second.Workers.Values | Measure-Object -Property Queries -Sum).Sum
            $color = if ($second.Errors -gt 0) { 'Red' } elseif ($queries -gt 0 -or $p95 -ge 1000) { 'Yellow' } else { 'Green' }

            Write-Host ('{0,-8} {1,5} {2,4} {3,9} {4,7:N0} {5,7:N0} {6,7:N0}  ' -f [DateTime]::Now.ToString('HH:mm:ss'), $second.Requests, $second.Errors, $cache, $p50, $p95, $max) -ForegroundColor $color -NoNewline
            foreach ($alias in $workers.Values) {
                $worker = $second.Workers[$alias]
                if ($null -eq $worker) {
                    Write-Host ('{0}: {1,3} / {2,-3}  ' -f $alias, 0, '-') -ForegroundColor DarkGray -NoNewline
                    continue
                }
                $workerColor = if ($worker.Restarted) { 'Magenta' } elseif ($worker.Queries -gt 0) { 'Yellow' } else { 'Gray' }
                $restartLabel = if ($worker.Restarted) { " RESTART pid $($worker.Pid)" } else { '' }
                Write-Host ('{0}: {1,3} / {2,-3}{3}  ' -f $alias, $worker.Requests, $worker.Queries, $restartLabel) -ForegroundColor $workerColor -NoNewline
            }
            Write-Host ''
            $second = New-SecondStats
        }

        $pending = @($slots | Where-Object { $null -ne $_.Task -and -not $_.Task.IsCompleted } | ForEach-Object Task)
        if ($pending.Count -gt 0) {
            $null = [System.Threading.Tasks.Task]::WaitAny([System.Threading.Tasks.Task[]]$pending, 20)
        }
        else {
            Start-Sleep -Milliseconds 10
        }
    }
}
finally {
    Write-Host ''
    Write-Host ('Total: {0} requests, {1} errors, {2} db queries in {3:mm\:ss}' -f $totals.Requests, $totals.Errors, $totals.Queries, ([DateTime]::Now - $started)) -ForegroundColor Cyan
    $client.Dispose()
}
