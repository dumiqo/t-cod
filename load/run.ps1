param(
    [Parameter(Mandatory)][ValidateSet('calibrate', 'running', 'down', 'restart', 'paused')][string]$Mode,
    [int]$Rate = 100,
    [string]$Duration = '5m',
    [string]$Steps = '100,200,300,400,500,600,800'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$compose = Join-Path $root 'docker-compose.yml'
$out = Join-Path $PSScriptRoot "out\$Mode"
$shipped = (Get-Content "$root\WebApi\appsettings.json" -Raw | ConvertFrom-Json).ConnectionStrings.Postgres
$connection = $shipped -replace 'Database=club;', 'Database=club_load;'
if ($connection -eq $shipped) { throw 'Database=club; not found in the shipped connection string' }
$baseUrl = 'http://localhost:5080'
$k6Image = 'grafana/k6:0.54.0'

function Get-Now { [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() }
function Write-Event($name) { Add-Content -Path "$out\events.csv" -Value "$name,$(Get-Now)" }
function Invoke-Compose { & docker compose -f $compose @args; if ($LASTEXITCODE -ne 0) { throw "docker compose $args failed" } }

if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force $out | Out-Null

& dotnet build -c Release "$root\WebApi" | Out-Null
& dotnet build -c Release "$root\WebApi.LoadTests" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

cmd /c "docker compose -f `"$compose`" unpause redis 2>nul" | Out-Null
Invoke-Compose up -d --wait
Invoke-Compose exec -T postgres psql -U club -d club -c 'DROP DATABASE IF EXISTS club_load WITH (FORCE)'
Invoke-Compose exec -T redis redis-cli FLUSHALL
if ($Mode -eq 'down') { Invoke-Compose stop redis }

$env:ConnectionStrings__Postgres = $connection
$appDir = "$root\WebApi\bin\Release\net9.0"
$app = Start-Process dotnet -ArgumentList "WebApi.dll --urls $baseUrl" -WorkingDirectory $appDir -PassThru -NoNewWindow `
    -RedirectStandardOutput "$out\app.log" -RedirectStandardError "$out\app.err"
$health = $null
try
{
    $deadline = (Get-Date).AddSeconds(120)
    while ((& curl.exe -s -m 2 "$baseUrl/health") -ne 'Healthy')
    {
        if ((Get-Date) -gt $deadline -or $app.HasExited) { throw 'app did not start' }
        Start-Sleep -Milliseconds 500
    }

    $loadTests = "$root\WebApi.LoadTests\bin\Release\net9.0\WebApi.LoadTests.dll"
    & dotnet $loadTests seed $connection $out
    if ($LASTEXITCODE -ne 0) { throw 'seed failed' }
    & dotnet $loadTests login $baseUrl $out
    if ($LASTEXITCODE -ne 0) { throw 'login failed' }

    $health = Start-Job -ArgumentList "$out\health.csv", "$baseUrl/health/ready" -ScriptBlock {
        param($file, $url)
        while ($true)
        {
            $body = & curl.exe -s -m 1 $url
            if (-not $body) { $body = 'NoResponse' }
            Add-Content -Path $file -Value "$([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()),$body"
            Start-Sleep -Seconds 1
        }
    }

    Write-Event 'k6'
    $k6 = Start-Process docker -PassThru -NoNewWindow -RedirectStandardOutput "$out\k6.txt" -RedirectStandardError "$out\k6.err" -ArgumentList @(
        'run', '--rm', '-v', "`"${PSScriptRoot}:/load`"", '-e', "MODE=$Mode", '-e', "RATE=$Rate", '-e', "DURATION=$Duration", '-e', "STEPS=$Steps",
        $k6Image, 'run', '--log-format=raw', "--log-output=file=/load/out/$Mode/requests.csv", '/load/scenario.js')

    if ($Mode -in 'restart', 'paused')
    {
        Start-Sleep -Seconds 90
        if ($Mode -eq 'restart') { Invoke-Compose stop redis } else { Invoke-Compose pause redis }
        Write-Event 'stop'
        Start-Sleep -Seconds 90
        if ($Mode -eq 'restart') { Invoke-Compose start redis } else { Invoke-Compose unpause redis }
        Write-Event 'start'
    }

    $k6.WaitForExit()
    Start-Sleep -Seconds 2
}
finally
{
    if ($health) { Stop-Job $health; Remove-Job $health }
    if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force }
    Remove-Item Env:ConnectionStrings__Postgres
    if ($Mode -eq 'down') { Invoke-Compose start redis }
}

& dotnet $loadTests report $connection $out $Mode
exit $LASTEXITCODE
