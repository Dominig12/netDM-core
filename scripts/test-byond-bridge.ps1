param(
    [string] $BuildPath = "native/byond/build",
    [string] $HostProject = "src/NetDM.Host/NetDM.Host.csproj"
)
$ErrorActionPreference = "Stop"
# GitHub windows-latest switched to VS 2026; local machines may use VS 2022.
# Probe installed VS via vswhere so both generations are supported.
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio/Installer/vswhere.exe"
$major = 0
if (Test-Path $vswhere) {
    $vs = (& $vswhere -latest -products "*" -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationVersion)
    if ($vs) { $major = [int](($vs | Select-Object -First 1).Split('.')[0]) }
}
$generator = switch ($major) {
    18 { "Visual Studio 18 2026" }
    17 { "Visual Studio 17 2022" }
    default { throw "Visual Studio C++ x86/x64 tools not found via vswhere. Install VS 2022/2026 C++ workload." }
}
Write-Host "Using native generator: $generator"
cmake -S native/byond -B $BuildPath -G $generator -A Win32
if ($LASTEXITCODE -ne 0) { throw "Win32 CMake configure failed" }
cmake --build $BuildPath --config Release
if ($LASTEXITCODE -ne 0) { throw "Win32 native compile failed" }
$hostLog = Join-Path ([IO.Path]::GetTempPath()) "netdm-byond-host.log"
$runner = Start-Process dotnet -ArgumentList @("run","--project",$HostProject,
    "-c","Release","--no-build") -PassThru -RedirectStandardOutput $hostLog
try {
    $ready = $false
    for ($n = 0; $n -lt 100; $n++) {
        try {
            $health = Invoke-RestMethod "http://127.0.0.1:5087/api/health" -TimeoutSec 2
            if ($health.status -eq "ok") { $ready = $true; break }
        } catch { }
        Start-Sleep -Milliseconds 150
    }
    if (-not $ready) { throw "NetDM.Host was not healthy; $hostLog" }
    $bin = Join-Path $BuildPath "Release"
    & (Join-Path $bin "netdm_bridge_test.exe")
    if ($LASTEXITCODE -ne 0) { throw "BYOND Win32 native bridge test failed" }
} finally {
    if (-not $runner.HasExited) { Stop-Process -Id $runner.Id -Force }
}
