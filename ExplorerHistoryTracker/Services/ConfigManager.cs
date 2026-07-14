using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ExplorerHistoryTracker;
using ExplorerHistoryTracker.Models;

namespace ExplorerHistoryTracker.Services
{
    public class AppConfig
    {
        public double WindowWidth { get; set; } = 380;
        public double WindowHeight { get; set; } = 550;
        public bool IsBackgroundMonitorEnabled { get; set; } = true;
        public bool IsTopmost { get; set; } = false;
        public string ThemeMode { get; set; } = "System";
        public List<FolderHistoryItem> FolderHistory { get; set; } = new();
        public List<FolderHistoryItem> AppAndFileHistory { get; set; } = new();
        public string LastActiveTab { get; set; } = "Recent";
        public string LastActiveFilter { get; set; } = "All";
    }

    public class ConfigManager
    {
        private readonly string _storageDir;
        private readonly string _storagePath;
        private readonly object _lock = new();
        private const int MaxHistoryItems = 300;

        public AppConfig Config { get; private set; }

        public ConfigManager()
        {
            _storageDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Tuna"
            );
            _storagePath = Path.Combine(_storageDir, "history_config.json");
            Config = LoadConfig();
        }

        public AppConfig LoadConfig()
        {
            lock (_lock)
            {
                try
                {
                    // Compatibility: Check old history.json path first
                    string oldPath = Path.Combine(_storageDir, "history.json");
                    if (File.Exists(oldPath) && !File.Exists(_storagePath))
                    {
                        try
                        {
                            string oldJson = File.ReadAllText(oldPath);
                            var oldItems = JsonSerializer.Deserialize(oldJson, JsonContext.Default.ListFolderHistoryItem);
                            if (oldItems != null)
                            {
                                var migratedConfig = new AppConfig
                                {
                                    FolderHistory = oldItems
                                };
                                SaveConfig(migratedConfig);
                                File.Delete(oldPath); // Clean up old file
                                return migratedConfig;
                            }
                        }
                        catch { }
                    }

                    if (!File.Exists(_storagePath))
                    {
                        return new AppConfig();
                    }

                    string json = File.ReadAllText(_storagePath);
                    var loaded = JsonSerializer.Deserialize(json, JsonContext.Default.AppConfig);
                    return loaded ?? new AppConfig();
                }
                catch
                {
                    return new AppConfig();
                }
            }
        }

        public void Save()
        {
            SaveConfig(Config);
        }

        private void SaveConfig(AppConfig config)
        {
            lock (_lock)
            {
                try
                {
                    if (!Directory.Exists(_storageDir))
                    {
                        Directory.CreateDirectory(_storageDir);
                    }

                    string json = JsonSerializer.Serialize(config, JsonContext.Default.AppConfig);
                    File.WriteAllText(_storagePath, json);
                }
                catch { }
            }
        }

        public FolderHistoryItem? AddOrUpdateFolder(string path)
        {
            var item = AddOrUpdateItem(path, Config.FolderHistory);
            Save();
            return item;
        }

        public FolderHistoryItem? AddOrUpdateAppOrFile(string path)
        {
            var item = AddOrUpdateItem(path, Config.AppAndFileHistory);
            Save();
            return item;
        }

        private FolderHistoryItem? AddOrUpdateItem(string path, List<FolderHistoryItem> list)
        {
            if (string.IsNullOrEmpty(path)) return null;

            path = path.Trim();
            if (Directory.Exists(path))
            {
                path = path.TrimEnd('\\');
                if (path.EndsWith(":")) path += "\\";
            }

            // Filter out system and temp files
            if (path.StartsWith("::") || 
                path.Contains(@"\AppData\Local\Temp", StringComparison.OrdinalIgnoreCase) ||
                path.Contains(@"\Windows\System32", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var item = list.FirstOrDefault(i => i.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (item != null)
            {
                item.LastVisited = DateTime.Now;
                item.VisitCount++;
            }
            else
            {
                string name = Path.GetFileName(path);
                if (string.IsNullOrEmpty(name))
                {
                    name = path;
                }
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    name = Path.GetFileNameWithoutExtension(name);
                }

                item = new FolderHistoryItem
                {
                    Path = path,
                    Name = name,
                    LastVisited = DateTime.Now,
                    VisitCount = 1,
                    IsPinned = false
                };
                list.Add(item);
            }

            // Enforce max limit for unpinned items
            var pinned = list.Where(i => i.IsPinned).ToList();
            var unpinned = list.Where(i => !i.IsPinned)
                               .OrderByDescending(i => i.LastVisited)
                               .ToList();

            if (unpinned.Count > MaxHistoryItems)
            {
                var keep = unpinned.Take(MaxHistoryItems).ToList();
                list.Clear();
                list.AddRange(pinned);
                list.AddRange(keep);
            }

            return item;
        }
    }
}
