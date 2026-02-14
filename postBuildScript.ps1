param(
    [Parameter(Mandatory=$true)]
    [string]$TargetDir
)

$TargetDir = $TargetDir.TrimEnd('\', '"')
$scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Path
$configPath = Join-Path $scriptPath "config.yaml"

if (-not (Test-Path $configPath)) {
    Write-Warning "config.yaml not found"
    exit 0
}

if (-not (Get-Module -ListAvailable -Name powershell-yaml)) {
    Write-Host "Installing powershell-yaml module..."
    Install-Module -Name powershell-yaml -Force -Scope CurrentUser -SkipPublisherCheck
}

Import-Module powershell-yaml

$pluginDll = Join-Path $TargetDir "FanslationStudio.Plugins.dll"
if (-not (Test-Path $pluginDll)) {
    Write-Warning "FanslationStudio.Plugins.dll not found"
    exit 1
}

Write-Host "Post-build deploy started"
$config = Get-Content $configPath -Raw | ConvertFrom-Yaml

foreach ($game in $config.games) {
    if (-not $game.export) {
        Write-Host "Skipping: $($game.name)"
        continue
    }

    Write-Host "Deploying: $($game.name)"

    $pluginPath = Join-Path $game.gamePath $game.pluginPath
    $releasePath = Join-Path $game.gamePath $game.releasePath | Join-Path -ChildPath $game.pluginPath

    New-Item -ItemType Directory -Path $pluginPath, $releasePath -Force | Out-Null
    Copy-Item -Path $pluginDll -Destination $pluginPath -Force
    Copy-Item -Path $pluginDll -Destination $releasePath -Force
    Write-Host "  Plugin DLL copied to plugin and release paths"
}

Write-Host "Post-build deploy completed"
