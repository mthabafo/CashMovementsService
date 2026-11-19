<#
.SYNOPSIS
    Manual testing in one step: builds, starts the gRPC server (which sets up the local database), opens the client,
    and stops the server when the client exits.

.DESCRIPTION
    With no arguments the client opens its interactive prompt; type 'exit' to finish.
    With arguments they are passed to the client as one command, which runs and exits.
    If a server is already listening on the port it is used as-is and left running.
    Server output goes to a log file (path printed at start) so it doesn't mix with the client's.

.EXAMPLE
    .\test.ps1
.EXAMPLE
    .\test.ps1 demo
.EXAMPLE
    .\test.ps1 balance ACC-1001 ZAR
#>
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $ClientArgs = @()
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$port = 5145
$serverUrl = "http://localhost:$port"
$serverDir = Join-Path $root 'CashMovements.Grpc\bin\Debug\net8.0'
$clientDll = Join-Path $root 'CashMovements.Client\bin\Debug\net8.0\CashMovements.Client.dll'
$serverLog = Join-Path ([IO.Path]::GetTempPath()) 'cashmovements-server.log'

function Test-Port {
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

$server = $null
$previousEnvironment = $env:ASPNETCORE_ENVIRONMENT

try {
    if (Test-Port) {
        Write-Host "A server is already listening on $serverUrl; using it (it will be left running)."
        Invoke-Build 'CashMovements.Client'
    }
    else {
        Invoke-Build 'CashMovements.Grpc'
        Invoke-Build 'CashMovements.Client'

        # Run the built server directly (not 'dotnet run') so stopping this process really stops the server.
        $env:ASPNETCORE_ENVIRONMENT = 'Development'
        Write-Host "Starting the server on $serverUrl (log: $serverLog)..."
        $server = Start-Process dotnet `
            -ArgumentList @('CashMovements.Grpc.dll', '--urls', $serverUrl) `
            -WorkingDirectory $serverDir `
            -RedirectStandardOutput $serverLog `
            -RedirectStandardError "$serverLog.err" `
            -NoNewWindow -PassThru

        # The port opens once startup (including the database setup) has finished.
        $deadline = (Get-Date).AddSeconds(60)
        while (-not (Test-Port)) {
            if ($server.HasExited) {
                Write-Host "The server stopped during startup. Last lines of its output:" -ForegroundColor Red
                Get-Content $serverLog, "$serverLog.err" -Tail 30 -ErrorAction SilentlyContinue
                exit 1
            }
            if ((Get-Date) -gt $deadline) {
                Write-Host "The server did not start within 60 seconds. See $serverLog" -ForegroundColor Red
                exit 1
            }
            Start-Sleep -Milliseconds 250
        }
        Write-Host 'Server ready.'
    }

    Write-Host ''
    & dotnet $clientDll @ClientArgs --server $serverUrl
    $clientExit = $LASTEXITCODE
}
finally {
    $env:ASPNETCORE_ENVIRONMENT = $previousEnvironment
    if ($server -and -not $server.HasExited) {
        Write-Host 'Stopping the server...'
        Stop-Process -Id $server.Id -Force
    }
}

exit $clientExit
