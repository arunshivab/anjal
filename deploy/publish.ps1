# Build a self-contained linux-x64 release of Anjal on the Windows laptop
# and pack it as anjal-<version>.tar.gz for scp to the VM.
#
#   .\deploy\publish.ps1 -Version 1.0.0-rc.1
param(
    [Parameter(Mandatory = $true)][string]$Version
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot

# ---- Guards: build only what the version number claims ----
# The version in Directory.Build.props is the one source of truth. A mismatch
# means the working tree is not the release being asked for.
$props = [xml](Get-Content (Join-Path $repo "Directory.Build.props") -Raw)
$sourceVersion = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if ($sourceVersion -ne $Version) {
    throw "Asked for $Version but Directory.Build.props says $sourceVersion. Check out the v$Version tag first."
}
# The commit must be the one tagged v$Version, with nothing uncommitted on top.
$tag = (git -C $repo describe --exact-match --tags HEAD 2>$null)
if ($tag -ne "v$Version") {
    throw "HEAD is not tagged v$Version (it is '$tag'). Run: git checkout v$Version"
}
if (git -C $repo status --porcelain --untracked-files=no) {
    throw "The working tree has uncommitted changes; a release must be built from the tag exactly."
}
$out = Join-Path $repo "artifacts\anjal-$Version"
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path "$out\bin" | Out-Null

foreach ($app in @("Server", "Webmail")) {
    dotnet publish "$repo\src\Anjal.$app\Anjal.$app.csproj" -c Release -r linux-x64 --self-contained true `
        -p:PublishSingleFile=false -p:DebugType=none -p:Version=$Version -o "$out\$($app.ToLower())"
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $app" }
}

Copy-Item "$repo\deploy\backup.sh", "$repo\deploy\restore.sh", "$repo\deploy\install.sh" "$out\bin\"
Copy-Item "$repo\deploy\*.service", "$repo\deploy\*.timer", "$repo\deploy\*.example" "$out\bin\"
Copy-Item "$repo\tools\sql\schema.sql" "$out\bin\"
Copy-Item "$repo\deploy\DEPLOY.md" "$out\"

# Normalise line endings for scripts, units and templates (git may have checked them out CRLF).
foreach ($f in Get-ChildItem "$out\bin\*" -Include *.sh, *.service, *.timer, *.example) {
    $text = (Get-Content $f.FullName -Raw) -replace "`r`n", "`n"
    [System.IO.File]::WriteAllText($f.FullName, $text, [System.Text.UTF8Encoding]::new($false))
}

tar -czf "$repo\artifacts\anjal-$Version.tar.gz" -C "$repo\artifacts" "anjal-$Version"
Write-Host "Built $repo\artifacts\anjal-$Version.tar.gz"
Write-Host "Copy to the VM:  scp -i $env:USERPROFILE\.ssh\anjal_e2e .\artifacts\anjal-$Version.tar.gz arun@<vm-ip>:~/"
