@echo off
echo ======================================
echo    CacheMax 高性能缓存加速器 v3.0
echo           系统安装程序
echo ======================================
echo.

REM 检查管理员权限
net session >nul 2>&1
if %errorLevel% == 0 (
    echo [✓] 已获得管理员权限
) else (
    echo [✗] 需要管理员权限运行此脚本
    echo 请右键点击此脚本，选择"以管理员身份运行"
    echo.
    pause
    exit /b 1
)

echo.
echo 安装信息：
echo 源目录: %~dp0CacheMax.GUI\bin\Release\net8.0-windows
echo 目标目录: C:\Program Files\CacheMax
echo.

REM 检查源文件是否存在
if not exist "%~dp0CacheMax.GUI\bin\Release\net8.0-windows\CacheMax.exe" (
    echo [✗] 错误：找不到Release版本的可执行文件
    echo 请先编译Release版本：dotnet build --configuration Release
    echo.
    pause
    exit /b 1
)

echo [*] 正在检查现有安装...

REM 如果目标目录已存在，先停止可能运行的进程
if exist "C:\Program Files\CacheMax\CacheMax.exe" (
    echo [*] 检测到已安装版本，正在停止运行中的进程...
    taskkill /f /im CacheMax.exe >nul 2>&1
    timeout /t 2 /nobreak >nul
)

REM 创建目标目录
echo [*] 创建安装目录...
if not exist "C:\Program Files\CacheMax" (
    mkdir "C:\Program Files\CacheMax"
)

REM 复制必要的运行文件（不包括配置文件）
echo [*] 复制程序文件...

REM 复制主要可执行文件和DLL
echo     正在复制 CacheMax.exe...
copy "%~dp0CacheMax.GUI\bin\Release\net8.0-windows\CacheMax.exe" "C:\Program Files\CacheMax\"
if %errorlevel% neq 0 (
    echo [✗] 错误：CacheMax.exe 复制失败
    pause
    exit /b 1
)

echo     正在复制 CacheMax.dll...
copy "%~dp0CacheMax.GUI\bin\Release\net8.0-windows\CacheMax.dll" "C:\Program Files\CacheMax\"
if %errorlevel% neq 0 (
    echo [✗] 错误：CacheMax.dll 复制失败
    pause
    exit /b 1
)

echo     正在复制依赖配置文件...
copy "%~dp0CacheMax.GUI\bin\Release\net8.0-windows\CacheMax.deps.json" "C:\Program Files\CacheMax\"
copy "%~dp0CacheMax.GUI\bin\Release\net8.0-windows\CacheMax.runtimeconfig.json" "C:\Program Files\CacheMax\"

REM 复制图标文件
if exist "%~dp0CacheMax.GUI\bin\Release\net8.0-windows\CacheMax.ico" (
    echo     正在复制程序图标...
    copy "%~dp0CacheMax.GUI\bin\Release\net8.0-windows\CacheMax.ico" "C:\Program Files\CacheMax\"
) else (
    echo [!] 警告：未找到程序图标文件
)

REM 复制第三方依赖
if exist "%~dp0CacheMax.GUI\bin\Release\net8.0-windows\Newtonsoft.Json.dll" (
    echo     正在复制第三方库...
    copy "%~dp0CacheMax.GUI\bin\Release\net8.0-windows\Newtonsoft.Json.dll" "C:\Program Files\CacheMax\"
) else (
    echo [!] 警告：未找到 Newtonsoft.Json.dll
)

REM 检查关键文件是否复制成功
if not exist "C:\Program Files\CacheMax\CacheMax.exe" (
    echo [✗] 错误：主程序文件复制失败
    pause
    exit /b 1
)

echo [✓] 程序文件复制完成

REM 创建开始菜单快捷方式
echo [*] 创建开始菜单快捷方式...
powershell -Command "$WshShell = New-Object -comObject WScript.Shell; $Shortcut = $WshShell.CreateShortcut('%ProgramData%\Microsoft\Windows\Start Menu\Programs\CacheMax.lnk'); $Shortcut.TargetPath = 'C:\Program Files\CacheMax\CacheMax.exe'; $Shortcut.WorkingDirectory = 'C:\Program Files\CacheMax'; $Shortcut.IconLocation = 'C:\Program Files\CacheMax\CacheMax.exe'; $Shortcut.Description = 'CacheMax 高性能文件系统加速器'; $Shortcut.Save()" >nul 2>&1

REM 创建桌面快捷方式（可选）
set /p desktop_shortcut="是否创建桌面快捷方式？(Y/N): "
if /i "%desktop_shortcut%"=="Y" (
    echo [*] 创建桌面快捷方式...
    powershell -Command "$WshShell = New-Object -comObject WScript.Shell; $Shortcut = $WshShell.CreateShortcut('%USERPROFILE%\Desktop\CacheMax.lnk'); $Shortcut.TargetPath = 'C:\Program Files\CacheMax\CacheMax.exe'; $Shortcut.WorkingDirectory = 'C:\Program Files\CacheMax'; $Shortcut.IconLocation = 'C:\Program Files\CacheMax\CacheMax.exe'; $Shortcut.Description = 'CacheMax 高性能文件系统加速器'; $Shortcut.Save()" >nul 2>&1
)

REM 注册到系统PATH（可选）
set /p add_to_path="是否添加到系统PATH环境变量？(Y/N): "
if /i "%add_to_path%"=="Y" (
    echo [*] 添加到系统PATH...
    powershell -Command "$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine'); if ($env:Path -notlike '*C:\Program Files\CacheMax*') { [Environment]::SetEnvironmentVariable('Path', $env:Path + ';C:\Program Files\CacheMax', 'Machine') }" >nul 2>&1
)

echo.
echo ======================================
echo           安装完成！
echo ======================================
echo.
echo 安装位置: C:\Program Files\CacheMax
echo 程序大小:
for %%I in ("C:\Program Files\CacheMax\CacheMax.exe") do echo     主程序: %%~zI 字节
echo.
echo 启动方式:
echo 1. 开始菜单 -> CacheMax
if /i "%desktop_shortcut%"=="Y" echo 2. 双击桌面快捷方式
if /i "%add_to_path%"=="Y" echo 3. 命令行输入: CacheMax
echo 4. 直接运行: C:\Program Files\CacheMax\CacheMax.exe
echo.
echo 重要提示:
echo - Release版本只允许单实例运行
echo - 首次运行请设置缓存根目录
echo - 建议使用SSD作为缓存设备
echo.

set /p start_now="是否立即启动 CacheMax？(Y/N): "
if /i "%start_now%"=="Y" (
    echo [*] 启动 CacheMax...
    start "" "C:\Program Files\CacheMax\CacheMax.exe"
)

echo.
echo 安装脚本执行完毕！
pause