# Shared by setup-bluemoon-stand.ps1 and play-bluemoon.ps1. Dot-source it: . "$PSScriptRoot\tgui-tools.ps1"

# The game pins Node 24 (dependencies.sh); Vite 7 refuses anything older than 20.19 or 22.12.
function Assert-NodeForTgui {
    $text = (node --version) -replace '^v', ''
    $version = [version]($text -replace '[^0-9.].*$', '')
    $usable = ($version.Major -eq 20 -and $version.Minor -ge 19) -or ($version.Major -eq 22 -and $version.Minor -ge 12) -or
        $version.Major -eq 23 -or $version.Major -ge 24
    if (-not $usable) {
        throw "Node.js $text is too old for the TGUI build (Vite needs 20.19+ or 22.12+; the game uses 24). " +
            'Install Node.js 24 from nodejs.org, or any current LTS: winget install OpenJS.NodeJS.LTS'
    }
    if ($version.Major -lt 24) { Write-Warning "Node.js $text works, but the game is built with Node.js 24." }
}

# Installs TGUI's packages from its lockfile every time: a leftover install from an older checkout misses
# packages added since (pixi.js for the network map), and an up-to-date install takes seconds.
function Install-TguiPackages([string] $GameRepository) {
    Push-Location (Join-Path $GameRepository 'tgui')
    try {
        $yarn = Get-ChildItem -LiteralPath '.yarn/releases' -Filter 'yarn-*.cjs' | Select-Object -First 1
        if (!$yarn) { throw 'The pinned Yarn release is missing in tgui/.yarn/releases.' }
        node $yarn.FullName install
        if ($LASTEXITCODE -ne 0) { throw 'yarn install failed in tgui.' }
    }
    finally { Pop-Location }
}
