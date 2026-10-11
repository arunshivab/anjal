# deploy.ps1 - local pre-flight before pushing to GitHub.
#
# Usage:
#   .\deploy.ps1 -Message "Implement X"             # build, test, regen docs, line endings, format check, commit, push
#   .\deploy.ps1 -Message "WIP" -NoTest             # skip tests
#   .\deploy.ps1 -Message "WIP" -NoPush              # do everything except commit/push

param(
    [Parameter(Mandatory = $true)][string]$Message,
    [switch]$NoTest,
    [switch]$NoPush
)

$ErrorActionPreference = "Stop"

function Step($n, $total, $label) {
    Write-Host ""
    Write-Host "[$n/$total] $label" -ForegroundColor Cyan
}

Step 1 6 "dotnet restore"
dotnet restore

Step 2 6 "dotnet build (Release)"
dotnet build --no-restore --configuration Release

if (-not $NoTest) {
    Step 3 6 "dotnet test"
    dotnet test --no-build --configuration Release
}
else {
    Step 3 6 "tests skipped (-NoTest)"
}

Step 4 6 "regenerate API docs"
python tools/gen_api_docs.py

# Owner, 11 Oct 2026: every file is put back to its line-ending rule (.gitattributes) and
# checked before format runs, so CI never sees an ending or a byte-order mark it rejects.
Step 5 6 "line endings (tools/check_eol.py --fix)"
python tools/check_eol.py --fix
if ($LASTEXITCODE -ne 0) { throw "line endings: see the list above" }

Step 6 6 "dotnet format --verify-no-changes"
dotnet format --verify-no-changes

if (-not $NoPush) {
    Write-Host ""
    Write-Host "Committing and pushing..." -ForegroundColor Cyan
    git add -A
    git commit -m $Message
    git push
    Write-Host ""
    Write-Host "Pushed." -ForegroundColor Green
}
else {
    Write-Host ""
    Write-Host "Push skipped (-NoPush). Working tree:" -ForegroundColor Yellow
    git status --short
}
