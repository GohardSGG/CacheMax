using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CacheMax.GUI.Services
{
    public class ErrorRecoveryService
    {
        private readonly Dictionary<string, AccelerationState> _stateHistory = new();
        private readonly Dictionary<string, Timer> _recoveryTimers = new();
        private readonly object _lockObject = new();

        public event EventHandler<RecoveryEventArgs>? RecoveryStarted;
        public event EventHandler<RecoveryEventArgs>? RecoveryCompleted;
        public event EventHandler<RecoveryEventArgs>? RecoveryFailed;
        public event EventHandler<string>? LogMessage;

        public class AccelerationState
        {
            public string MountPoint { get; set; } = string.Empty;
            public string OriginalPath { get; set; } = string.Empty;
            public string CachePath { get; set; } = string.Empty;
            public DateTime CreatedAt { get; set; }
            public bool IsActive { get; set; }
            public List<ErrorRecord> Errors { get; set; } = new();
            public int RecoveryAttempts { get; set; }
            public DateTime LastRecoveryAttempt { get; set; }
        }

        public class ErrorRecord
        {
            public DateTime Timestamp { get; set; } = DateTime.Now;
            public string ErrorType { get; set; } = string.Empty;
            public string Message { get; set; } = string.Empty;
            public string? StackTrace { get; set; }
            public ErrorSeverity Severity { get; set; } = ErrorSeverity.Medium;
        }

        public class RecoveryEventArgs : EventArgs
        {
            public string MountPoint { get; set; } = string.Empty;
            public string Action { get; set; } = string.Empty;
            public bool Success { get; set; }
            public string? Message { get; set; }
            public int AttemptNumber { get; set; }
        }

        public enum ErrorSeverity
        {
            Low,     // 轻微错误，不影响基本功能
            Medium,  // 中等错误，可能影响性能
            High,    // 严重错误，影响核心功能
            Critical // 关键错误，系统无法正常工作
        }

        public enum RecoveryStrategy
        {
            None,           // 不进行恢复
            Retry,          // 简单重试
            Reset,          // 重置连接
            Recreate,       // 重新创建
            Fallback        // 回退到安全状态
        }

        /// <summary>
        /// 记录加速状态
        /// </summary>
        public void RecordAccelerationState(string mountPoint, string originalPath, string cachePath, bool isActive)
        {
            lock (_lockObject)
            {
                _stateHistory[mountPoint] = new AccelerationState
                {
                    MountPoint = mountPoint,
                    OriginalPath = originalPath,
                    CachePath = cachePath,
                    CreatedAt = DateTime.Now,
                    IsActive = isActive
                };

                LogMessage?.Invoke(this, $"记录加速状态：{mountPoint} -> {(isActive ? "激活" : "停用")}");
            }
        }

        /// <summary>
        /// 记录错误
        /// </summary>
        public void RecordError(string mountPoint, string errorType, string message, Exception? exception = null, ErrorSeverity severity = ErrorSeverity.Medium)
        {
            lock (_lockObject)
            {
                if (!_stateHistory.TryGetValue(mountPoint, out var state))
                {
                    LogMessage?.Invoke(this, $"警告：尝试记录未知挂载点的错误：{mountPoint}");
                    return;
                }

                var error = new ErrorRecord
                {
                    ErrorType = errorType,
                    Message = message,
                    StackTrace = exception?.StackTrace,
                    Severity = severity
                };

                state.Errors.Add(error);

                LogMessage?.Invoke(this, $"记录错误：{mountPoint} - {errorType}: {message} (严重程度：{severity})");

                // 根据错误严重程度决定是否启动自动恢复
                if (severity >= ErrorSeverity.High && state.IsActive)
                {
                    var strategy = DetermineRecoveryStrategy(state);
                    if (strategy != RecoveryStrategy.None)
                    {
                        ScheduleRecovery(mountPoint, strategy);
                    }
                }
            }
        }

        /// <summary>
        /// 手动触发恢复
        /// </summary>
        public async Task<bool> TriggerRecovery(string mountPoint, CacheManagerService cacheManager, IProgress<string>? progress = null)
        {
            AccelerationState? state = null;
            RecoveryStrategy strategy;

            lock (_lockObject)
            {
                if (!_stateHistory.TryGetValue(mountPoint, out state))
                {
                    progress?.Report($"错误：未找到挂载点状态：{mountPoint}");
                    return false;
                }

                strategy = DetermineRecoveryStrategy(state);
            }

            return await ExecuteRecovery(mountPoint, strategy, cacheManager, progress);
        }

        /// <summary>
        /// 检查系统状态并执行预防性恢复
        /// </summary>
        public async Task<bool> PerformHealthCheck(CacheManagerService cacheManager, IProgress<string>? progress = null)
        {
            progress?.Report("开始系统健康检查...");
            var recoveryPerformed = false;
            var statesToCheck = new List<(string mountPoint, AccelerationState state)>();
            var totalErrors = 0;
            var checkedCount = 0;

            lock (_lockObject)
            {
                foreach (var kvp in _stateHistory)
                {
                    var mountPoint = kvp.Key;
                    var state = kvp.Value;

                    if (state.IsActive)
                    {
                        statesToCheck.Add((mountPoint, state));
                    }
                }
            }

            progress?.Report($"发现 {statesToCheck.Count} 个活跃的加速项目需要检查");

            if (statesToCheck.Count == 0)
            {
                progress?.Report("ℹ️ 信息：没有找到任何活跃的加速项目");
                progress?.Report("这是正常的，说明当前没有配置文件夹加速");
                LogMessage?.Invoke(this, "健康检查：没有活跃的加速项目");
                return false; // 没有项目不算问题
            }

            foreach (var (mountPoint, state) in statesToCheck)
            {
                checkedCount++;
                progress?.Report($"检查项目 {checkedCount}/{statesToCheck.Count}: {mountPoint}");

                try
                {
                    var issues = new List<string>();

                    // 详细检查1：挂载点是否存在
                    if (!Directory.Exists(mountPoint))
                    {
                        issues.Add($"挂载点目录不存在: {mountPoint}");
                    }

                    // 详细检查2：原始目录是否存在
                    if (!Directory.Exists(state.OriginalPath))
                    {
                        issues.Add($"原始目录不存在: {state.OriginalPath}");
                    }

                    // 详细检查3：缓存目录是否存在
                    if (!Directory.Exists(state.CachePath))
                    {
                        issues.Add($"缓存目录不存在: {state.CachePath}");
                    }

                    // 详细检查4：符号链接验证
                    bool validationResult = false;
                    try
                    {
                        validationResult = cacheManager.ValidateAcceleration(mountPoint, state.OriginalPath, state.CachePath, progress);
                        if (!validationResult)
                        {
                            issues.Add("符号链接验证失败");
                        }
                    }
                    catch (Exception validateEx)
                    {
                        issues.Add($"符号链接验证异常: {validateEx.Message}");
                    }

                    // 详细检查5：检查错误历史
                    var recentErrors = state.Errors.FindAll(e => e.Timestamp > DateTime.Now.AddHours(-1));
                    if (recentErrors.Count > 0)
                    {
                        issues.Add($"最近1小时内有 {recentErrors.Count} 个错误");
                    }

                    if (issues.Count > 0)
                    {
                        totalErrors++;
                        progress?.Report($"❌ {mountPoint} 发现 {issues.Count} 个问题:");
                        foreach (var issue in issues)
                        {
                            progress?.Report($"   - {issue}");
                            LogMessage?.Invoke(this, $"健康检查问题: {mountPoint} - {issue}");
                        }

                        RecordError(mountPoint, "HealthCheck", string.Join("; ", issues), null, ErrorSeverity.High);

                        // 询问是否触发恢复
                        progress?.Report($"正在为 {mountPoint} 触发自动恢复...");
                        try
                        {
                            await TriggerRecovery(mountPoint, cacheManager, progress);
                            recoveryPerformed = true;
                            progress?.Report($"✅ {mountPoint} 恢复操作已触发");
                        }
                        catch (Exception recoverEx)
                        {
                            progress?.Report($"❌ {mountPoint} 恢复失败: {recoverEx.Message}");
                            LogMessage?.Invoke(this, $"恢复失败: {mountPoint} - {recoverEx.Message}");
                        }
                    }
                    else
                    {
                        progress?.Report($"✅ {mountPoint} 健康状态良好");
                        LogMessage?.Invoke(this, $"健康检查通过: {mountPoint}");
                    }
                }
                catch (Exception ex)
                {
                    totalErrors++;
                    progress?.Report($"❌ {mountPoint} 检查时发生异常: {ex.Message}");
                    LogMessage?.Invoke(this, $"健康检查异常：{mountPoint} - {ex.Message}");
                    RecordError(mountPoint, "HealthCheckException", ex.Message, ex, ErrorSeverity.High);
                }
            }

            // 输出总结
            progress?.Report($"=== 健康检查完成 ===");
            progress?.Report($"检查项目数: {checkedCount}");
            progress?.Report($"发现问题数: {totalErrors}");
            progress?.Report($"执行恢复: {(recoveryPerformed ? "是" : "否")}");

            if (totalErrors > 0)
            {
                LogMessage?.Invoke(this, $"健康检查完成: 发现 {totalErrors} 个问题");
            }
            else
            {
                LogMessage?.Invoke(this, "健康检查完成: 所有项目状态良好");
            }

            return totalErrors > 0;
        }

        private RecoveryStrategy DetermineRecoveryStrategy(AccelerationState state)
        {
            var recentErrors = state.Errors.FindAll(e => e.Timestamp > DateTime.Now.AddMinutes(-30));
            var highSeverityErrors = recentErrors.FindAll(e => e.Severity >= ErrorSeverity.High);

            // 如果最近30分钟内有多个高严重性错误，使用更激进的恢复策略
            if (highSeverityErrors.Count >= 3)
            {
                return RecoveryStrategy.Recreate;
            }

            if (recentErrors.Count >= 5)
            {
                return RecoveryStrategy.Reset;
            }

            if (state.RecoveryAttempts < 3 && (DateTime.Now - state.LastRecoveryAttempt).TotalMinutes > 5)
            {
                return RecoveryStrategy.Retry;
            }

            // 如果重试次数过多，回退到安全状态
            if (state.RecoveryAttempts >= 5)
            {
                return RecoveryStrategy.Fallback;
            }

            return RecoveryStrategy.None;
        }

        private void ScheduleRecovery(string mountPoint, RecoveryStrategy strategy)
        {
            // 取消现有的恢复计时器
            if (_recoveryTimers.TryGetValue(mountPoint, out var existingTimer))
            {
                existingTimer.Dispose();
            }

            // 计算延迟时间（根据恢复策略和之前的尝试次数）
            var state = _stateHistory[mountPoint];
            var delayMinutes = Math.Min(Math.Pow(2, state.RecoveryAttempts), 30); // 指数退避，最大30分钟

            var timer = new Timer(_ =>
            {
                try
                {
                    // 记录定时恢复触发，但实际恢复需要通过手动调用
                    LogMessage?.Invoke(this, $"定时恢复触发：{mountPoint} (策略：{strategy})");
                    LogMessage?.Invoke(this, $"建议：请手动执行恢复操作以修复问题");
                }
                catch (Exception ex)
                {
                    LogMessage?.Invoke(this, $"定时恢复异常：{mountPoint} - {ex.Message}");
                }
            }, null, TimeSpan.FromMinutes(delayMinutes), Timeout.InfiniteTimeSpan);

            _recoveryTimers[mountPoint] = timer;

            LogMessage?.Invoke(this, $"安排恢复：{mountPoint} (策略：{strategy}，延迟：{delayMinutes}分钟)");
        }

        private async Task<bool> ExecuteRecovery(string mountPoint, RecoveryStrategy strategy, CacheManagerService cacheManager, IProgress<string>? progress)
        {
            var state = _stateHistory[mountPoint];
            state.RecoveryAttempts++;
            state.LastRecoveryAttempt = DateTime.Now;

            var eventArgs = new RecoveryEventArgs
            {
                MountPoint = mountPoint,
                Action = strategy.ToString(),
                AttemptNumber = state.RecoveryAttempts
            };

            RecoveryStarted?.Invoke(this, eventArgs);
            progress?.Report($"开始恢复：{mountPoint} (策略：{strategy}，第{state.RecoveryAttempts}次尝试)");

            try
            {
                bool success = strategy switch
                {
                    RecoveryStrategy.Retry => await RetryOperation(state, cacheManager, progress),
                    RecoveryStrategy.Reset => await ResetAcceleration(state, cacheManager, progress),
                    RecoveryStrategy.Recreate => await RecreateAcceleration(state, cacheManager, progress),
                    RecoveryStrategy.Fallback => await FallbackToSafeState(state, cacheManager, progress),
                    _ => false
                };

                eventArgs.Success = success;
                eventArgs.Message = success ? "恢复成功" : "恢复失败";

                if (success)
                {
                    RecoveryCompleted?.Invoke(this, eventArgs);
                    LogMessage?.Invoke(this, $"恢复成功：{mountPoint} (策略：{strategy})");

                    // 重置错误计数
                    state.Errors.Clear();
                    state.RecoveryAttempts = 0;
                }
                else
                {
                    RecoveryFailed?.Invoke(this, eventArgs);
                    LogMessage?.Invoke(this, $"恢复失败：{mountPoint} (策略：{strategy})");
                }

                return success;
            }
            catch (Exception ex)
            {
                eventArgs.Success = false;
                eventArgs.Message = ex.Message;
                RecoveryFailed?.Invoke(this, eventArgs);

                LogMessage?.Invoke(this, $"恢复异常：{mountPoint} - {ex.Message}");
                RecordError(mountPoint, "RecoveryException", ex.Message, ex, ErrorSeverity.High);
                return false;
            }
        }

        private async Task<bool> RetryOperation(AccelerationState state, CacheManagerService cacheManager, IProgress<string>? progress)
        {
            progress?.Report("执行重试恢复...");

            // 简单验证当前状态
            if (cacheManager.ValidateAcceleration(state.MountPoint, state.OriginalPath, state.CachePath, progress))
            {
                progress?.Report("验证通过，无需重试");
                return true;
            }

            // 尝试强制同步
            return await cacheManager.SyncToOriginal(state.CachePath, progress);
        }

        private async Task<bool> ResetAcceleration(AccelerationState state, CacheManagerService cacheManager, IProgress<string>? progress)
        {
            progress?.Report("执行重置恢复...");

            try
            {
                // 停止当前加速
                await cacheManager.StopCacheAcceleration(state.MountPoint, state.OriginalPath, state.CachePath, false, progress);

                // 等待一下
                await Task.Delay(2000);

                // 重新启动加速
                return await cacheManager.InitializeCacheAcceleration(
                    state.OriginalPath.Replace(".original", ""), // 移除.original后缀获得原始路径
                    Path.GetDirectoryName(state.CachePath) ?? "",
                    SyncMode.Batch,
                    3,
                    progress);
            }
            catch (Exception ex)
            {
                progress?.Report($"重置恢复失败：{ex.Message}");
                return false;
            }
        }

        private async Task<bool> RecreateAcceleration(AccelerationState state, CacheManagerService cacheManager, IProgress<string>? progress)
        {
            progress?.Report("执行重新创建恢复...");

            try
            {
                // 完全停止并删除缓存
                await cacheManager.StopCacheAcceleration(state.MountPoint, state.OriginalPath, state.CachePath, true, progress);

                // 等待文件系统稳定
                await Task.Delay(5000);

                // 重新创建加速
                return await cacheManager.InitializeCacheAcceleration(
                    state.OriginalPath.Replace(".original", ""),
                    Path.GetDirectoryName(state.CachePath) ?? "",
                    SyncMode.Batch,
                    3,
                    progress);
            }
            catch (Exception ex)
            {
                progress?.Report($"重新创建恢复失败：{ex.Message}");
                return false;
            }
        }

        private async Task<bool> FallbackToSafeState(AccelerationState state, CacheManagerService cacheManager, IProgress<string>? progress)
        {
            progress?.Report("执行回退到安全状态...");

            try
            {
                // 停止加速并恢复原始状态
                var success = await cacheManager.StopCacheAcceleration(state.MountPoint, state.OriginalPath, state.CachePath, false, progress);

                if (success)
                {
                    state.IsActive = false;
                    progress?.Report("已安全停止加速，回退到原始状态");
                }

                return success;
            }
            catch (Exception ex)
            {
                progress?.Report($"回退失败：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 获取错误统计
        /// </summary>
        public Dictionary<string, object> GetErrorStatistics()
        {
            var stats = new Dictionary<string, object>();

            lock (_lockObject)
            {
                var totalErrors = 0;
                var totalRecoveryAttempts = 0;
                var activeAccelerations = 0;

                foreach (var state in _stateHistory.Values)
                {
                    totalErrors += state.Errors.Count;
                    totalRecoveryAttempts += state.RecoveryAttempts;
                    if (state.IsActive) activeAccelerations++;
                }

                stats["TotalErrors"] = totalErrors;
                stats["TotalRecoveryAttempts"] = totalRecoveryAttempts;
                stats["ActiveAccelerations"] = activeAccelerations;
                stats["TotalAccelerations"] = _stateHistory.Count;
            }

            return stats;
        }

        public void Dispose()
        {
            lock (_lockObject)
            {
                foreach (var timer in _recoveryTimers.Values)
                {
                    timer.Dispose();
                }
                _recoveryTimers.Clear();
            }
        }
    }
}