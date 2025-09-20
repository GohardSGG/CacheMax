# CacheMax 自动化性能测试脚本
# 用于客观测量和优化4K随机读写性能

param(
    [string]$TestDir = "A:\Test",
    [string]$CacheDir = "S:\Cache",
    [int]$TestSizeMB = 32,
    [int]$Iterations = 5
)

Write-Host "=== CacheMax 自动化性能测试 (4K随机读写) ===" -ForegroundColor Green
Write-Host "测试目录: $TestDir"
Write-Host "缓存目录: $CacheDir"
Write-Host "测试文件大小: $TestSizeMB MB"
Write-Host "测试模式: 4K随机读取"
Write-Host "测试迭代次数: $Iterations"
Write-Host ""

# 路径定义
$PassthroughExe = "C:\Code\CacheMax\CacheMax.FileSystem\Release\passthrough-mod.exe"
$TestFile = "benchmark_test.dat"
$OriginalPath = "$TestDir.original"
$CacheTestDir = Join-Path $CacheDir "Test"

# 确保目录存在
if (!(Test-Path $TestDir)) {
    New-Item -ItemType Directory -Path $TestDir -Force | Out-Null
}
if (!(Test-Path $CacheDir)) {
    New-Item -ItemType Directory -Path $CacheDir -Force | Out-Null
}

# 创建测试文件
Write-Host "创建 $TestSizeMB MB 测试文件..." -ForegroundColor Yellow
$TestFilePath = Join-Path $TestDir $TestFile
$Data = [byte[]]::new(1MB)
$Random = [System.Random]::new()
$Random.NextBytes($Data)

$Stream = [System.IO.File]::OpenWrite($TestFilePath)
try {
    for ($i = 0; $i -lt $TestSizeMB; $i++) {
        $Stream.Write($Data, 0, $Data.Length)
        if ($i % 10 -eq 0) {
            Write-Progress -Activity "创建测试文件" -PercentComplete (($i / $TestSizeMB) * 100)
        }
    }
} finally {
    $Stream.Close()
}
Write-Progress -Activity "创建测试文件" -Completed

# 编译 CacheMax
Write-Host "编译 CacheMax..." -ForegroundColor Yellow
$BuildResult = & "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" "C:\Code\CacheMax\CacheMax.FileSystem\CacheMax.FileSystem.vcxproj" -p:Configuration=Release -p:Platform=x64 -v:quiet
if ($LASTEXITCODE -ne 0) {
    Write-Host "编译失败!" -ForegroundColor Red
    exit 1
}

# 4K随机性能测试函数 (5秒持续测试)
function Test-4KRandomPerformance {
    param([string]$Path, [string]$Description)

    Write-Host "测试 $Description..." -ForegroundColor Cyan

    $Results = @()

    for ($iter = 1; $iter -le $Iterations; $iter++) {
        Write-Host "  迭代 $iter/$Iterations (5秒测试)..."

        # 使用内置.NET方法进行4K随机读取测试
        $FilePath = Join-Path $Path $TestFile

        if (!(Test-Path $FilePath)) {
            Write-Host "  警告: 测试文件不存在 $FilePath" -ForegroundColor Yellow
            continue
        }

        # 5秒持续4K随机读取测试
        $FileStream = [System.IO.File]::OpenRead($FilePath)
        $Buffer = [byte[]]::new(4096)
        $Random = [System.Random]::new()
        $FileSize = $FileStream.Length

        $StartTime = Get-Date
        $Operations = 0

        try {
            # 持续5秒进行4K随机读取
            do {
                $Offset = [long]($Random.NextDouble() * ($FileSize - 4096))
                $Offset = $Offset - ($Offset % 4096)  # 4K对齐
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

        Write-Host "    用时: $([math]::Round($Duration, 2)) 秒, 操作数: $Operations, 速度: $([math]::Round($Speed, 2)) MB/s"

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

    Write-Host "  结果: 平均 $([math]::Round($AvgSpeed, 2)) MB/s (范围: $([math]::Round($MinSpeed, 2)) - $([math]::Round($MaxSpeed, 2)) MB/s), 总操作: $TotalOps" -ForegroundColor Green

    return $AvgSpeed
}

# 停止所有现有的passthrough进程
Write-Host "停止现有进程..." -ForegroundColor Yellow
Get-Process -Name "passthrough-mod" -ErrorAction SilentlyContinue | Stop-Process -Force

# 1. 原始磁盘性能测试
Write-Host "`n=== 测试1: 原始磁盘性能 (A盘) ===" -ForegroundColor Magenta
$OriginalDiskSpeed = Test-4KRandomPerformance -Path $TestDir -Description "原始磁盘直接访问"

# 2. 缓存盘直接性能测试
Write-Host "`n=== 测试2: 缓存盘直接性能 (S盘DAX) ===" -ForegroundColor Magenta

# 复制测试文件到缓存目录
if (!(Test-Path $CacheTestDir)) {
    New-Item -ItemType Directory -Path $CacheTestDir -Force | Out-Null
}
Copy-Item $TestFilePath (Join-Path $CacheTestDir $TestFile) -Force

$CacheDiskSpeed = Test-4KRandomPerformance -Path $CacheTestDir -Description "缓存盘DAX直接访问"

# 3. CacheMax真实使用场景测试
Write-Host "`n=== 测试3: CacheMax真实场景 (A:\Test → S:\Cache\Test) ===" -ForegroundColor Magenta

# 模拟GUI操作：重命名原始目录，启动WinFsp挂载
Write-Host "模拟CacheMax GUI操作..." -ForegroundColor Yellow

# 备份并重命名原始目录
if (Test-Path $OriginalPath) {
    Remove-Item $OriginalPath -Recurse -Force
}
Rename-Item $TestDir $OriginalPath

# 启动 CacheMax (真实场景：原始路径 → 缓存路径 → 挂载点)
$Arguments = "-p `"$OriginalPath`" -c `"$CacheDir`" -m `"$TestDir`""
Write-Host "启动命令: passthrough-mod.exe $Arguments" -ForegroundColor Cyan

$Process = Start-Process -FilePath $PassthroughExe -ArgumentList $Arguments -PassThru -WindowStyle Minimized
Start-Sleep -Seconds 3

if ($Process.HasExited) {
    Write-Host "❌ CacheMax 启动失败!" -ForegroundColor Red
    Write-Host "错误代码: $($Process.ExitCode)" -ForegroundColor Red

    # 恢复目录
    if (Test-Path $OriginalPath) {
        Rename-Item $OriginalPath $TestDir
    }
    exit 1
}

# 等待WinFsp挂载生效
Write-Host "等待WinFsp挂载生效..." -ForegroundColor Yellow
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
    Write-Host "❌ 挂载超时!" -ForegroundColor Red
    $Process | Stop-Process -Force

    # 恢复目录
    if (Test-Path $OriginalPath) {
        Rename-Item $OriginalPath $TestDir
    }
    exit 1
}

Write-Host "✅ CacheMax挂载成功，开始性能测试..." -ForegroundColor Green

# 执行CacheMax性能测试 (访问A:\Test实际读取缓存)
$CacheMaxSpeed = Test-4KRandomPerformance -Path $TestDir -Description "CacheMax加速访问"

# 清理
Write-Host "`n清理环境..." -ForegroundColor Yellow
$Process | Stop-Process -Force
Start-Sleep -Seconds 2

# 恢复目录结构
if (Test-Path $OriginalPath) {
    Rename-Item $OriginalPath $TestDir
}

# 结果分析
Write-Host "`n=== 性能分析结果 ===" -ForegroundColor Green
Write-Host "1. 原始磁盘性能 (A盘): $([math]::Round($OriginalDiskSpeed, 2)) MB/s"
Write-Host "2. 缓存盘直接性能 (S盘DAX): $([math]::Round($CacheDiskSpeed, 2)) MB/s"
Write-Host "3. CacheMax挂载性能: $([math]::Round($CacheMaxSpeed, 2)) MB/s"

# 计算加速比
$CacheVsOriginal = $CacheDiskSpeed / $OriginalDiskSpeed
$CacheMaxVsOriginal = $CacheMaxSpeed / $OriginalDiskSpeed
$CacheMaxVsCache = $CacheMaxSpeed / $CacheDiskSpeed

Write-Host ""
Write-Host "=== 性能对比分析 ===" -ForegroundColor Cyan
Write-Host "缓存盘 vs 原始盘: $([math]::Round($CacheVsOriginal, 2))x 加速"
Write-Host "CacheMax vs 原始盘: $([math]::Round($CacheMaxVsOriginal, 2))x 加速"
Write-Host "CacheMax vs 缓存盘: $([math]::Round($CacheMaxVsCache * 100, 1))% 效率"

$EfficiencyLoss = (1 - $CacheMaxVsCache) * 100

Write-Host ""
if ($CacheMaxVsCache -gt 0.8) {
    Write-Host "✅ 优秀: CacheMax达到缓存盘直接访问的 $([math]::Round($CacheMaxVsCache * 100, 1))%" -ForegroundColor Green
} elseif ($CacheMaxVsCache -gt 0.5) {
    Write-Host "⚠️  一般: CacheMax达到缓存盘直接访问的 $([math]::Round($CacheMaxVsCache * 100, 1))%" -ForegroundColor Yellow
} else {
    Write-Host "❌ 差: CacheMax只达到缓存盘直接访问的 $([math]::Round($CacheMaxVsCache * 100, 1))%" -ForegroundColor Red
}

Write-Host "用户态开销: $([math]::Round($EfficiencyLoss, 1))%"

# 输出优化建议
Write-Host "`n=== 优化建议 ===" -ForegroundColor Cyan
if ($CacheMaxVsCache -lt 0.8) {
    Write-Host "• 减少用户态开销 (当前损失 $([math]::Round($EfficiencyLoss, 1))%)"
    Write-Host "• 优化内存映射逻辑"
    Write-Host "• 减少文件系统调用开销"
    Write-Host "• 优化4K随机访问模式"
    Write-Host "• 考虑直接内存映射而不是文件系统层"
}

# 清理测试文件
Write-Host "`n清理测试环境..." -ForegroundColor Yellow
if (Test-Path $CacheTestDir) {
    Remove-Item $CacheTestDir -Recurse -Force
}

# 确保完全恢复原状
Write-Host "确保文件系统完全恢复原状..." -ForegroundColor Yellow
if (Test-Path $OriginalPath) {
    Write-Host "警告: 检测到未恢复的原始目录，正在恢复..." -ForegroundColor Red
    if (Test-Path $TestDir) {
        Remove-Item $TestDir -Recurse -Force
    }
    Rename-Item $OriginalPath $TestDir
    Write-Host "已恢复原始目录结构" -ForegroundColor Green
}

# 验证恢复状态
if ((Test-Path $TestDir) -and !(Test-Path $OriginalPath)) {
    Write-Host "✅ 文件系统状态已完全恢复" -ForegroundColor Green
} else {
    Write-Host "❌ 警告: 文件系统状态可能未完全恢复!" -ForegroundColor Red
}

# 保存结果到CSV
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

Write-Host "`n📊 测试完成! 结果已保存到: $ResultFile" -ForegroundColor Green
Write-Host "🎯 优化目标: 让CacheMax效率超过80% (当前: $([math]::Round($CacheMaxVsCache * 100, 1))%)" -ForegroundColor Cyan