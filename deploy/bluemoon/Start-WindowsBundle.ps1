param(
    [ValidateRange(1, 65535)] [int] $GamePort = 1337,
    [ValidateRange(1, 65535)] [int] $HostPort = 5087,
    [ValidateRange(1, 65535)] [int] $DatabasePort = 3307,
    [ValidateRange(30, 900)] [int] $StartupTimeoutSeconds = 480,
    [ValidatePattern('^[A-Za-z0-9_-]{1,64}$')] [string] $ServerId = 'bluemoon'
)
$ErrorActionPreference = 'Stop'
if (@($GamePort, $HostPort, $DatabasePort | Select-Object -Unique).Count -ne 3) { throw 'Ports must be distinct.' }
$game = Join-Path $PSScriptRoot 'game'
$state = Join-Path $PSScriptRoot 'state'
$dbBin = Join-Path $PSScriptRoot 'runtime/mariadb/mariadb-11.4.13-winx64/bin'
$dbData = Join-Path $state 'database'
$dbConfig = Join-Path $dbData 'my.ini'
$rootOptions = Join-Path $state 'db-root.ini'
$appOptions = Join-Path $state 'db-app.ini'
$bootstrap = Join-Path $state 'db-bootstrap.sql'
$dbReady = Join-Path $state 'db-schema-4.7.ready'
$runStatus = Join-Path $state 'run.json'
$stopRequest = Join-Path $state 'stop.request'
$gameStopPath = $null
$null = [IO.Directory]::CreateDirectory($state)
$lease = [IO.File]::Open((Join-Path $state 'instance.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
$netdm = $daemon = $database = $null
$requestedStop = $false

function New-Password {
    $bytes = New-Object byte[] 32
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($bytes) } finally { $generator.Dispose() }
    return [BitConverter]::ToString($bytes).Replace('-', '')
}

function Start-OwnedProcess([string] $Executable, [string[]] $Arguments, [string] $WorkingDirectory, [string] $Name) {
    $quoted = ($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $options = @{
        FilePath = $Executable; WorkingDirectory = $WorkingDirectory; WindowStyle = 'Hidden'; PassThru = $true
        RedirectStandardOutput = (Join-Path $state "$Name.out.log")
        RedirectStandardError = (Join-Path $state "$Name.err.log")
    }
    if ($quoted) { $options.ArgumentList = $quoted }
    $child = Start-Process @options
    # Windows PowerShell needs a retained handle for ExitCode after a fast exit.
    $null = $child.Handle
    return $child
}

function Stop-OwnedProcess($Process) {
    if ($null -ne $Process) {
        if (!$Process.HasExited) { $Process.Kill(); $Process.WaitForExit() }
        $Process.Dispose()
    }
}

function Invoke-Database([string] $OptionFile, [string] $Statement, [string] $SqlFile, [switch] $UseDatabase) {
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = Join-Path $dbBin 'mariadb.exe'
    $info.Arguments = '--defaults-extra-file="' + $OptionFile + '" --batch --skip-column-names --connect-timeout=2'
    if ($UseDatabase) { $info.Arguments += ' --database=bluemoon' }
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $info.RedirectStandardOutput = $info.RedirectStandardError = $true
    $client = [Diagnostics.Process]::Start($info)
    try {
        $stdout = $client.StandardOutput.ReadToEndAsync()
        $stderr = $client.StandardError.ReadToEndAsync()
        if ($SqlFile) {
            $inputFile = [IO.File]::OpenRead($SqlFile)
            try { $inputFile.CopyTo($client.StandardInput.BaseStream) } finally { $inputFile.Dispose() }
        }
        else { $client.StandardInput.WriteLine($Statement) }
        $client.StandardInput.Close()
        if (!$client.WaitForExit(20000)) { $client.Kill(); throw 'MariaDB client timed out.' }
        if ($client.ExitCode -ne 0) { throw "MariaDB client exit $($client.ExitCode)." }
        return $stdout.GetAwaiter().GetResult().Trim()
    }
    finally { $client.Dispose() }
}

function Wait-Database {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline -and !$database.HasExited) {
        try { if ((Invoke-Database $rootOptions 'SELECT 1') -eq '1') { return } } catch { }
        Start-Sleep -Milliseconds 200
    }
    throw 'MariaDB did not start. See state/database logs.'
}

function Start-Host {
    $child = Start-OwnedProcess (Join-Path $PSScriptRoot 'host/NetDM.Host.exe') @() (Join-Path $PSScriptRoot 'host') 'host'
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $deadline -and !$child.HasExited) {
        try {
            $identity = Invoke-RestMethod "$base/api/health" -TimeoutSec 1
            if ($identity.processId -eq $child.Id -and (Invoke-RestMethod "$base/api/readiness/ipc" -TimeoutSec 1).ready) {
                return $child
            }
        } catch { }
        Start-Sleep -Milliseconds 200
    }
    Stop-OwnedProcess $child
    throw 'NetDM Host IPC did not start. See state/host logs.'
}

try {
    [IO.File]::Delete($stopRequest)
    [IO.File]::Delete($runStatus)
    $fresh = !(Test-Path -LiteralPath $dbData)
    if (!$fresh -and !(Test-Path -LiteralPath $dbReady)) {
        throw 'The database has an incomplete bootstrap. Inspect the existing data before restarting.'
    }
    if ($fresh) {
        $rootPassword = New-Password
        $appPassword = New-Password
        $init = Start-OwnedProcess (Join-Path $dbBin 'mariadb-install-db.exe') @("--datadir=$dbData", "--port=$DatabasePort", '--silent') $state 'database-init'
        try {
            if (!$init.WaitForExit(60000) -or $init.ExitCode -ne 0) { throw 'Could not initialize portable MariaDB.' }
        }
        finally { Stop-OwnedProcess $init }
        @(
            "ALTER USER 'root'@'localhost' IDENTIFIED BY '$rootPassword';",
            'CREATE DATABASE bluemoon CHARACTER SET utf8mb4 COLLATE utf8mb4_general_ci;',
            "CREATE USER 'bluemoon'@'127.0.0.1' IDENTIFIED BY '$appPassword';",
            "GRANT ALL PRIVILEGES ON bluemoon.* TO 'bluemoon'@'127.0.0.1';"
        ) | Set-Content -LiteralPath $bootstrap -Encoding Ascii
        foreach ($credentials in @(@('root', $rootPassword, $rootOptions), @('bluemoon', $appPassword, $appOptions))) {
            "[client]`nhost=127.0.0.1`nprotocol=tcp`nuser=$($credentials[0])`npassword=$($credentials[1])" |
                Set-Content -LiteralPath $credentials[2] -Encoding Ascii
        }
    }
    foreach ($options in @($rootOptions, $appOptions)) {
        $clientOptions = [IO.File]::ReadAllText($options)
        $clientOptions = [regex]::Replace($clientOptions, '(?m)^port=.*\r?\n?', '')
        [IO.File]::WriteAllText($options, $clientOptions.TrimEnd() + "`nport=$DatabasePort`n")
    }
    $dbArguments = @("--defaults-file=$dbConfig", '--bind-address=127.0.0.1', "--port=$DatabasePort", '--console')
    if ($fresh) { $dbArguments += "--init-file=$bootstrap" }
    $database = Start-OwnedProcess (Join-Path $dbBin 'mariadbd.exe') $dbArguments $state 'database'
    Wait-Database
    if ($fresh) {
        [IO.File]::Delete($bootstrap)
        foreach ($name in @('tgstation_schema.sql', 'bluemoon_schema.sql', 'bluemoon_whitelist_schema.sql', 'mentor.sql', 'schema-revision.sql')) {
            $null = Invoke-Database $appOptions -SqlFile (Join-Path $PSScriptRoot "runtime/sql/$name") -UseDatabase
        }
    }
    $revision = Invoke-Database $appOptions 'SELECT CONCAT(major, ".", minor) FROM schema_revision ORDER BY date DESC LIMIT 1' -UseDatabase
    if ($revision -ne '4.7') { throw "Expected database schema 4.7, got $revision." }
    [IO.File]::WriteAllText($dbReady, $revision)
    $passwordMatch = [regex]::Match([IO.File]::ReadAllText($appOptions), '(?m)^password=([A-F0-9]+)\r?$')
    if (!$passwordMatch.Success) { throw 'Invalid bundle database credentials file.' }
    $config = "SQL_ENABLED`nADDRESS 127.0.0.1`nPORT $DatabasePort`nFEEDBACK_DATABASE bluemoon`nFEEDBACK_DATABASE_WHITELIST bluemoon`nFEEDBACK_LOGIN bluemoon`nFEEDBACK_PASSWORD $($passwordMatch.Groups[1].Value)`n"
    [IO.File]::WriteAllText((Join-Path $game 'config/entries/dbconfig.txt'), $config)

    $env:NETDM_IPC_MODE = 'v1'
    $env:NETDM_PIPE_NAME = 'bluemoon-' + [Guid]::NewGuid().ToString('N')
    $env:NETDM_OBSERVE = '1'
    $env:NETDM_SERVER_ID = $ServerId
    $env:NETDM_BUNDLE_STOP_FILE = 'data/.netdm-stop-' + [Guid]::NewGuid().ToString('N')
    $gameStopPath = Join-Path $game $env:NETDM_BUNDLE_STOP_FILE
    $env:NetDM__HttpPort = "$HostPort"
    $env:NetDM__Deployment = 'true'
    # The round's research journal lives with the instance state, not with the Host binaries.
    $env:NetDM__ResearchDirectory = Join-Path $state 'research'
    $base ="http://127.0.0.1:$HostPort"
    $netdm = Start-Host
    $daemon = Start-OwnedProcess (Join-Path $PSScriptRoot 'runtime/byond/bin/dd.exe') @(
        'tgstation.dmb', "$GamePort", '-home', $game, '-trusted', '-invisible', '-close'
    ) $game 'game'
    $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    $lastStream = $null
    $lastStorage = $null
    $hostRestarts = [Collections.Generic.Queue[DateTime]]::new()
    while (!(Test-Path -LiteralPath $stopRequest) -and !$daemon.HasExited -and !$database.HasExited) {
        if ($netdm.HasExited) {
            # Exit 70 is a packet worker fault. Restarts are bounded to avoid a crash loop.
            Write-Warning "NetDM Host exited with code $($netdm.ExitCode); restarting it for the running world."
            $netdm.Dispose()
            $netdm = $null
            $now = [DateTime]::UtcNow
            while ($hostRestarts.Count -gt 0 -and $now - $hostRestarts.Peek() -gt [TimeSpan]::FromMinutes(10)) {
                $null = $hostRestarts.Dequeue()
            }
            if ($hostRestarts.Count -ge 5) { throw 'NetDM Host failed 5 times in 10 minutes. See state/host logs.' }
            $hostRestarts.Enqueue($now)
            [IO.File]::Delete($runStatus)
            $netdm = Start-Host
            $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
        }
        # The world is watched by its network session. Research storage that cannot be written blocks
        # research only: the round goes on, and the operator is told once per change.
        $readiness = $null
        try { $readiness = Invoke-RestMethod "$base/api/readiness/network" -TimeoutSec 1 } catch { }
        if ($readiness -and $readiness.networkReady -and $readiness.research -ne $lastStorage) {
            if ($readiness.research -ne 'ready') {
                Write-Warning "Research storage is '$($readiness.research)': research is blocked until it recovers. See state/host logs."
            }
            elseif ($lastStorage) { Write-Host 'Research storage is ready again.' }
            $lastStorage = $readiness.research
        }
        if ($readiness -and $readiness.networkReady) {
            if ($lastStream -ne $readiness.streamId -or !(Test-Path -LiteralPath $runStatus)) {
                $lastStream = $readiness.streamId
                [ordered]@{ daemonPid = $daemon.Id; hostPid = $netdm.Id; dbPid = $database.Id; streamId = $lastStream
                    gamePort = $GamePort; hostPort = $HostPort; databasePort = $DatabasePort; schema = $revision } |
                    ConvertTo-Json | Set-Content -LiteralPath $runStatus -Encoding Ascii
                Write-Host "BlueMoon Observe ready at $base (DreamDaemon $($daemon.Id))."
            }
            $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
        }
        elseif ([DateTime]::UtcNow -ge $deadline) { throw 'BlueMoon Observe readiness timed out. See state/game logs.' }
        else { [IO.File]::Delete($runStatus) }
        Start-Sleep -Milliseconds 500
    }
    if ($daemon.HasExited -and $daemon.ExitCode -ne 0) { throw "DreamDaemon exited with $($daemon.ExitCode)." }
    if ($database.HasExited) { throw 'MariaDB exited while BlueMoon was running.' }
    $requestedStop = Test-Path -LiteralPath $stopRequest
}
finally {
    $shutdownFailure = $null
    [IO.File]::Delete($runStatus)
    [IO.File]::Delete($stopRequest)
    [IO.File]::Delete($bootstrap)
    if ($daemon -and !$daemon.HasExited -and $gameStopPath) {
        $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($gameStopPath))
        [IO.File]::WriteAllText($gameStopPath, 'stop')
        if (!$daemon.WaitForExit(90000)) { $shutdownFailure = 'DreamDaemon shutdown timed out.' }
    }
    if ($daemon -and $daemon.HasExited -and $daemon.ExitCode -ne 0) {
        $shutdownFailure = "DreamDaemon exited with $($daemon.ExitCode) during shutdown."
    }
    Stop-OwnedProcess $daemon
    if ($gameStopPath) { [IO.File]::Delete($gameStopPath) }
    Stop-OwnedProcess $netdm
    if ($database -and !$database.HasExited -and (Test-Path -LiteralPath $rootOptions)) {
        try { $null = Invoke-Database $rootOptions 'SHUTDOWN'; $null = $database.WaitForExit(10000) } catch { }
    }
    Stop-OwnedProcess $database
    $lease.Dispose()
    if ($shutdownFailure) {
        if ($requestedStop) { throw $shutdownFailure }
        Write-Warning $shutdownFailure
    }
}
