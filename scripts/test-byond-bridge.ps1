param(
    [string] $BuildPath = "native/byond/build",
    [string] $HostProject = "src/NetDM.Host/NetDM.Host.csproj",
    # An already built directory with netdm_byond.dll and netdm_bridge_test.exe,
    # for example the output of native/byond/build.ps1 -Destination <dir>.
    [string] $BridgeBuild,
    # Use the portable MinGW-w64 toolchain even when Visual Studio is installed.
    [switch] $Portable
)
$ErrorActionPreference = "Stop"
Set-Location (Split-Path $PSScriptRoot)

function Assert-BridgeBin([string] $Directory) {
    foreach ($name in "netdm_byond.dll", "netdm_bridge_test.exe") {
        if (-not (Test-Path -LiteralPath (Join-Path $Directory $name))) {
            throw "$name is missing in $Directory. Build it with native/byond/build.ps1 -Destination $Directory."
        }
    }
}

$bin = $null
if ($BridgeBuild) {
    $bin = (Resolve-Path -LiteralPath $BridgeBuild).Path
    Write-Host "Using the prebuilt bridge in $bin"
}
else {
    # Visual Studio stays supported for machines that already have it; without it the portable
    # MinGW-w64 toolchain from native/byond/build.ps1 builds the same Win32 DLL.
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio/Installer/vswhere.exe"
    $major = 0
    if (-not $Portable -and (Test-Path -LiteralPath $vswhere)) {
        $vs = (& $vswhere -latest -products "*" -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationVersion)
        if ($vs) { $major = [int](($vs | Select-Object -First 1).Split('.')[0]) }
    }
    if ($major) {
        $generator = if ($major -ge 18) { "Visual Studio 18 2026" } else { "Visual Studio 17 2022" }
        Write-Host "Using native generator: $generator"
        cmake -S native/byond -B $BuildPath -G $generator -A Win32
        if ($LASTEXITCODE -ne 0) { throw "Win32 CMake configure failed" }
        cmake --build $BuildPath --config Release
        if ($LASTEXITCODE -ne 0) { throw "Win32 native compile failed" }
        $bin = Join-Path $BuildPath "Release"
    }
    else {
        Write-Host "Visual Studio C++ x86 tools not found; using the portable MinGW-w64 toolchain."
        $output = Join-Path $BuildPath "portable"
        pwsh -NoProfile -File native/byond/build.ps1 -Destination $output
        if ($LASTEXITCODE -ne 0) { throw "The portable Win32 bridge build failed." }
        $bin = (Resolve-Path -LiteralPath $output).Path
    }
}
Assert-BridgeBin $bin

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
    & (Join-Path $bin "netdm_bridge_test.exe")
    if ($LASTEXITCODE -ne 0) { throw "BYOND Win32 native bridge test failed" }
} catch {
    Write-Host "==== NetDM.Host stdout ===="
    if (Test-Path $hostLog) { Get-Content $hostLog -Tail 160 }
    throw
} finally {
    if (-not $runner.HasExited) { Stop-Process -Id $runner.Id -Force }
}
