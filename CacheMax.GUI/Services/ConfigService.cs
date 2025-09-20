using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace CacheMax.GUI.Services
{
    public class AcceleratedFolder
    {
        public string OriginalPath { get; set; } = string.Empty;
        public string CachePath { get; set; } = string.Empty;
        public string MountPoint { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public long CacheSize { get; set; }
    }

    public class AppConfig
    {
        public string DefaultCacheRoot { get; set; } = @"S:\Cache";
        public List<AcceleratedFolder> AcceleratedFolders { get; set; } = new();
        public bool AutoStartWithWindows { get; set; }
        public bool MinimizeToTray { get; set; }
    }

    public class ConfigService
    {
        private readonly string _configPath;
        private AppConfig _config = new();

        public AppConfig Config => _config;

        public ConfigService()
        {
            _configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CacheMax",
                "config.json"
            );
            LoadConfig();
        }

        private void LoadConfig()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    var json = File.ReadAllText(_configPath);
                    _config = JsonConvert.DeserializeObject<AppConfig>(json) ?? new AppConfig();
                }
                else
                {
                    _config = new AppConfig();
                    SaveConfig();
                }
            }
            catch
            {
                _config = new AppConfig();
            }
        }

        public void SaveConfig()
        {
            try
            {
                var directory = Path.GetDirectoryName(_configPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonConvert.SerializeObject(_config, Formatting.Indented);
                File.WriteAllText(_configPath, json);
            }
            catch (Exception ex)
            {
                // Log error or show message
                Console.WriteLine($"Failed to save config: {ex.Message}");
            }
        }

        public void AddAcceleratedFolder(AcceleratedFolder folder)
        {
            _config.AcceleratedFolders.Add(folder);
            SaveConfig();
        }

        public void RemoveAcceleratedFolder(string mountPoint)
        {
            _config.AcceleratedFolders.RemoveAll(f => f.MountPoint == mountPoint);
            SaveConfig();
        }

        public AcceleratedFolder? GetAcceleratedFolder(string mountPoint)
        {
            return _config.AcceleratedFolders.Find(f => f.MountPoint == mountPoint);
        }
    }
}