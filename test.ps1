<#
.SYNOPSIS
    Manual testing in one step: builds, starts what's needed, runs a client, and stops everything it started when the
    client exits.

.DESCRIPTION
    Default: starts the gRPC server (which sets up the local database) and opens the gRPC client. With no arguments the
    client opens its interactive prompt (type 'exit' to finish); with arguments they run as one command.

    -Legacy: also starts the WCF gateway (CashMovements.Wcf, .NET Framework) and runs the legacy WCF client instead.
    Arguments are <account> <currency>; the default is ACC-1001 ZAR.

    Anything already listening on its port (gRPC 5145, WCF 8095) is used as-is and left running.
    Server and gateway output go to log files (paths printed at start) so they don't mix with the client's.

.EXAMPLE
    .\test.ps1
.EXAMPLE
    .\test.ps1 demo
.EXAMPLE
    .\test.ps1 balance ACC-1001 ZAR
.EXAMPLE
    .\test.ps1 -Legacy
.EXAMPLE
    .\test.ps1 -Legacy ACC-2002 ZAR
#>
param(
    [switch] $Legacy,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $ClientArgs = @()
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$logDir = [IO.Path]::GetTempPath()
$grpcPort = 5145   # Kestrel:Endpoints:Grpc in CashMovements.Grpc\appsettings.json
$wcfPort = 8095    # base address in CashMovements.Wcf\App.config

function Test-Port([int] $port) {
    $tcp = New-Object Net.Sockets.TcpClient
    try { $tcp.Connect('localhost', $port); return $true } catch { return $false } finally { $tcp.Dispose() }
}

function Invoke-Build([string] $project) {
    Write-Host "Building $project..."
    $output = dotnet build (Join-Path $root $project) --nologo -v q
    if ($LASTEXITCODE -ne 0) {
        $output | Write-Host
        throw "Build of $project failed."
    }
}

# Starts a built program in the background and waits until it listens on $port. Returns the process.
function Start-Background([string] $name, [string] $file, [string[]] $arguments, [string] $workingDirectory, [int] $port) {
    $log = Join-Path $logDir "cashmovements-$($name.ToLowerInvariant().Replace(' ', '-')).log"
    Write-Host "Starting the $name on port $port (log: $log)..."

    $startArgs = @{
        FilePath               = $file
        WorkingDirectory       = $workingDirectory
        RedirectStandardOutput = $log
        RedirectStandardError  = "$log.err"
        NoNewWindow            = $true
        PassThru               = $true
    }
    if ($arguments) { $startArgs.ArgumentList = $arguments }
    $process = Start-Process @startArgs

    $deadline = (Get-Date).AddSeconds(60)
    while (-not (Test-Port $port)) {
        if ($process.HasExited) {
            Write-Host "The $name stopped during startup. Last lines of its output:" -ForegroundColor Red
            Get-Content $log, "$log.err" -Tail 30 -ErrorAction SilentlyContinue
            throw "The $name did not start."
        }
        if ((Get-Date) -gt $deadline) {
            throw "The $name did not start within 60 seconds. See $log"
        }
        Start-Sleep -Milliseconds 250
    }

    Write-Host "$name ready."
    return $process
}

$started = New-Object System.Collections.Generic.List[System.Diagnostics.Process]
$previousEnvironment = $env:ASPNETCORE_ENVIRONMENT
$clientExit = 1

try {
    $grpcRunning = Test-Port $grpcPort
    $wcfRunning = $Legacy -and (Test-Port $wcfPort)

    # Build only what this run will start or use; a running server's files are locked.
    if (-not $grpcRunning) { Invoke-Build 'CashMovements.Grpc' }
    if ($Legacy) {
        if (-not $wcfRunning) { Invoke-Build 'CashMovements.Wcf' }
        Invoke-Build 'CashMovements.Wcf.Client'
    }
    else {
        Invoke-Build 'CashMovements.Client'
    }

    if ($grpcRunning) {
        Write-Host "A gRPC server is already listening on port $grpcPort; using it (it will be left running)."
    }
    else {
        # Run the built server directly (not 'dotnet run') so stopping this process really stops the server.
        # Its ports come from appsettings.json; Development enables the local database setup.
        $env:ASPNETCORE_ENVIRONMENT = 'Development'
        $started.Add((Start-Background 'gRPC server' 'dotnet' @('CashMovements.Grpc.dll') `
            (Join-Path $root 'CashMovements.Grpc\bin\Debug\net8.0') $grpcPort))
    }

    if ($Legacy) {
        if ($wcfRunning) {
            Write-Host "A WCF gateway is already listening on port $wcfPort; using it (it will be left running)."
        }
        else {
            $wcfDir = Join-Path $root 'CashMovements.Wcf\bin\Debug\net48'
            $started.Add((Start-Background 'WCF gateway' (Join-Path $wcfDir 'CashMovements.Wcf.exe') @() $wcfDir $wcfPort))
        }

        if ($ClientArgs.Count -eq 0) { $ClientArgs = @('ACC-1001', 'ZAR') }
        Write-Host ''
        & (Join-Path $root 'CashMovements.Wcf.Client\bin\Debug\net48\CashMovements.Wcf.Client.exe') @ClientArgs
        $clientExit = $LASTEXITCODE
    }
    else {
        Write-Host ''
        & dotnet (Join-Path $root 'CashMovements.Client\bin\Debug\net8.0\CashMovements.Client.dll') @ClientArgs --server "http://localhost:$grpcPort"
        $clientExit = $LASTEXITCODE
    }
}
finally {
    $env:ASPNETCORE_ENVIRONMENT = $previousEnvironment
    for ($i = $started.Count - 1; $i -ge 0; $i--) {
        if (-not $started[$i].HasExited) {
            Stop-Process -Id $started[$i].Id -Force
        }
    }
    if ($started.Count -gt 0) { Write-Host 'Stopped what this script started.' }
}

exit $clientExit
