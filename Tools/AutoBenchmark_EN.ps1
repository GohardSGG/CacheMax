# CacheMax Automated Performance Test Script
# Objective measurement and optimization for 4K random read/write performance

param(
    [string]$TestDir = "A:\Test",
    [string]$CacheDir = "S:\Cache",
    [int]$TestSizeMB = 32,
    [int]$Iterations = 5
)

Write-Host "=== CacheMax Automated Performance Test (4K Random Read/Write) ===" -ForegroundColor Green
Write-Host "Test Directory: $TestDir"
Write-Host "Cache Directory: $CacheDir"
Write-Host "Test File Size: $TestSizeMB MB"
Write-Host "Test Mode: 4K Random Read"
Write-Host "Test Iterations: $Iterations"
Write-Host ""

# Path definitions
$PassthroughExe = "C:\Code\CacheMax\CacheMax.FileSystem\Release\passthrough-mod.exe"
$TestFile = "benchmark_test.dat"
$OriginalPath = "$TestDir.original"
$CacheTestDir = Join-Path $CacheDir "Test"

# Ensure directories exist
if (!(Test-Path $TestDir)) {
    New-Item -ItemType Directory -Path $TestDir -Force | Out-Null
}
if (!(Test-Path $CacheDir)) {
    New-Item -ItemType Directory -Path $CacheDir -Force | Out-Null
}

# Create test file
Write-Host "Creating $TestSizeMB MB test file..." -ForegroundColor Yellow
$TestFilePath = Join-Path $TestDir $TestFile
$Data = [byte[]]::new(1MB)
$Random = [System.Random]::new()
$Random.NextBytes($Data)

$Stream = [System.IO.File]::OpenWrite($TestFilePath)
try {
    for ($i = 0; $i -lt $TestSizeMB; $i++) {
        $Stream.Write($Data, 0, $Data.Length)
        if ($i % 10 -eq 0) {
            Write-Progress -Activity "Creating test file" -PercentComplete (($i / $TestSizeMB) * 100)
        }
    }
} finally {
    $Stream.Close()
}
Write-Progress -Activity "Creating test file" -Completed

# Build CacheMax
Write-Host "Building CacheMax..." -ForegroundColor Yellow
$BuildResult = & "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" "C:\Code\CacheMax\CacheMax.FileSystem\CacheMax.FileSystem.vcxproj" -p:Configuration=Release -p:Platform=x64 -v:quiet
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

# 4K Random Performance Test Function (5-second continuous test)
function Test-4KRandomPerformance {
    param([string]$Path, [string]$Description)

    Write-Host "Testing $Description..." -ForegroundColor Cyan

    $Results = @()

    for ($iter = 1; $iter -le $Iterations; $iter++) {
        Write-Host "  Iteration $iter/$Iterations (5-second test)..."

        # Use built-in .NET methods for 4K random read test
        $FilePath = Join-Path $Path $TestFile

        if (!(Test-Path $FilePath)) {
            Write-Host "  Warning: Test file not found $FilePath" -ForegroundColor Yellow
            continue
        }

        # 5-second continuous 4K random read test
        $FileStream = [System.IO.File]::OpenRead($FilePath)
        $Buffer = [byte[]]::new(4096)
        $Random = [System.Random]::new()
        $FileSize = $FileStream.Length

        $StartTime = Get-Date
        $Operations = 0

        try {
            # Continuous 5-second 4K random read
            do {
                $Offset = [long]($Random.NextDouble() * ($FileSize - 4096))
                $Offset = $Offset - ($Offset % 4096)  # 4K alignment
                $FileStream.Seek($Offset, [System.IO.SeekOrigin]::Begin) | Out-Null
                $FileStream.Read($Buffer, 0, 4096) | Out-Null
                $Operations++

                $CurrentTime = Get-Date
                $Elapsed = ($CurrentTime - $StartTime).TotalSeconds
            } while ($Elapsed -lt 5.0)

        } finally {
            $FileStream.Close()
        }

        $EndTime = Get-Date
        $Duration = ($EndTime - $StartTime).TotalSeconds
        $TotalMB = ($Operations * 4096) / 1MB
        $Speed = $TotalMB / $Duration

        Write-Host "    Duration: $([math]::Round($Duration, 2)) seconds, Operations: $Operations, Speed: $([math]::Round($Speed, 2)) MB/s"

        $Results += [PSCustomObject]@{
            Iteration = $iter
            Duration = $Duration
            Operations = $Operations
            Speed = $Speed
        }
    }

    $AvgSpeed = ($Results | Measure-Object -Property Speed -Average).Average
    $MinSpeed = ($Results | Measure-Object -Property Speed -Minimum).Minimum
    $MaxSpeed = ($Results | Measure-Object -Property Speed -Maximum).Maximum
    $TotalOps = ($Results | Measure-Object -Property Operations -Sum).Sum

    Write-Host "  Result: Average $([math]::Round($AvgSpeed, 2)) MB/s (Range: $([math]::Round($MinSpeed, 2)) - $([math]::Round($MaxSpeed, 2)) MB/s), Total Ops: $TotalOps" -ForegroundColor Green

    return $AvgSpeed
}

# Stop all existing passthrough processes
Write-Host "Stopping existing processes..." -ForegroundColor Yellow
Get-Process -Name "passthrough-mod" -ErrorAction SilentlyContinue | Stop-Process -Force

# 1. Original disk performance test
Write-Host "`n=== Test 1: Original Disk Performance (A: drive) ===" -ForegroundColor Magenta
$OriginalDiskSpeed = Test-4KRandomPerformance -Path $TestDir -Description "Original disk direct access"

# 2. Cache disk direct performance test
Write-Host "`n=== Test 2: Cache Disk Direct Performance (S: drive DAX) ===" -ForegroundColor Magenta

# Copy test file to cache directory
if (!(Test-Path $CacheTestDir)) {
    New-Item -ItemType Directory -Path $CacheTestDir -Force | Out-Null
}
Copy-Item $TestFilePath (Join-Path $CacheTestDir $TestFile) -Force

$CacheDiskSpeed = Test-4KRandomPerformance -Path $CacheTestDir -Description "Cache disk DAX direct access"

# 3. CacheMax real usage scenario test
Write-Host "`n=== Test 3: CacheMax Real Scenario (A:\Test -> S:\Cache\Test) ===" -ForegroundColor Magenta

# Simulate GUI operation: Rename original directory, start WinFsp mount
Write-Host "Simulating CacheMax GUI operation..." -ForegroundColor Yellow

# Backup and rename original directory
if (Test-Path $OriginalPath) {
    Remove-Item $OriginalPath -Recurse -Force
}
Rename-Item $TestDir $OriginalPath

# Start CacheMax (real scenario: original path -> cache path -> mount point)
$Arguments = "-p `"$OriginalPath`" -c `"$CacheDir`" -m `"$TestDir`""
Write-Host "Start command: passthrough-mod.exe $Arguments" -ForegroundColor Cyan

$Process = Start-Process -FilePath $PassthroughExe -ArgumentList $Arguments -PassThru -WindowStyle Minimized
Start-Sleep -Seconds 3

if ($Process.HasExited) {
    Write-Host "CacheMax startup failed!" -ForegroundColor Red
    Write-Host "Error code: $($Process.ExitCode)" -ForegroundColor Red

    # Restore directory
    if (Test-Path $OriginalPath) {
        Rename-Item $OriginalPath $TestDir
    }
    exit 1
}

# Wait for WinFsp mount to take effect
Write-Host "Waiting for WinFsp mount to take effect..." -ForegroundColor Yellow
$MountReady = $false
for ($i = 0; $i -lt 30; $i++) {
    if (Test-Path (Join-Path $TestDir $TestFile)) {
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

    # Restore directory
    if (Test-Path $OriginalPath) {
        Rename-Item $OriginalPath $TestDir
    }
    exit 1
}

Write-Host "CacheMax mount successful, starting performance test..." -ForegroundColor Green

# Execute CacheMax performance test (accessing A:\Test actually reads from cache)
$CacheMaxSpeed = Test-4KRandomPerformance -Path $TestDir -Description "CacheMax accelerated access"

# Cleanup
Write-Host "`nCleaning up environment..." -ForegroundColor Yellow
$Process | Stop-Process -Force
Start-Sleep -Seconds 2

# Restore directory structure
if (Test-Path $OriginalPath) {
    Rename-Item $OriginalPath $TestDir
}

# Performance analysis results
Write-Host "`n=== Performance Analysis Results ===" -ForegroundColor Green
Write-Host "1. Original disk performance (A: drive): $([math]::Round($OriginalDiskSpeed, 2)) MB/s"
Write-Host "2. Cache disk direct performance (S: drive DAX): $([math]::Round($CacheDiskSpeed, 2)) MB/s"
Write-Host "3. CacheMax mount performance: $([math]::Round($CacheMaxSpeed, 2)) MB/s"

# Calculate speedup ratios
$CacheVsOriginal = $CacheDiskSpeed / $OriginalDiskSpeed
$CacheMaxVsOriginal = $CacheMaxSpeed / $OriginalDiskSpeed
$CacheMaxVsCache = $CacheMaxSpeed / $CacheDiskSpeed

Write-Host ""
Write-Host "=== Performance Comparison Analysis ===" -ForegroundColor Cyan
Write-Host "Cache disk vs Original disk: $([math]::Round($CacheVsOriginal, 2))x speedup"
Write-Host "CacheMax vs Original disk: $([math]::Round($CacheMaxVsOriginal, 2))x speedup"
Write-Host "CacheMax vs Cache disk: $([math]::Round($CacheMaxVsCache * 100, 1))% efficiency"

$EfficiencyLoss = (1 - $CacheMaxVsCache) * 100

Write-Host ""
if ($CacheMaxVsCache -gt 0.8) {
    Write-Host "EXCELLENT: CacheMax achieved $([math]::Round($CacheMaxVsCache * 100, 1))% of direct cache disk access" -ForegroundColor Green
} elseif ($CacheMaxVsCache -gt 0.5) {
    Write-Host "GOOD: CacheMax achieved $([math]::Round($CacheMaxVsCache * 100, 1))% of direct cache disk access" -ForegroundColor Yellow
} else {
    Write-Host "NEEDS WORK: CacheMax only achieved $([math]::Round($CacheMaxVsCache * 100, 1))% of direct cache disk access" -ForegroundColor Red
}

Write-Host "User-mode overhead: $([math]::Round($EfficiencyLoss, 1))%"

# Output optimization suggestions
Write-Host "`n=== Optimization Suggestions ===" -ForegroundColor Cyan
if ($CacheMaxVsCache -lt 0.8) {
    Write-Host "• Reduce user-mode overhead (current loss $([math]::Round($EfficiencyLoss, 1))%)"
    Write-Host "• Optimize memory mapping logic"
    Write-Host "• Reduce file system call overhead"
    Write-Host "• Optimize 4K random access patterns"
    Write-Host "• Consider direct memory mapping instead of file system layer"
}

# Clean up test files
Write-Host "`nCleaning up test environment..." -ForegroundColor Yellow
if (Test-Path $CacheTestDir) {
    Remove-Item $CacheTestDir -Recurse -Force
}

# Ensure complete restoration to original state
Write-Host "Ensuring file system completely restored to original state..." -ForegroundColor Yellow
if (Test-Path $OriginalPath) {
    Write-Host "Warning: Detected unrestored original directory, restoring..." -ForegroundColor Red
    if (Test-Path $TestDir) {
        Remove-Item $TestDir -Recurse -Force
    }
    Rename-Item $OriginalPath $TestDir
    Write-Host "Original directory structure restored" -ForegroundColor Green
}

# Verify restoration status
if ((Test-Path $TestDir) -and !(Test-Path $OriginalPath)) {
    Write-Host "File system state completely restored" -ForegroundColor Green
} else {
    Write-Host "WARNING: File system state may not be completely restored!" -ForegroundColor Red
}

# Save results to CSV
$ResultFile = "C:\Code\CacheMax\benchmark_results.csv"
$Result = [PSCustomObject]@{
    Timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    OriginalDiskSpeed = $OriginalDiskSpeed
    CacheDiskSpeed = $CacheDiskSpeed
    CacheMaxSpeed = $CacheMaxSpeed
    CacheVsOriginal = $CacheVsOriginal
    CacheMaxVsOriginal = $CacheMaxVsOriginal
    CacheMaxVsCache = $CacheMaxVsCache
    EfficiencyLoss = $EfficiencyLoss
}

if (Test-Path $ResultFile) {
    $Result | Export-Csv $ResultFile -Append -NoTypeInformation
} else {
    $Result | Export-Csv $ResultFile -NoTypeInformation
}

Write-Host "`nTest completed! Results saved to: $ResultFile" -ForegroundColor Green
Write-Host "Optimization target: Make CacheMax efficiency exceed 80% (current: $([math]::Round($CacheMaxVsCache * 100, 1))%)" -ForegroundColor Cyan