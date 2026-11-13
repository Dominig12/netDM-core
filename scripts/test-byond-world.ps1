param(
    [Parameter(Mandatory)] [string] $BridgeDll,
    [string] $ByondBin = (Join-Path ${env:ProgramFiles(x86)} 'BYOND/bin'),
    [string] $HostDll = 'src/NetDM.Host/bin/Release/net10.0/NetDM.Host.dll'
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Use pwsh on native Windows for this test.' }

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$hostPath = (Resolve-Path -LiteralPath $HostDll).Path
$work = [IO.Directory]::CreateTempSubdirectory('netdm-world-').FullName
$pipe = 'netdm-world-' + [guid]::NewGuid().ToString('N')
Copy-Item -LiteralPath (Join-Path $root 'tests/byond-world/smoke.dme'),
    (Join-Path $root 'tests/byond-world/smoke.dm'), (Join-Path $root 'dm/netdm_bridge.dm'),
    (Join-Path $root 'dm/netdm_services.dm') -Destination $work
Copy-Item -LiteralPath $BridgeDll -Destination (Join-Path $work 'netdm_byond.dll')
Set-Content -LiteralPath (Join-Path $work 'bridge-path.dm') -Value '#define NETDM_DLL "./netdm_byond.dll"'

function Start-Child([string] $Executable, [string[]] $Arguments, [string] $Directory) {
    $info = [Diagnostics.ProcessStartInfo]::new($Executable)
    $info.WorkingDirectory = $Directory
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.Environment['NETDM_IPC_MODE'] = 'v1'
    $info.Environment['NETDM_PIPE_NAME'] = $pipe
    $info.Environment['NetDM__HttpPort'] = '0'
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($info)
    return @{
        Process = $process
        Stdout = $process.StandardOutput.ReadToEndAsync()
        Stderr = $process.StandardError.ReadToEndAsync()
    }
}

function Stop-Child($Child) {
    if ($null -eq $Child) { return }
    if (-not $Child.Process.HasExited) {
        $Child.Process.Kill($true)
        $Child.Process.WaitForExit()
    }
    $Child.Process.Dispose()
}

$compiler = $netdmHost = $daemon = $null
try {
    $compiler = Start-Child (Join-Path $ByondBin 'dm.exe') @('smoke.dme') $work
    if (-not $compiler.Process.WaitForExit(30000)) { throw 'DreamMaker timed out.' }
    Write-Host $compiler.Stdout.GetAwaiter().GetResult()
    if ($compiler.Process.ExitCode -ne 0) {
        Write-Host $compiler.Stderr.GetAwaiter().GetResult()
        throw 'DreamMaker compilation failed.'
    }

    $netdmHost = Start-Child (Get-Command dotnet -ErrorAction Stop).Source @($hostPath) $root
    $daemon = Start-Child (Join-Path $ByondBin 'dd.exe') @('smoke.dmb', '0', '-trusted', '-invisible', '-close') $work
    if (-not $daemon.Process.WaitForExit(30000)) { throw 'DreamDaemon timed out.' }
    $log = Get-Content -Raw -LiteralPath (Join-Path $work 'smoke.log')
    Write-Host $log
    if ($daemon.Process.ExitCode -ne 0 -or $log -notmatch 'NETDM_WORLD_PASS') {
        Write-Host $daemon.Stdout.GetAwaiter().GetResult()
        Write-Host $daemon.Stderr.GetAwaiter().GetResult()
        throw "Real DreamDaemon V1 smoke failed, exit code $($daemon.Process.ExitCode)."
    }
}
finally {
    Stop-Child $compiler
    Stop-Child $daemon
    Stop-Child $netdmHost
    Write-Host "Smoke artifacts: $work"
}
