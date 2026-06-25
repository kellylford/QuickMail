# Setup GitHub Pages for publishing the user guide
# Run this once: .\scripts\setup-gh-pages.ps1

$owner = "kellylford"
$repo = "QuickMail"

Write-Host "Setting up GitHub Pages for $owner/$repo..." -ForegroundColor Green

# Enable GitHub Pages pointing to gh-pages branch
$response = gh api repos/$owner/$repo/pages `
  --method PUT `
  -f "source[branch]=gh-pages" `
  -f "source[path]=/" `
  --jq '.html_url'

if ($LASTEXITCODE -eq 0) {
    Write-Host "✓ GitHub Pages enabled" -ForegroundColor Green
    Write-Host "  Published at: $response"
} else {
    Write-Host "✗ Failed to enable GitHub Pages" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "Setup complete! To publish the user guide:" -ForegroundColor Green
Write-Host "  1. Go to Actions → Publish User Guide → Run workflow" -ForegroundColor Gray
Write-Host "  2. Check deployment status at: $response" -ForegroundColor Gray
