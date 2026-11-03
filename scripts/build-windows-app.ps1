$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Push-Location (Join-Path $repo "windows")
try {
    dotnet restore MotionClicker.Windows/MotionClicker.Windows.csproj --locked-mode
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    dotnet build MotionClicker.Windows/MotionClicker.Windows.csproj -c Release --no-restore -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} finally { Pop-Location }
