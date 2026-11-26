param(
    [string] $GameRepository = '..\MOLOT-BlueMoon-Station',
    [string] $ChromePath = 'C:\Program Files\Google\Chrome\Application\chrome.exe',
    [string] $Destination = 'TestResults\p13\browser-proof'
)
# Renders the P13 network scene in a real Chromium (headless): the same module the TGUI window mounts.
# This is not the BYOND client browser, but it proves the PixiJS renderer, activity halos, agents,
# selection and the canvas fallback work outside jsdom. Artifacts are kept in Destination.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$game = (Resolve-Path -LiteralPath $GameRepository).Path
$tgui = Join-Path $game 'tgui'
$yarn = Get-ChildItem -LiteralPath (Join-Path $tgui '.yarn/releases') -Filter 'yarn-*.cjs' | Select-Object -First 1
if (!$yarn) { throw 'The pinned Yarn release is missing.' }
if (!(Test-Path -LiteralPath $ChromePath)) { throw "Chrome not found: $ChromePath" }
$out = [IO.Path]::GetFullPath((Join-Path $root $Destination))
$null = [IO.Directory]::CreateDirectory($out)
$harness = Join-Path $tgui '.netdm-harness'
try {
    $null = [IO.Directory]::CreateDirectory($harness)
    $entry = Join-Path $harness 'entry.ts'
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'netdm-browser-harness.ts') -Destination $entry -Force
    Push-Location $tgui
    try {
        node $yarn.FullName esbuild '.netdm-harness/entry.ts' --bundle --format=iife --target=es2020 `
            "--outfile=$(Join-Path $out 'bundle.js')" --log-level=warning
        if ($LASTEXITCODE -ne 0) { throw 'esbuild failed for the scene harness.' }
    }
    finally { Pop-Location }
    $html = '<!DOCTYPE html><html><head><meta charset="utf-8"><title>HARNESS_START</title>' +
        '<style>html,body{margin:0;background:#101217;} #host{width:800px;height:600px;}</style>' +
        '</head><body><div id="host"></div><script src="bundle.js"></script></body></html>'
    Set-Content -LiteralPath (Join-Path $out 'index.html') -Value $html -Encoding utf8NoBOM
    Copy-Item -LiteralPath $entry -Destination (Join-Path $out 'entry.ts') -Force
    $url = 'file:///' + ((Join-Path $out 'index.html') -replace '\\', '/')
    foreach ($case in @(@{ Name = 'webgl'; Flags = @('--enable-unsafe-swiftshader') },
                        @{ Name = 'canvas'; Flags = @('--disable-3d-apis') })) {
        $shot = Join-Path $out ("scene-{0}.png" -f $case.Name)
        $dom = & $ChromePath --headless=new --hide-scrollbars --window-size=820,620 --virtual-time-budget=10000 `
            @($case.Flags) ("--screenshot=$shot") '--dump-dom' $url 2>&1 | Out-String
        if ($dom -notmatch "HARNESS_OK:$($case.Name)") { throw "The scene did not render with the $($case.Name) backend." }
        Write-Host "scene $($case.Name): HARNESS_OK, screenshot $shot"
    }
}
finally {
    Remove-Item -LiteralPath $harness -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host 'NETDM_SCENE_BROWSER_PASS'
