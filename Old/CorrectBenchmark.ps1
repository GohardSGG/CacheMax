# Correct CacheMax Performance Test
# Using the same methodology as dax_performance_test.c

param(
    [string]$TestDir = "A:\Test",
    [string]$CacheDir = "S:\Cache",
    [int]$TestSizeMB = 32,
    [int]$NumOperations = 1000  # Same as DAX test
)

Write-Host "=== CacheMax Correct Performance Test ===" -ForegroundColor Green
Write-Host "Using identical methodology to dax_performance_test.c"
Write-Host "Test File Size: $TestSizeMB MB"
Write-Host "Operations: $NumOperations (4K random read)"
Write-Host ""

$TestFile = "benchmark_test.dat"
$PassthroughExe = "C:\Code\CacheMax\CacheMax.FileSystem\Release\passthrough-mod.exe"
$OriginalPath = "$TestDir.original"
$CacheTestDir = Join-Path $CacheDir "Test"

# High-precision performance test function matching DAX test
function Test-HighPrecisionPerformance {
    param([string]$FilePath, [string]$Description)

    Write-Host "Testing $Description..." -ForegroundColor Cyan

    if (!(Test-Path $FilePath)) {
        Write-Host "  File not found: $FilePath" -ForegroundColor Red
        return 0
    }

    # Use .NET high-precision timer and file operations
    $BlockSize = 4096
    $TotalMB = ($NumOperations * $BlockSize) / 1MB

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

    try {
        $Speed = [HighPrecisionFileTest]::TestRandomRead($FilePath, $NumOperations, $BlockSize)
        Write-Host "  Result: $([math]::Round($Speed, 2)) MB/s" -ForegroundColor Green
        return $Speed
    }
    catch {
        Write-Host "  Error: $($_.Exception.Message)" -ForegroundColor Red
        return 0
    }
}

# Ensure test file exists
$TestFilePath = Join-Path $TestDir $TestFile
if (!(Test-Path $TestFilePath)) {
    Write-Host "Creating $TestSizeMB MB test file..." -ForegroundColor Yellow

    # Ensure directory exists
    if (!(Test-Path $TestDir)) {
        New-Item -ItemType Directory -Path $TestDir -Force | Out-Null
    }

    # Create test file quickly
    $Data = [byte[]]::new(1MB)
    $Random = [System.Random]::new()
    $Random.NextBytes($Data)

    $Stream = [System.IO.File]::OpenWrite($TestFilePath)
    try {
        for ($i = 0; $i -lt $TestSizeMB; $i++) {
            $Stream.Write($Data, 0, $Data.Length)
        }
    } finally {
        $Stream.Close()
    }
}

# Build CacheMax
Write-Host "Building CacheMax..." -ForegroundColor Yellow
$BuildResult = & "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" "C:\Code\CacheMax\CacheMax.FileSystem\CacheMax.FileSystem.vcxproj" -p:Configuration=Release -p:Platform=x64 -v:quiet
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

# Stop existing processes
Get-Process -Name "passthrough-mod" -ErrorAction SilentlyContinue | Stop-Process -Force

# Test 1: Original disk performance (A:)
Write-Host "`n=== Test 1: Original Disk Performance (A: drive) ===" -ForegroundColor Magenta
$OriginalSpeed = Test-HighPrecisionPerformance -FilePath $TestFilePath -Description "Original disk (A:)"

# Test 2: Cache disk performance (S:)
Write-Host "`n=== Test 2: Cache Disk Performance (S: drive DAX) ===" -ForegroundColor Magenta

# Copy test file to cache directory
if (!(Test-Path $CacheTestDir)) {
    New-Item -ItemType Directory -Path $CacheTestDir -Force | Out-Null
}
$CacheFilePath = Join-Path $CacheTestDir $TestFile
Copy-Item $TestFilePath $CacheFilePath -Force

$CacheSpeed = Test-HighPrecisionPerformance -FilePath $CacheFilePath -Description "Cache disk (S:)"

# Test 3: CacheMax performance
Write-Host "`n=== Test 3: CacheMax Performance (via WinFsp) ===" -ForegroundColor Magenta

# Prepare for CacheMax mount
if (Test-Path $OriginalPath) {
    Remove-Item $OriginalPath -Recurse -Force
}
Rename-Item $TestDir $OriginalPath

# Start CacheMax
$Arguments = "-p `"$OriginalPath`" -c `"$CacheDir`" -m `"$TestDir`""
Write-Host "Starting CacheMax: passthrough-mod.exe $Arguments" -ForegroundColor Cyan

$Process = Start-Process -FilePath $PassthroughExe -ArgumentList $Arguments -PassThru -WindowStyle Minimized
Start-Sleep -Seconds 3

if ($Process.HasExited) {
    Write-Host "CacheMax startup failed!" -ForegroundColor Red
    # Restore directory
    if (Test-Path $OriginalPath) {
        Rename-Item $OriginalPath $TestDir
    }
    exit 1
}

# Wait for mount
Write-Host "Waiting for WinFsp mount..." -ForegroundColor Yellow
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
    Write-Host "Mount timeout!" -ForegroundColor Red
    $Process | Stop-Process -Force
    if (Test-Path $OriginalPath) {
        Rename-Item $OriginalPath $TestDir
    }
    exit 1
}

Write-Host "CacheMax mounted successfully!" -ForegroundColor Green

# Test CacheMax performance
$CacheMaxSpeed = Test-HighPrecisionPerformance -FilePath $TestFilePath -Description "CacheMax (via WinFsp)"

# Cleanup
Write-Host "`nCleaning up..." -ForegroundColor Yellow
$Process | Stop-Process -Force
Start-Sleep -Seconds 2

# Restore directory
if (Test-Path $OriginalPath) {
    Rename-Item $OriginalPath $TestDir
}

# Analysis
Write-Host "`n=== Performance Analysis (Correct Method) ===" -ForegroundColor Green
Write-Host "1. Original disk (A:): $([math]::Round($OriginalSpeed, 2)) MB/s"
Write-Host "2. Cache disk (S:): $([math]::Round($CacheSpeed, 2)) MB/s"
Write-Host "3. CacheMax: $([math]::Round($CacheMaxSpeed, 2)) MB/s"

if ($OriginalSpeed -gt 0 -and $CacheSpeed -gt 0 -and $CacheMaxSpeed -gt 0) {
    $CacheVsOriginal = $CacheSpeed / $OriginalSpeed
    $CacheMaxVsOriginal = $CacheMaxSpeed / $OriginalSpeed
    $CacheMaxVsCache = $CacheMaxSpeed / $CacheSpeed

    Write-Host ""
    Write-Host "=== Performance Ratios ===" -ForegroundColor Cyan
    Write-Host "Cache vs Original: $([math]::Round($CacheVsOriginal, 2))x"
    Write-Host "CacheMax vs Original: $([math]::Round($CacheMaxVsOriginal, 2))x"
    Write-Host "CacheMax vs Cache: $([math]::Round($CacheMaxVsCache * 100, 1))%"

    Write-Host ""
    if ($CacheMaxVsCache -gt 0.8) {
        Write-Host "✅ EXCELLENT: CacheMax efficiency = $([math]::Round($CacheMaxVsCache * 100, 1))%" -ForegroundColor Green
    } elseif ($CacheMaxVsCache -gt 0.5) {
        Write-Host "⚠️  GOOD: CacheMax efficiency = $([math]::Round($CacheMaxVsCache * 100, 1))%" -ForegroundColor Yellow
    } else {
        Write-Host "❌ NEEDS WORK: CacheMax efficiency = $([math]::Round($CacheMaxVsCache * 100, 1))%" -ForegroundColor Red
    }

    Write-Host "Compare with DAX test results:"
    Write-Host "  - Expected A: drive speed: ~380 MB/s"
    Write-Host "  - Expected S: drive speed: ~1180 MB/s"
    Write-Host "  - Our results should be similar to achieve fair comparison"
}

# Keep cache file for performance analysis
# (Use CleanupTest.ps1 to clean up manually)
Write-Host "`nCache file preserved at: $CacheFilePath" -ForegroundColor Cyan

Write-Host "`nTest completed with correct methodology!" -ForegroundColor Green