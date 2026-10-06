#requires -Version 7.0
<#
.SYNOPSIS
    Live terminal dashboard for the Compute Zone Down demo.

.DESCRIPTION
    Keeps a fixed number of virtual users calling the API behind the zone-redundant
    Load Balancer and prints one line per second with request rate, errors/timeouts,
    latency and how many responses each availability zone served (X-Served-By).

    Every request uses a new TCP connection: the Load Balancer picks the backend per
    connection (5-tuple hash), so reusing connections would pin users to one VM.

    In the background it also polls the VM scale set power state through the Azure CLI
    (az login required) and prints a line whenever it changes, e.g. when Chaos Studio
    stops the zone 2 instance and when it starts it again.

.EXAMPLE
    ./scripts/zone-down-dashboard.ps1

.EXAMPLE
    ./scripts/zone-down-dashboard.ps1 -Users 5 -PowerStateIntervalSeconds 0
#>
param(
    [string]$BaseUrl = 'http://chaos-todo-dev-api-vm.italynorth.cloudapp.azure.com',
    [string]$Path = '/api/todos',
    [ValidateRange(1, 100)]
    [int]$Users = 10,
    [ValidateRange(0, 10000)]
    [int]$ThinkTimeMs = 200,
    [ValidateRange(1, 60)]
    [int]$TimeoutSeconds = 5,
    [ValidateRange(0, 86400)]
    [int]$DurationSeconds = 0,
    [string]$ResourceGroup = 'chaos-todo-dev-rg',
    [string]$VmssName = 'chaos-todo-dev-api-vmss',
    [ValidateRange(0, 300)]
    [int]$PowerStateIntervalSeconds = 10
)

$ErrorActionPreference = 'Stop'

$uri = [Uri]::new([Uri]$BaseUrl.TrimEnd('/'), $Path)
$handler = [System.Net.Http.SocketsHttpHandler]::new()
$handler.UseCookies = $false
$handler.PooledConnectionLifetime = [TimeSpan]::Zero
$client = [System.Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)

# --- VMSS power state polling (background thread) -----------------------------------
$power = [hashtable]::Synchronized(@{ State = $null; Error = $null; Stop = $false })
$powerJob = $null
if ($PowerStateIntervalSeconds -gt 0) {
    if (-not (Get-Command az -ErrorAction SilentlyContinue) -or -not (az account show --query id -o tsv 2>$null)) {
        Write-Host 'Azure CLI not available or not logged in: VMSS power state polling disabled.' -ForegroundColor DarkYellow
    }
    else {
        $powerJob = Start-ThreadJob -ArgumentList $power, $ResourceGroup, $VmssName, $PowerStateIntervalSeconds -ScriptBlock {
            param($power, $resourceGroup, $vmssName, $interval)
            while (-not $power.Stop) {
                try {
                    $json = az vmss list-instances -g $resourceGroup -n $vmssName --expand instanceView `
                        --query "[].{zone: zones[0], power: instanceView.statuses[?starts_with(code, 'PowerState/')].code | [0]}" -o json 2>$null
                    if ($LASTEXITCODE -ne 0) { throw 'az vmss list-instances failed' }
                    $power.State = (@($json | ConvertFrom-Json | Sort-Object zone | ForEach-Object {
                        'zone-{0}={1}' -f $_.zone, ($_.power -replace '^PowerState/', '')
                    }) -join '  ')
                    $power.Error = $null
                }
                catch {
                    $power.Error = $_.Exception.Message
                }
                for ($i = 0; $i -lt $interval * 10 -and -not $power.Stop; $i++) { Start-Sleep -Milliseconds 100 }
            }
        }
    }
}

function Get-Percentile([double[]]$Sorted, [double]$P) {
    if (-not $Sorted -or $Sorted.Count -eq 0) { return 0 }
    $Sorted[[math]::Max(0, [math]::Min($Sorted.Count - 1, [math]::Ceiling($P * $Sorted.Count) - 1))]
}

function New-SecondStats {
    @{ Requests = 0; Latencies = [System.Collections.Generic.List[double]]::new(); Errors = 0; Timeouts = 0; Zones = @{} }
}

function Start-Call {
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::Get, $uri)
    $request.Headers.ConnectionClose = $true
    [pscustomobject]@{ Task = $client.SendAsync($request); Request = $request; Started = [Diagnostics.Stopwatch]::GetTimestamp(); NotBefore = 0 }
}

$zones = [System.Collections.Generic.SortedSet[string]]::new()
$totals = @{ Requests = 0; Errors = 0; Timeouts = 0; Zones = @{} }
$started = [DateTime]::Now
$thinkTicks = [long]([Diagnostics.Stopwatch]::Frequency * $ThinkTimeMs / 1000)
$lastPower = $null
$lastPowerError = $null

Write-Host ("Zone down dashboard  {0}  users={1}  think={2}ms  timeout={3}s  (Ctrl+C to stop)" -f $uri, $Users, $ThinkTimeMs, $TimeoutSeconds) -ForegroundColor Cyan
Write-Host ('{0,-8} {1,5} {2,4} {3,8} {4,7} {5,7} {6,7}  {7}' -f 'time', 'req', 'err', 'timeouts', 'p50ms', 'p95ms', 'maxms', 'responses per zone') -ForegroundColor DarkGray

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
            if ($call.Task.IsCanceled -or ($call.Task.IsFaulted -and $call.Task.Exception.GetBaseException() -is [TimeoutException])) {
                $second.Timeouts++
                $totals.Timeouts++
            }
            elseif ($call.Task.IsFaulted) {
                $second.Errors++
                $totals.Errors++
            }
            else {
                $response = $call.Task.Result
                try {
                    if ($response.IsSuccessStatusCode) {
                        $second.Latencies.Add($elapsedMs)
                        $values = $null
                        $servedBy = if ($response.Headers.TryGetValues('X-Served-By', [ref]$values)) { $values -join ',' } else { 'unknown' }
                        # "chaostodo000000/zone-2" -> "zone-2"
                        $zone = if ($servedBy -match '/(zone-[^/]+)$') { $Matches[1] } else { $servedBy }
                        $null = $zones.Add($zone)
                        $second.Zones[$zone] = 1 + [int]$second.Zones[$zone]
                        $totals.Zones[$zone] = 1 + [int]$totals.Zones[$zone]
                    }
                    else {
                        $second.Errors++
                        $totals.Errors++
                    }
                }
                finally {
                    $response.Dispose()
                }
            }
            $call.Request.Dispose()
            $slots[$i] = [pscustomobject]@{ Task = $null; Request = $null; Started = 0; NotBefore = $now + $thinkTicks }
        }

        if ($tick.ElapsedMilliseconds -ge 1000) {
            $tick.Restart()
            $currentPower = $power.State
            if ($currentPower -and $currentPower -ne $lastPower) {
                Write-Host ('{0,-8} VMSS  {1}' -f [DateTime]::Now.ToString('HH:mm:ss'), $currentPower) -ForegroundColor Magenta
                $lastPower = $currentPower
            }
            if ($power.Error -and $power.Error -ne $lastPowerError) {
                Write-Host ('{0,-8} VMSS  power state unavailable: {1}' -f [DateTime]::Now.ToString('HH:mm:ss'), $power.Error) -ForegroundColor DarkYellow
            }
            $lastPowerError = $power.Error

            $sorted = [double[]]@($second.Latencies | Sort-Object)
            $p50 = Get-Percentile $sorted 0.50
            $p95 = Get-Percentile $sorted 0.95
            $max = if ($sorted.Count) { $sorted[-1] } else { 0 }
            $missingZone = $zones.Count -gt 1 -and @($zones | Where-Object { -not $second.Zones.ContainsKey($_) }).Count -gt 0
            $color = if ($second.Errors -gt 0 -or $second.Timeouts -gt 0) { 'Red' } elseif ($missingZone -or $p95 -ge 1000) { 'Yellow' } else { 'Green' }

            Write-Host ('{0,-8} {1,5} {2,4} {3,8} {4,7:N0} {5,7:N0} {6,7:N0}  ' -f [DateTime]::Now.ToString('HH:mm:ss'), $second.Requests, $second.Errors, $second.Timeouts, $p50, $p95, $max) -ForegroundColor $color -NoNewline
            foreach ($zone in $zones) {
                $count = [int]$second.Zones[$zone]
                $zoneColor = if ($count -eq 0) { 'DarkGray' } else { 'Gray' }
                Write-Host ('{0}: {1,3}  ' -f $zone, $count) -ForegroundColor $zoneColor -NoNewline
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
    $power.Stop = $true
    if ($powerJob) { $null = $powerJob | Wait-Job -Timeout 5; $powerJob | Remove-Job -Force }
    $zoneSummary = ($totals.Zones.GetEnumerator() | Sort-Object Name | ForEach-Object { '{0}={1}' -f $_.Name, $_.Value }) -join ', '
    Write-Host ''
    Write-Host ('Total: {0} requests, {1} errors, {2} timeouts in {3:mm\:ss}; {4}' -f $totals.Requests, $totals.Errors, $totals.Timeouts, ([DateTime]::Now - $started), $zoneSummary) -ForegroundColor Cyan
    $client.Dispose()
}
