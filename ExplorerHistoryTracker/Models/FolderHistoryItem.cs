using System;
using ExplorerHistoryTracker.ViewModels;

namespace ExplorerHistoryTracker.Models
{
    /// <summary>
    /// Represents a folder history record. Inherits from ViewModelBase to support live UI updates for individual card actions.
    /// </summary>
    public class FolderHistoryItem : ViewModelBase
    {
        private string _path = string.Empty;
        private string _name = string.Empty;
        private DateTime _lastVisited;
        private int _visitCount;
        private bool _isPinned;

        public string Path
        {
            get => _path;
            set => SetProperty(ref _path, value);
        }

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        public DateTime LastVisited
        {
            get => _lastVisited;
            set => SetProperty(ref _lastVisited, value);
        }

        public int VisitCount
        {
            get => _visitCount;
            set => SetProperty(ref _visitCount, value);
        }

        public bool IsPinned
        {
            get => _isPinned;
            set => SetProperty(ref _isPinned, value);
        }

        [System.Text.Json.Serialization.JsonIgnore]
        public Avalonia.Media.Imaging.Bitmap? Icon
        {
            get
            {
                // Use IconCache (LRU, max 64 entries) instead of caching per-item.
                // This prevents unbounded bitmap memory growth — once scrolled out of view,
                // the least-recently-used icon is evicted and disposed automatically.
                return Services.IconCache.GetOrCreate(Path, p => Services.IconHelper.GetIcon(p));
            }
        }

        [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern uint AssocQueryString(
            uint flags,
            uint str,
            string pszAssoc,
            string? pszExtra,
            [System.Runtime.InteropServices.Out] System.Text.StringBuilder? pszOut,
            ref uint pcchOut
        );

        private const uint ASSOCF_INIT_DEFAULTTOSTAR = 0x00000100;
        private const uint ASSOCSTR_EXECUTABLE = 2;

        [System.Text.Json.Serialization.JsonIgnore]
        public string AppName
        {
            get
            {
                try
                {
                    if (System.IO.Directory.Exists(Path))
                    {
                        return "explorer.exe";
                    }
                    
                    string ext = System.IO.Path.GetExtension(Path);
                    if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        return System.IO.Path.GetFileName(Path).ToLower();
                    }
                    
                    if (!string.IsNullOrEmpty(ext))
                    {
                        uint cch = 0;
                        AssocQueryString(ASSOCF_INIT_DEFAULTTOSTAR, ASSOCSTR_EXECUTABLE, ext, null, null, ref cch);
                        if (cch > 0)
                        {
                            var sb = new System.Text.StringBuilder((int)cch);
                            uint hr = AssocQueryString(ASSOCF_INIT_DEFAULTTOSTAR, ASSOCSTR_EXECUTABLE, ext, null, sb, ref cch);
                            if (hr == 0) // S_OK
                            {
                                string exePath = sb.ToString();
                                return System.IO.Path.GetFileName(exePath).ToLower();
                            }
                        }
                    }
                }
                catch { }
                return "explorer.exe";
            }
        }
    }
}
