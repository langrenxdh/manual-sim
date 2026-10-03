# Builds a self-contained Windows package of the simulator (M10): no .NET install needed on the target PC.
# Usage (from the repository root):  pwsh scripts/publish.ps1  [-Output publish]
# Result: publish/ManualSim/ (ManualSim.exe + config/) and publish/ManualSim.zip.
# Machine-specific and personal files are left out: config/setup.json (first-time setup runs on the new PC),
# scores/ and telemetry/ (they are created next to config/ as you drive).
param(
    [string]$Output = "publish"
)
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$package = Join-Path $root "$Output/ManualSim"
if (Test-Path $package) { Remove-Item -Recurse -Force $package }

dotnet publish (Join-Path $root "src/Sim.App/Sim.App.csproj") -c Release -r win-x64 --self-contained true `
    -p:DebugType=none -o $package
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
# The launcher knows its assembly by name (Sim.App.dll), so only the .exe is renamed.
Rename-Item (Join-Path $package "Sim.App.exe") "ManualSim.exe"

# The app finds config/ by walking up from the executable, so it sits next to ManualSim.exe.
$config = Join-Path $package "config"
New-Item -ItemType Directory -Force $config | Out-Null
Get-ChildItem (Join-Path $root "config") -File | Where-Object { $_.Name -ne "setup.json" } |
    Copy-Item -Destination $config
Copy-Item (Join-Path $root "docs/install.md"), (Join-Path $root "docs/install.en.md") -Destination $package

$zip = Join-Path $root "$Output/ManualSim.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path $package -DestinationPath $zip
Write-Host "Package: $package"
Write-Host "Zip:     $zip"
