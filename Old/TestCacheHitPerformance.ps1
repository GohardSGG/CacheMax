# Test CacheMax performance with existing cache file

$TestDir = "A:\Test"
$CacheDir = "S:\Cache"
$TestFile = "benchmark_test.dat"
$PassthroughExe = "C:\Code\CacheMax\CacheMax.FileSystem\Release\passthrough-mod.exe"
$OriginalPath = "$TestDir.original"
$CacheFilePath = "S:\Cache\Test\benchmark_test.dat"

Write-Host "=== Testing CacheMax with Existing Cache ===" -ForegroundColor Green

# Verify cache file exists
if (!(Test-Path $CacheFilePath)) {
    Write-Host "❌ Cache file not found: $CacheFilePath" -ForegroundColor Red
    exit 1
}

$CacheSize = (Get-Item $CacheFilePath).Length
Write-Host "✅ Cache file exists: $([math]::Round($CacheSize / 1MB, 1)) MB" -ForegroundColor Green

# Stop any existing processes
Get-Process -Name "passthrough-mod" -ErrorAction SilentlyContinue | Stop-Process -Force

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
    Write-Host "❌ CacheMax startup failed!" -ForegroundColor Red
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
    Write-Host "❌ Mount failed!" -ForegroundColor Red
    $Process | Stop-Process -Force
    if (Test-Path $OriginalPath) {
        Rename-Item $OriginalPath $TestDir
    }
    exit 1
}

Write-Host "✅ Mount successful!" -ForegroundColor Green

# High-precision performance test (identical to main test)
Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Diagnostics;

public class HighPrecisionFileTest {
    [DllImport("kernel32.dll")]
    public static extern bool QueryPerformanceCounter(out long lpPerformanceCount);

    [DllImport("kernel32.dll")]
    public static extern bool QueryPerformanceFrequency(out long lpFrequency);

    public static double TestRandomRead(string filePath, int numOperations, int blockSize) {
        long frequency;
        QueryPerformanceFrequency(out frequency);

        byte[] buffer = new byte[blockSize];
        Random random = new Random();

        using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read)) {
            long fileSize = fs.Length;
            long maxOffset = fileSize - blockSize;

            long startTime;
            QueryPerformanceCounter(out startTime);

            for (int i = 0; i < numOperations; i++) {
                // Random offset aligned to 4K boundary
                long offset = (long)(random.NextDouble() * (maxOffset / blockSize)) * blockSize;
                fs.Seek(offset, SeekOrigin.Begin);
                fs.Read(buffer, 0, blockSize);
            }

            long endTime;
            QueryPerformanceCounter(out endTime);

            double elapsed = (double)(endTime - startTime) / frequency;
            double totalMB = (numOperations * blockSize) / (1024.0 * 1024.0);
            return totalMB / elapsed;
        }
    }
}
"@

# Test CacheMax with existing cache (should be fast!)
Write-Host "`n=== Testing CacheMax Performance (Cache Should Hit) ===" -ForegroundColor Magenta
try {
    $Speed = [HighPrecisionFileTest]::TestRandomRead($TestFilePath, 1000, 4096)
    Write-Host "CacheMax Speed: $([math]::Round($Speed, 2)) MB/s" -ForegroundColor Green

    # Compare with direct cache access
    Write-Host "`n=== Testing Direct Cache Access ===" -ForegroundColor Magenta
    $DirectSpeed = [HighPrecisionFileTest]::TestRandomRead($CacheFilePath, 1000, 4096)
    Write-Host "Direct Cache Speed: $([math]::Round($DirectSpeed, 2)) MB/s" -ForegroundColor Green

    $Efficiency = ($Speed / $DirectSpeed) * 100
    Write-Host "`n=== Cache Hit Analysis ===" -ForegroundColor Cyan
    Write-Host "CacheMax efficiency: $([math]::Round($Efficiency, 1))%"

    if ($Efficiency -gt 80) {
        Write-Host "✅ EXCELLENT: Cache is working optimally!" -ForegroundColor Green
    } elseif ($Efficiency -gt 50) {
        Write-Host "⚠️  GOOD: Cache is working but has overhead" -ForegroundColor Yellow
    } else {
        Write-Host "❌ POOR: Cache may not be hitting or has high overhead" -ForegroundColor Red
    }

} catch {
    Write-Host "❌ Error during performance test: $($_.Exception.Message)" -ForegroundColor Red
}

# Check cache file access time (should be updated if cache hit)
$CacheAfter = Get-Item $CacheFilePath
Write-Host "`nCache file LastAccess: $($CacheAfter.LastAccessTime)"
Write-Host "Cache file LastWrite: $($CacheAfter.LastWriteTime)"

# Cleanup
Write-Host "`nCleaning up..." -ForegroundColor Yellow
$Process | Stop-Process -Force
Start-Sleep -Seconds 2

if (Test-Path $OriginalPath) {
    Rename-Item $OriginalPath $TestDir
}

Write-Host "✅ Test completed!" -ForegroundColor Green