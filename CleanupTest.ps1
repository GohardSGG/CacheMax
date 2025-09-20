# Cleanup test files and cache

Write-Host "=== Cleaning up test environment ===" -ForegroundColor Yellow

# Stop any running CacheMax processes
Get-Process -Name "passthrough-mod" -ErrorAction SilentlyContinue | Stop-Process -Force
Write-Host "Stopped CacheMax processes"

# Clear cache directory
if (Test-Path "S:\Cache\*") {
    Remove-Item "S:\Cache\*" -Recurse -Force
    Write-Host "Cleared S:\Cache directory"
}

# Clean up test files
if (Test-Path "A:\Test\*") {
    Remove-Item "A:\Test\*" -Recurse -Force
    Write-Host "Cleared A:\Test directory"
}

# Clean up .original directories
if (Test-Path "A:\Test.original") {
    Remove-Item "A:\Test.original" -Recurse -Force
    Write-Host "Removed A:\Test.original"
}

Write-Host "✅ Cleanup completed!" -ForegroundColor Green