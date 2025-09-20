# CacheMax 鑷姩鍖栨€ц兘娴嬭瘯鑴氭湰
# 鐢ㄤ簬瀹㈣娴嬮噺鍜屼紭鍖?K闅忔満璇诲啓鎬ц兘

param(
    [string]$TestDir = "A:\Test",
    [string]$CacheDir = "S:\Cache",
    [int]$TestSizeMB = 32,
    [int]$Iterations = 5
)

Write-Host "=== CacheMax 鑷姩鍖栨€ц兘娴嬭瘯 (4K闅忔満璇诲啓) ===" -ForegroundColor Green
Write-Host "娴嬭瘯鐩綍: $TestDir"
Write-Host "缂撳瓨鐩綍: $CacheDir"
Write-Host "娴嬭瘯鏂囦欢澶у皬: $TestSizeMB MB"
Write-Host "娴嬭瘯妯″紡: 4K闅忔満璇诲彇"
Write-Host "娴嬭瘯杩唬娆℃暟: $Iterations"
Write-Host ""

# 璺緞瀹氫箟
$PassthroughExe = "C:\Code\CacheMax\CacheMax.FileSystem\Release\passthrough-mod.exe"
$TestFile = "benchmark_test.dat"
$OriginalPath = "$TestDir.original"
$CacheTestDir = Join-Path $CacheDir "Test"

# 纭繚鐩綍瀛樺湪
if (!(Test-Path $TestDir)) {
    New-Item -ItemType Directory -Path $TestDir -Force | Out-Null
}
if (!(Test-Path $CacheDir)) {
    New-Item -ItemType Directory -Path $CacheDir -Force | Out-Null
}

# 鍒涘缓娴嬭瘯鏂囦欢
Write-Host "鍒涘缓 $TestSizeMB MB 娴嬭瘯鏂囦欢..." -ForegroundColor Yellow
$TestFilePath = Join-Path $TestDir $TestFile
$Data = [byte[]]::new(1MB)
$Random = [System.Random]::new()
$Random.NextBytes($Data)

$Stream = [System.IO.File]::OpenWrite($TestFilePath)
try {
    for ($i = 0; $i -lt $TestSizeMB; $i++) {
        $Stream.Write($Data, 0, $Data.Length)
        if ($i % 10 -eq 0) {
            Write-Progress -Activity "鍒涘缓娴嬭瘯鏂囦欢" -PercentComplete (($i / $TestSizeMB) * 100)
        }
    }
} finally {
    $Stream.Close()
}
Write-Progress -Activity "鍒涘缓娴嬭瘯鏂囦欢" -Completed

# 缂栬瘧 CacheMax
Write-Host "缂栬瘧 CacheMax..." -ForegroundColor Yellow
$BuildResult = & "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" "C:\Code\CacheMax\CacheMax.FileSystem\CacheMax.FileSystem.vcxproj" -p:Configuration=Release -p:Platform=x64 -v:quiet
if ($LASTEXITCODE -ne 0) {
    Write-Host "缂栬瘧澶辫触!" -ForegroundColor Red
    exit 1
}

# 4K闅忔満鎬ц兘娴嬭瘯鍑芥暟 (5绉掓寔缁祴璇?
function Test-4KRandomPerformance {
    param([string]$Path, [string]$Description)

    Write-Host "娴嬭瘯 $Description..." -ForegroundColor Cyan

    $Results = @()

    for ($iter = 1; $iter -le $Iterations; $iter++) {
        Write-Host "  杩唬 $iter/$Iterations (5绉掓祴璇?..."

        # 浣跨敤鍐呯疆.NET鏂规硶杩涜4K闅忔満璇诲彇娴嬭瘯
        $FilePath = Join-Path $Path $TestFile

        if (!(Test-Path $FilePath)) {
            Write-Host "  璀﹀憡: 娴嬭瘯鏂囦欢涓嶅瓨鍦?$FilePath" -ForegroundColor Yellow
            continue
        }

        # 5绉掓寔缁?K闅忔満璇诲彇娴嬭瘯
        $FileStream = [System.IO.File]::OpenRead($FilePath)
        $Buffer = [byte[]]::new(4096)
        $Random = [System.Random]::new()
        $FileSize = $FileStream.Length

        $StartTime = Get-Date
        $Operations = 0

        try {
            # 鎸佺画5绉掕繘琛?K闅忔満璇诲彇
            do {
                $Offset = [long]($Random.NextDouble() * ($FileSize - 4096))
                $Offset = $Offset - ($Offset % 4096)  # 4K瀵归綈
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

        Write-Host "    鐢ㄦ椂: $([math]::Round($Duration, 2)) 绉? 鎿嶄綔鏁? $Operations, 閫熷害: $([math]::Round($Speed, 2)) MB/s"

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

    Write-Host "  缁撴灉: 骞冲潎 $([math]::Round($AvgSpeed, 2)) MB/s (鑼冨洿: $([math]::Round($MinSpeed, 2)) - $([math]::Round($MaxSpeed, 2)) MB/s), 鎬绘搷浣? $TotalOps" -ForegroundColor Green

    return $AvgSpeed
}

# 鍋滄鎵€鏈夌幇鏈夌殑passthrough杩涚▼
Write-Host "鍋滄鐜版湁杩涚▼..." -ForegroundColor Yellow
Get-Process -Name "passthrough-mod" -ErrorAction SilentlyContinue | Stop-Process -Force

# 1. 鍘熷纾佺洏鎬ц兘娴嬭瘯
Write-Host "`n=== 娴嬭瘯1: 鍘熷纾佺洏鎬ц兘 (A鐩? ===" -ForegroundColor Magenta
$OriginalDiskSpeed = Test-4KRandomPerformance -Path $TestDir -Description "鍘熷纾佺洏鐩存帴璁块棶"

# 2. 缂撳瓨鐩樼洿鎺ユ€ц兘娴嬭瘯
Write-Host "`n=== 娴嬭瘯2: 缂撳瓨鐩樼洿鎺ユ€ц兘 (S鐩楧AX) ===" -ForegroundColor Magenta

# 澶嶅埗娴嬭瘯鏂囦欢鍒扮紦瀛樼洰褰?
if (!(Test-Path $CacheTestDir)) {
    New-Item -ItemType Directory -Path $CacheTestDir -Force | Out-Null
}
Copy-Item $TestFilePath (Join-Path $CacheTestDir $TestFile) -Force

$CacheDiskSpeed = Test-4KRandomPerformance -Path $CacheTestDir -Description "缂撳瓨鐩楧AX鐩存帴璁块棶"

# 3. CacheMax鐪熷疄浣跨敤鍦烘櫙娴嬭瘯
Write-Host "`n=== 娴嬭瘯3: CacheMax鐪熷疄鍦烘櫙 (A:\Test 鈫?S:\Cache\Test) ===" -ForegroundColor Magenta

# 妯℃嫙GUI鎿嶄綔锛氶噸鍛藉悕鍘熷鐩綍锛屽惎鍔╓inFsp鎸傝浇
Write-Host "妯℃嫙CacheMax GUI鎿嶄綔..." -ForegroundColor Yellow

# 澶囦唤骞堕噸鍛藉悕鍘熷鐩綍
if (Test-Path $OriginalPath) {
    Remove-Item $OriginalPath -Recurse -Force
}
Rename-Item $TestDir $OriginalPath

# 鍚姩 CacheMax (鐪熷疄鍦烘櫙锛氬師濮嬭矾寰?鈫?缂撳瓨璺緞 鈫?鎸傝浇鐐?
$Arguments = "-p `"$OriginalPath`" -c `"$CacheDir`" -m `"$TestDir`""
Write-Host "鍚姩鍛戒护: passthrough-mod.exe $Arguments" -ForegroundColor Cyan

$Process = Start-Process -FilePath $PassthroughExe -ArgumentList $Arguments -PassThru -WindowStyle Minimized
Start-Sleep -Seconds 3

if ($Process.HasExited) {
    Write-Host "鉂?CacheMax 鍚姩澶辫触!" -ForegroundColor Red
    Write-Host "閿欒浠ｇ爜: $($Process.ExitCode)" -ForegroundColor Red

    # 鎭㈠鐩綍
    if (Test-Path $OriginalPath) {
        Rename-Item $OriginalPath $TestDir
    }
    exit 1
}

# 绛夊緟WinFsp鎸傝浇鐢熸晥
Write-Host "绛夊緟WinFsp鎸傝浇鐢熸晥..." -ForegroundColor Yellow
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
    Write-Host "鉂?鎸傝浇瓒呮椂!" -ForegroundColor Red
    $Process | Stop-Process -Force

    # 鎭㈠鐩綍
    if (Test-Path $OriginalPath) {
        Rename-Item $OriginalPath $TestDir
    }
    exit 1
}

Write-Host "鉁?CacheMax鎸傝浇鎴愬姛锛屽紑濮嬫€ц兘娴嬭瘯..." -ForegroundColor Green

# 鎵цCacheMax鎬ц兘娴嬭瘯 (璁块棶A:\Test瀹為檯璇诲彇缂撳瓨)
$CacheMaxSpeed = Test-4KRandomPerformance -Path $TestDir -Description "CacheMax鍔犻€熻闂?

# 娓呯悊
Write-Host "`n娓呯悊鐜..." -ForegroundColor Yellow
$Process | Stop-Process -Force
Start-Sleep -Seconds 2

# 鎭㈠鐩綍缁撴瀯
if (Test-Path $OriginalPath) {
    Rename-Item $OriginalPath $TestDir
}

# 缁撴灉鍒嗘瀽
Write-Host "`n=== 鎬ц兘鍒嗘瀽缁撴灉 ===" -ForegroundColor Green
Write-Host "1. 鍘熷纾佺洏鎬ц兘 (A鐩?: $([math]::Round($OriginalDiskSpeed, 2)) MB/s"
Write-Host "2. 缂撳瓨鐩樼洿鎺ユ€ц兘 (S鐩楧AX): $([math]::Round($CacheDiskSpeed, 2)) MB/s"
Write-Host "3. CacheMax鎸傝浇鎬ц兘: $([math]::Round($CacheMaxSpeed, 2)) MB/s"

# 璁＄畻鍔犻€熸瘮
$CacheVsOriginal = $CacheDiskSpeed / $OriginalDiskSpeed
$CacheMaxVsOriginal = $CacheMaxSpeed / $OriginalDiskSpeed
$CacheMaxVsCache = $CacheMaxSpeed / $CacheDiskSpeed

Write-Host ""
Write-Host "=== 鎬ц兘瀵规瘮鍒嗘瀽 ===" -ForegroundColor Cyan
Write-Host "缂撳瓨鐩?vs 鍘熷鐩? $([math]::Round($CacheVsOriginal, 2))x 鍔犻€?
Write-Host "CacheMax vs 鍘熷鐩? $([math]::Round($CacheMaxVsOriginal, 2))x 鍔犻€?
Write-Host "CacheMax vs 缂撳瓨鐩? $([math]::Round($CacheMaxVsCache * 100, 1))% 鏁堢巼"

$EfficiencyLoss = (1 - $CacheMaxVsCache) * 100

Write-Host ""
if ($CacheMaxVsCache -gt 0.8) {
    Write-Host "鉁?浼樼: CacheMax杈惧埌缂撳瓨鐩樼洿鎺ヨ闂殑 $([math]::Round($CacheMaxVsCache * 100, 1))%" -ForegroundColor Green
} elseif ($CacheMaxVsCache -gt 0.5) {
    Write-Host "鈿狅笍  涓€鑸? CacheMax杈惧埌缂撳瓨鐩樼洿鎺ヨ闂殑 $([math]::Round($CacheMaxVsCache * 100, 1))%" -ForegroundColor Yellow
} else {
    Write-Host "鉂?宸? CacheMax鍙揪鍒扮紦瀛樼洏鐩存帴璁块棶鐨?$([math]::Round($CacheMaxVsCache * 100, 1))%" -ForegroundColor Red
}

Write-Host "鐢ㄦ埛鎬佸紑閿€: $([math]::Round($EfficiencyLoss, 1))%"

# 杈撳嚭浼樺寲寤鸿
Write-Host "`n=== 浼樺寲寤鸿 ===" -ForegroundColor Cyan
if ($CacheMaxVsCache -lt 0.8) {
    Write-Host "鈥?鍑忓皯鐢ㄦ埛鎬佸紑閿€ (褰撳墠鎹熷け $([math]::Round($EfficiencyLoss, 1))%)"
    Write-Host "鈥?浼樺寲鍐呭瓨鏄犲皠閫昏緫"
    Write-Host "鈥?鍑忓皯鏂囦欢绯荤粺璋冪敤寮€閿€"
    Write-Host "鈥?浼樺寲4K闅忔満璁块棶妯″紡"
    Write-Host "鈥?鑰冭檻鐩存帴鍐呭瓨鏄犲皠鑰屼笉鏄枃浠剁郴缁熷眰"
}

# 娓呯悊娴嬭瘯鏂囦欢
Write-Host "`n娓呯悊娴嬭瘯鐜..." -ForegroundColor Yellow
if (Test-Path $CacheTestDir) {
    Remove-Item $CacheTestDir -Recurse -Force
}

# 纭繚瀹屽叏鎭㈠鍘熺姸
Write-Host "纭繚鏂囦欢绯荤粺瀹屽叏鎭㈠鍘熺姸..." -ForegroundColor Yellow
if (Test-Path $OriginalPath) {
    Write-Host "璀﹀憡: 妫€娴嬪埌鏈仮澶嶇殑鍘熷鐩綍锛屾鍦ㄦ仮澶?.." -ForegroundColor Red
    if (Test-Path $TestDir) {
        Remove-Item $TestDir -Recurse -Force
    }
    Rename-Item $OriginalPath $TestDir
    Write-Host "宸叉仮澶嶅師濮嬬洰褰曠粨鏋? -ForegroundColor Green
}

# 楠岃瘉鎭㈠鐘舵€?
if ((Test-Path $TestDir) -and !(Test-Path $OriginalPath)) {
    Write-Host "鉁?鏂囦欢绯荤粺鐘舵€佸凡瀹屽叏鎭㈠" -ForegroundColor Green
} else {
    Write-Host "鉂?璀﹀憡: 鏂囦欢绯荤粺鐘舵€佸彲鑳芥湭瀹屽叏鎭㈠!" -ForegroundColor Red
}

# 淇濆瓨缁撴灉鍒癈SV
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

Write-Host "`n馃搳 娴嬭瘯瀹屾垚! 缁撴灉宸蹭繚瀛樺埌: $ResultFile" -ForegroundColor Green
Write-Host "馃幆 浼樺寲鐩爣: 璁〤acheMax鏁堢巼瓒呰繃80% (褰撳墠: $([math]::Round($CacheMaxVsCache * 100, 1))%)" -ForegroundColor Cyan
