# Setup GitHub Pages for publishing the user guide
param()

$owner = "kellylford"
$repo = "QuickMail"

Write-Host "Setting up GitHub Pages for $owner/$repo..." -ForegroundColor Green

# Serve GitHub Pages from GitHub Actions deploys (publish-user-guide.yml), not from the
# gh-pages branch. Serving a branch would let anyone with write access publish the site by
# pushing to it; an Actions deploy has to pass the github-pages environment, which admits
# only main and v* tags. If Pages has never been enabled on the repo, create it first with
#   gh api repos/kellylford/QuickMail/pages --method POST -f build_type=workflow
$response = gh api repos/$owner/$repo/pages --method PUT -f "build_type=workflow" 2>&1
if ($LASTEXITCODE -eq 0) {
    $response = gh api repos/$owner/$repo/pages --jq '.html_url' 2>&1
}

if ($LASTEXITCODE -eq 0) {
    Write-Host "GitHub Pages enabled" -ForegroundColor Green
    Write-Host "Published at: $response"
} else {
    Write-Host "Failed to enable GitHub Pages" -ForegroundColor Red
    Write-Host $response
    exit 1
}

Write-Host ""
Write-Host "Setup complete!" -ForegroundColor Green
Write-Host "To publish the user guide:"
Write-Host "  1. Go to Actions → Publish User Guide → Run workflow (on main)"
Write-Host "  2. Check deployment status at: $response"
