using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CacheMax.GUI.Services
{
    /// <summary>
    /// 智能缓存大小更新器
    /// 实现防抖 + 定时更新机制，在不影响性能的前提下保持缓存大小接近实时
    /// </summary>
    public class CacheSizeUpdater : IDisposable
    {
        // 配置参数
        private const int DEBOUNCE_DELAY_MS = 8000;        // 防抖延迟：8秒
        private const int MAX_DEBOUNCE_DELAY_MS = 45000;   // 最大防抖延迟：45秒（强制更新）
        private const int PERIODIC_UPDATE_MS = 45000;      // 定时更新间隔：45秒

        private readonly string _cachePath;
        private readonly Action<long> _onSizeUpdated;

        private Timer? _debounceTimer;                     // 防抖计时器
        private Timer? _periodicTimer;                     // 定时更新计时器

        private DateTime _lastTriggerTime;                 // 最后一次触发时间（用于强制更新判断）
        private DateTime _lastUpdateTime;                  // 最后一次实际更新时间

        private int _isCalculating = 0;                    // 防重入标志（0=空闲，1=计算中）
        private bool _disposed = false;

        /// <summary>
        /// 创建缓存大小更新器
        /// </summary>
        /// <param name="cachePath">缓存目录路径</param>
        /// <param name="onSizeUpdated">大小更新时的回调（参数为新的大小）</param>
        public CacheSizeUpdater(string cachePath, Action<long> onSizeUpdated)
        {
            _cachePath = cachePath ?? throw new ArgumentNullException(nameof(cachePath));
            _onSizeUpdated = onSizeUpdated ?? throw new ArgumentNullException(nameof(onSizeUpdated));

            _lastTriggerTime = DateTime.Now;
            _lastUpdateTime = DateTime.Now;

            // 启动定时更新计时器（每45秒触发一次）
            _periodicTimer = new Timer(
                PeriodicUpdateCallback,
                null,
                PERIODIC_UPDATE_MS,
                PERIODIC_UPDATE_MS);
        }

        /// <summary>
        /// 通知文件发生变化（触发防抖更新）
        /// </summary>
        public void NotifyFileChange()
        {
            if (_disposed) return;

            var now = DateTime.Now;
            var timeSinceLastTrigger = (now - _lastTriggerTime).TotalMilliseconds;

            // 检查是否需要强制更新（持续变化超过45秒）
            if (timeSinceLastTrigger >= MAX_DEBOUNCE_DELAY_MS)
            {
                // 强制立即更新
                _lastTriggerTime = now;
                TriggerUpdate("强制更新（持续变化超过45秒）");
                return;
            }

            // 更新最后触发时间
            _lastTriggerTime = now;

            // 重置防抖计时器（8秒后触发）
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(
                DebounceCallback,
                null,
                DEBOUNCE_DELAY_MS,
                Timeout.Infinite);
        }

        /// <summary>
        /// 立即更新（跳过防抖）
        /// </summary>
        public void UpdateNow(string reason = "手动触发")
        {
            if (_disposed) return;
            TriggerUpdate(reason);
        }

        /// <summary>
        /// 防抖回调（8秒静默后触发）
        /// </summary>
        private void DebounceCallback(object? state)
        {
            TriggerUpdate("防抖更新（8秒无变化）");
        }

        /// <summary>
        /// 定时更新回调（每45秒触发）
        /// </summary>
        private void PeriodicUpdateCallback(object? state)
        {
            TriggerUpdate("定时更新（每45秒）");
        }

        /// <summary>
        /// 触发更新操作
        /// </summary>
        private void TriggerUpdate(string reason)
        {
            if (_disposed) return;

            // 防重入：如果正在计算，跳过本次更新
            if (Interlocked.CompareExchange(ref _isCalculating, 1, 0) != 0)
            {
                // 已经在计算中，跳过
                return;
            }

            // 在后台低优先级线程执行计算
            Task.Run(() =>
            {
                try
                {
                    var size = CalculateCacheSize();
                    _lastUpdateTime = DateTime.Now;

                    // 回调通知更新
                    _onSizeUpdated?.Invoke(size);
                }
                catch (Exception)
                {
                    // 忽略计算错误，不影响主流程
                }
                finally
                {
                    // 释放计算标志
                    Interlocked.Exchange(ref _isCalculating, 0);
                }
            }, CancellationToken.None);
        }

        /// <summary>
        /// 计算缓存目录大小
        /// </summary>
        private long CalculateCacheSize()
        {
            try
            {
                if (!Directory.Exists(_cachePath))
                    return 0;

                long totalSize = 0;
                var dirInfo = new DirectoryInfo(_cachePath);

                // 使用EnumerateFiles避免一次性加载所有文件
                foreach (var file in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    try
                    {
                        totalSize += file.Length;
                    }
                    catch
                    {
                        // 单个文件访问失败不影响整体计算
                    }
                }

                return totalSize;
            }
            catch
            {
                return 0;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _debounceTimer?.Dispose();
            _periodicTimer?.Dispose();

            _debounceTimer = null;
            _periodicTimer = null;
        }
    }
}
