# Test cache hit monitoring

$TestDir = "A:\Test"
$CacheDir = "S:\Cache"
$TestFile = "benchmark_test.dat"
$PassthroughExe = "C:\Code\CacheMax\CacheMax.FileSystem\Release\passthrough-mod.exe"
$OriginalPath = "$TestDir.original"

Write-Host "=== Testing Cache Hit Monitoring ===" -ForegroundColor Green

# Stop any existing processes
Get-Process -Name "passthrough-mod" -ErrorAction SilentlyContinue | Stop-Process -Force

# Ensure cache file exists (from previous test)
$CacheFilePath = "S:\Cache\benchmark_test.dat"
if (!(Test-Path $CacheFilePath)) {
    Write-Host "Cache file not found, run previous test first!" -ForegroundColor Red
    exit 1
}

Write-Host "Cache file exists: $((Get-Item $CacheFilePath).Length / 1MB) MB" -ForegroundColor Green

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
$TestFilePath = Join-Path $TestDir $TestFile
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

# Test small sequential reads to see if cache is working
Write-Host "`nTesting cache effectiveness..." -ForegroundColor Yellow

$Buffer = New-Object byte[] 4096
$FileStream = [System.IO.File]::OpenRead($TestFilePath)

try {
    # Read from start of file (should trigger cache creation if not already cached)
    Write-Host "  Reading from start of file..."
    $FileStream.Seek(0, [System.IO.SeekOrigin]::Begin) | Out-Null
    $BytesRead = $FileStream.Read($Buffer, 0, 4096)
    Write-Host "    Read $BytesRead bytes"

    # Read from middle of file
    Write-Host "  Reading from middle of file..."
    $FileStream.Seek(16777216, [System.IO.SeekOrigin]::Begin) | Out-Null
    $BytesRead = $FileStream.Read($Buffer, 0, 4096)
    Write-Host "    Read $BytesRead bytes"

    # Read from end of file
    Write-Host "  Reading from end of file..."
    $FileStream.Seek(-4096, [System.IO.SeekOrigin]::End) | Out-Null
    $BytesRead = $FileStream.Read($Buffer, 0, 4096)
    Write-Host "    Read $BytesRead bytes"

} finally {
    $FileStream.Close()
}

Write-Host "`nAfter reading, checking cache file status..." -ForegroundColor Cyan

# Check if cache file was updated/accessed
$CacheItem = Get-Item $CacheFilePath
Write-Host "  Cache file size: $($CacheItem.Length) bytes"
Write-Host "  Cache file LastAccess: $($CacheItem.LastAccessTime)"
Write-Host "  Cache file LastWrite: $($CacheItem.LastWriteTime)"

# Compare with original file
$OriginalItem = Get-Item "$OriginalPath\$TestFile"
Write-Host "  Original file size: $($OriginalItem.Length) bytes"
Write-Host "  Original file LastAccess: $($OriginalItem.LastAccessTime)"
Write-Host "  Original file LastWrite: $($OriginalItem.LastWriteTime)"

if ($CacheItem.Length -eq $OriginalItem.Length) {
    Write-Host "  ✅ Cache file size matches original" -ForegroundColor Green
} else {
    Write-Host "  ❌ Cache file size mismatch!" -ForegroundColor Red
}

# Performance comparison
Write-Host "`nQuick performance comparison..." -ForegroundColor Yellow

# Test via CacheMax (should use cache)
Write-Host "  Testing via CacheMax..."
$StartTime = Get-Date
$FileStream2 = [System.IO.File]::OpenRead($TestFilePath)
try {
    for ($i = 0; $i -lt 100; $i++) {
        $Offset = $i * 4096
        $FileStream2.Seek($Offset, [System.IO.SeekOrigin]::Begin) | Out-Null
        $FileStream2.Read($Buffer, 0, 4096) | Out-Null
    }
} finally {
    $FileStream2.Close()
}
$CacheMaxTime = (Get-Date - $StartTime).TotalMilliseconds
Write-Host "    CacheMax: $([math]::Round($CacheMaxTime, 1)) ms for 100 x 4KB reads"

# Test direct cache file
Write-Host "  Testing direct cache file..."
$StartTime = Get-Date
$FileStream3 = [System.IO.File]::OpenRead($CacheFilePath)
try {
    for ($i = 0; $i -lt 100; $i++) {
        $Offset = $i * 4096
        $FileStream3.Seek($Offset, [System.IO.SeekOrigin]::Begin) | Out-Null
        $FileStream3.Read($Buffer, 0, 4096) | Out-Null
    }
} finally {
    $FileStream3.Close()
}
$DirectTime = (Get-Date - $StartTime).TotalMilliseconds
Write-Host "    Direct cache: $([math]::Round($DirectTime, 1)) ms for 100 x 4KB reads"

$Efficiency = ($DirectTime / $CacheMaxTime) * 100
Write-Host "    CacheMax efficiency: $([math]::Round($Efficiency, 1))%" -ForegroundColor $(if ($Efficiency -gt 50) { "Green" } else { "Red" })

# Cleanup
Write-Host "`nCleaning up..." -ForegroundColor Yellow
$Process | Stop-Process -Force
Start-Sleep -Seconds 2

if (Test-Path $OriginalPath) {
    Rename-Item $OriginalPath $TestDir
}

Write-Host "`nTest completed!" -ForegroundColor Green