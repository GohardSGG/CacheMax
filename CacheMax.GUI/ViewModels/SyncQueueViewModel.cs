using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CacheMax.GUI.ViewModels
{
    /// <summary>
    /// 同步队列项目视图模型
    /// </summary>
    public class SyncQueueItemViewModel : INotifyPropertyChanged
    {
        private string _status = "等待中";
        private string _fileName = "";
        private string _filePath = "";
        private long _fileSize;
        private string _fileSizeFormatted = "";
        private double _progress;
        private string _progressText = "0%";
        private DateTime _createdAt;
        private DateTime? _completedAt;
        private string? _errorMessage;

        public string Status
        {
            get => _status;
            set
            {
                if (_status != value)
                {
                    _status = value;
                    OnPropertyChanged();
                }
            }
        }

        public string FileName
        {
            get => _fileName;
            set
            {
                if (_fileName != value)
                {
                    _fileName = value;
                    OnPropertyChanged();
                }
            }
        }

        public string FilePath
        {
            get => _filePath;
            set
            {
                if (_filePath != value)
                {
                    _filePath = value;
                    OnPropertyChanged();
                    FileName = System.IO.Path.GetFileName(value);
                }
            }
        }

        public long FileSize
        {
            get => _fileSize;
            set
            {
                if (_fileSize != value)
                {
                    _fileSize = value;
                    OnPropertyChanged();
                    FileSizeFormatted = FormatFileSize(value);
                }
            }
        }

        public string FileSizeFormatted
        {
            get => _fileSizeFormatted;
            private set
            {
                if (_fileSizeFormatted != value)
                {
                    _fileSizeFormatted = value;
                    OnPropertyChanged();
                }
            }
        }

        public double Progress
        {
            get => _progress;
            set
            {
                if (Math.Abs(_progress - value) > 0.001)
                {
                    _progress = value;
                    OnPropertyChanged();
                    ProgressText = $"{value:F0}%";
                }
            }
        }

        public string ProgressText
        {
            get => _progressText;
            private set
            {
                if (_progressText != value)
                {
                    _progressText = value;
                    OnPropertyChanged();
                }
            }
        }

        public DateTime CreatedAt
        {
            get => _createdAt;
            set
            {
                if (_createdAt != value)
                {
                    _createdAt = value;
                    OnPropertyChanged();
                }
            }
        }

        public DateTime? CompletedAt
        {
            get => _completedAt;
            set
            {
                if (_completedAt != value)
                {
                    _completedAt = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CompletedAtFormatted));
                }
            }
        }

        public string CompletedAtFormatted
        {
            get => _completedAt?.ToString("HH:mm:ss") ?? "";
        }

        public string? ErrorMessage
        {
            get => _errorMessage;
            set
            {
                if (_errorMessage != value)
                {
                    _errorMessage = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024)
                return $"{bytes} B";
            if (bytes < 1024 * 1024)
                return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024)
                return $"{bytes / (1024.0 * 1024):F1} MB";

            return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
        }
    }

    /// <summary>
    /// 同步队列统计信息
    /// </summary>
    public class SyncQueueStats
    {
        public int TotalCount { get; set; }
        public int PendingCount { get; set; }
        public int ProcessingCount { get; set; }
        public int CompletedCount { get; set; }
        public int FailedCount { get; set; }
        public double TotalSizeMB { get; set; }
        public double ProcessedSizeMB { get; set; }
        public double OverallProgress => TotalSizeMB > 0 ? (ProcessedSizeMB / TotalSizeMB) * 100 : 0;
    }

    /// <summary>
    /// 同步队列事件参数
    /// </summary>
    public class SyncQueueEventArgs : EventArgs
    {
        public SyncQueueItemViewModel Item { get; }
        public string Action { get; }

        public SyncQueueEventArgs(SyncQueueItemViewModel item, string action)
        {
            Item = item;
            Action = action;
        }
    }
}