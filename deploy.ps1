# deploy.ps1 - local pre-flight before pushing to GitHub.
#
# Usage:
#   .\deploy.ps1 -Message "Implement X"             # build, test, regen docs, format check, commit, push
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

Step 1 5 "dotnet restore"
dotnet restore

Step 2 5 "dotnet build (Release)"
dotnet build --no-restore --configuration Release

if (-not $NoTest) {
    Step 3 5 "dotnet test"
    dotnet test --no-build --configuration Release
}
else {
    Step 3 5 "tests skipped (-NoTest)"
}

Step 4 5 "regenerate API docs"
python tools/gen_api_docs.py

Step 5 5 "dotnet format --verify-no-changes"
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
