# Build a self-contained linux-x64 release of Anjal on the Windows laptop
# and pack it as anjal-<version>.tar.gz for scp to the VM.
#
#   .\deploy\publish.ps1 -Version 0.13.0
param(
    [Parameter(Mandatory = $true)][string]$Version
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$out = Join-Path $repo "artifacts\anjal-$Version"
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path "$out\bin" | Out-Null

foreach ($app in @("Server", "Webmail")) {
    dotnet publish "$repo\src\Anjal.$app\Anjal.$app.csproj" -c Release -r linux-x64 --self-contained true `
        -p:PublishSingleFile=false -p:DebugType=none -o "$out\$($app.ToLower())"
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
Write-Host "Copy to the VM:  scp .\artifacts\anjal-$Version.tar.gz ubuntu@<vm-ip>:~/"
