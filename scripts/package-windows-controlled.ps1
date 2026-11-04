$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$sourceHead = (git -C $repo rev-parse HEAD).Trim()
$output = Join-Path $repo "dist/controlled/$sourceHead/windows"
if (Test-Path $output) { throw "Refusing to replace existing package output: $output" }
$stage = Join-Path $output "stage"
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Push-Location (Join-Path $repo "windows")
try {
    foreach ($project in @("MotionClicker.Windows", "MotionClicker.Fixture")) {
        dotnet restore "$project/$project.csproj" --locked-mode
        if ($LASTEXITCODE -ne 0) { throw "Locked restore failed: $project" }
        dotnet publish "$project/$project.csproj" -c Release --no-restore -p:Platform=x64 -o (Join-Path $stage $project)
        if ($LASTEXITCODE -ne 0) { throw "Native publish failed: $project" }
        if (!(Test-Path (Join-Path $stage "$project/$project.exe"))) { throw "Native executable missing: $project" }
    }
    $icon = Join-Path $stage "MotionClicker.Windows/Assets/MotionClicker.ico"
    if (!(Test-Path $icon)) { throw "Runtime icon missing from published package" }
    if ((Get-FileHash $icon).Hash -ne (Get-FileHash "MotionClicker.Windows/Assets/MotionClicker.ico").Hash) { throw "Published icon mismatch" }
    $signatures = @{}
    foreach ($project in @("MotionClicker.Windows", "MotionClicker.Fixture")) {
        $signatures[$project] = (Get-AuthenticodeSignature (Join-Path $stage "$project/$project.exe")).Status.ToString()
    }
    $signatures | ConvertTo-Json | Set-Content (Join-Path $stage "signing-facts.json")
    dotnet --info | Out-File (Join-Path $stage "toolchain.txt")
    Copy-Item (Join-Path $repo "contracts/FIXTURE.md") (Join-Path $stage "CONTROLLED-ACCEPTANCE.md")
    Copy-Item (Join-Path $repo "LICENSE") (Join-Path $stage "LICENSE")
    python (Join-Path $repo "scripts/write-package-identity.py") --root $stage --platform Windows --toolchain ((dotnet --version).Trim()) --signing ($signatures | ConvertTo-Json -Compress)
    if ($LASTEXITCODE -ne 0) { throw "Artifact identity failed" }
    $archive = Join-Path $output "MotionClicker-controlled-windows-x64-$sourceHead.zip"
    Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $archive
    Copy-Item (Join-Path $stage "artifact-identity.json") $output
    @($archive, (Join-Path $output "artifact-identity.json")) | ForEach-Object {
        "$((Get-FileHash -Algorithm SHA256 $_).Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($_))"
    } | Set-Content (Join-Path $output "SHA256SUMS")
    Write-Output $archive
} finally { Pop-Location }
