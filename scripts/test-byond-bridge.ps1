param(
    [string] $BuildPath = "native/byond/build",
    [string] $HostProject = "src/NetDM.Host/NetDM.Host.csproj"
)
$ErrorActionPreference = "Stop"
cmake -S native/byond -B $BuildPath -G "Visual Studio 17 2022" -A Win32
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
