# Test cache file creation manually

$TestDir = "A:\Test"
$CacheDir = "S:\Cache"
$TestFile = "benchmark_test.dat"
$PassthroughExe = "C:\Code\CacheMax\CacheMax.FileSystem\Release\passthrough-mod.exe"
$OriginalPath = "$TestDir.original"

Write-Host "=== Testing Cache File Creation ===" -ForegroundColor Green

# Stop any existing processes
Get-Process -Name "passthrough-mod" -ErrorAction SilentlyContinue | Stop-Process -Force

# Ensure test file exists
$TestFilePath = Join-Path $TestDir $TestFile
if (!(Test-Path $TestFilePath)) {
    Write-Host "Test file not found: $TestFilePath" -ForegroundColor Red
    exit 1
}

Write-Host "Test file size: $((Get-Item $TestFilePath).Length / 1MB) MB" -ForegroundColor Cyan

# Clear any existing cache
$CacheFilePath = "S:\Cache\Test\benchmark_test.dat"
if (Test-Path $CacheFilePath) {
    Write-Host "Removing existing cache file..." -ForegroundColor Yellow
    Remove-Item $CacheFilePath -Force
}

# Setup for CacheMax
if (Test-Path $OriginalPath) {
    Remove-Item $OriginalPath -Recurse -Force
}
Rename-Item $TestDir $OriginalPath

# Start CacheMax
$Arguments = "-p `"$OriginalPath`" -c `"$CacheDir`" -m `"$TestDir`""
Write-Host "Starting CacheMax: $Arguments" -ForegroundColor Cyan

$Process = Start-Process -FilePath $PassthroughExe -ArgumentList $Arguments -PassThru -WindowStyle Minimized
Start-Sleep -Seconds 3

if ($Process.HasExited) {
    Write-Host "CacheMax startup failed!" -ForegroundColor Red
    if (Test-Path $OriginalPath) {
        Rename-Item $OriginalPath $TestDir
    }
    exit 1
}

# Wait for mount
Write-Host "Waiting for mount..." -ForegroundColor Yellow
$MountReady = $false
for ($i = 0; $i -lt 30; $i++) {
    if (Test-Path $TestFilePath) {
        $MountReady = $true
        break
    }
    Start-Sleep -Seconds 1
    Write-Host "." -NoNewline
}
Write-Host ""

if (-not $MountReady) {
    Write-Host "Mount failed!" -ForegroundColor Red
    $Process | Stop-Process -Force
    if (Test-Path $OriginalPath) {
        Rename-Item $OriginalPath $TestDir
    }
    exit 1
}

Write-Host "Mount successful!" -ForegroundColor Green

# Check cache file BEFORE any read operations
Write-Host "`nCache status BEFORE reading:" -ForegroundColor Cyan
if (Test-Path $CacheFilePath) {
    $CacheSize = (Get-Item $CacheFilePath).Length
    Write-Host "  Cache file exists: $CacheSize bytes" -ForegroundColor Green
} else {
    Write-Host "  Cache file does not exist" -ForegroundColor Red
}

# Now read from the file to trigger cache creation
Write-Host "`nReading file to trigger cache creation..." -ForegroundColor Yellow
$Buffer = New-Object byte[] 4096
$FileStream = [System.IO.File]::OpenRead($TestFilePath)
try {
    $FileStream.Read($Buffer, 0, 4096) | Out-Null
    Write-Host "Successfully read 4KB from file" -ForegroundColor Green
} finally {
    $FileStream.Close()
}

# Check cache file AFTER read operation
Write-Host "`nCache status AFTER reading:" -ForegroundColor Cyan
if (Test-Path $CacheFilePath) {
    $CacheSize = (Get-Item $CacheFilePath).Length
    Write-Host "  Cache file exists: $CacheSize bytes" -ForegroundColor Green
} else {
    Write-Host "  Cache file still does not exist" -ForegroundColor Red
}

# Wait a bit more for async copy
Write-Host "`nWaiting 5 seconds for async copy..." -ForegroundColor Yellow
Start-Sleep -Seconds 5

# Final cache check
Write-Host "`nFinal cache status:" -ForegroundColor Cyan
if (Test-Path $CacheFilePath) {
    $CacheSize = (Get-Item $CacheFilePath).Length
    $OriginalSize = (Get-Item "$OriginalPath\$TestFile").Length
    Write-Host "  Cache file: $CacheSize bytes" -ForegroundColor Green
    Write-Host "  Original file: $OriginalSize bytes" -ForegroundColor Cyan
    if ($CacheSize -eq $OriginalSize) {
        Write-Host "  ✅ Cache copy complete and correct!" -ForegroundColor Green
    } else {
        Write-Host "  ⚠️  Cache copy incomplete or wrong size" -ForegroundColor Yellow
    }
} else {
    Write-Host "  ❌ Cache file was never created" -ForegroundColor Red
    Write-Host "  This indicates the cache creation logic is not working" -ForegroundColor Red
}

# Check cache directory contents
Write-Host "`nCache directory contents:" -ForegroundColor Cyan
Get-ChildItem "S:\Cache\" -Recurse | ForEach-Object {
    Write-Host "  $($_.FullName) ($($_.Length) bytes)" -ForegroundColor White
}

# Cleanup
Write-Host "`nCleaning up..." -ForegroundColor Yellow
$Process | Stop-Process -Force
Start-Sleep -Seconds 2

if (Test-Path $OriginalPath) {
    Rename-Item $OriginalPath $TestDir
}

Write-Host "`nTest completed!" -ForegroundColor Green