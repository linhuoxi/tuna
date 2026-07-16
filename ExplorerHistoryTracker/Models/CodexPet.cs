using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Media;

namespace ExplorerHistoryTracker.Models
{
    public class CodexPet : INotifyPropertyChanged
    {
        private string _id = "";
        private string _displayName = "";
        private string _description = "";
        private int _spriteVersionNumber = 1;
        private string _spritesheetPath = "";
        private int _frameWidth = 192;
        private int _frameHeight = 208;
        private int _idleFrameCount = 6;
        private int _animationIntervalMs = 150;
        private string _folderPath = "";

        [JsonPropertyName("id")]
        public string Id
        {
            get => _id;
            set { _id = value; OnPropertyChanged(); }
        }

        [JsonPropertyName("displayName")]
        public string DisplayName
        {
            get => _displayName;
            set { _displayName = value; OnPropertyChanged(); }
        }

        [JsonPropertyName("description")]
        public string Description
        {
            get => _description;
            set { _description = value; OnPropertyChanged(); }
        }

        [JsonPropertyName("spriteVersionNumber")]
        public int SpriteVersionNumber
        {
            get => _spriteVersionNumber;
            set { _spriteVersionNumber = value; OnPropertyChanged(); }
        }

        [JsonPropertyName("spritesheetPath")]
        public string SpritesheetPath
        {
            get => _spritesheetPath;
            set { _spritesheetPath = value; OnPropertyChanged(); }
        }

        [JsonPropertyName("frameWidth")]
        public int FrameWidth
        {
            get => _frameWidth;
            set { _frameWidth = value; OnPropertyChanged(); }
        }

        [JsonPropertyName("frameHeight")]
        public int FrameHeight
        {
            get => _frameHeight;
            set { _frameHeight = value; OnPropertyChanged(); }
        }

        [JsonPropertyName("idleFrameCount")]
        public int IdleFrameCount
        {
            get => _idleFrameCount;
            set { _idleFrameCount = value; OnPropertyChanged(); }
        }

        [JsonPropertyName("animationIntervalMs")]
        public int AnimationIntervalMs
        {
            get => _animationIntervalMs;
            set { _animationIntervalMs = value; OnPropertyChanged(); }
        }

        [JsonPropertyName("folderPath")]
        public string FolderPath
        {
            get => _folderPath;
            set { _folderPath = value; OnPropertyChanged(); }
        }

        // Run-time helper properties for UI Binding
        private IImage? _icon;

        [JsonIgnore]
        public IImage? Icon
        {
            get
            {
                if (_icon == null && !string.IsNullOrEmpty(FolderPath) && !string.IsNullOrEmpty(SpritesheetPath))
                {
                    try
                    {
                        string fullPath = System.IO.Path.Combine(FolderPath, SpritesheetPath);
                        if (System.IO.File.Exists(fullPath))
                        {
                            var bitmap = new Avalonia.Media.Imaging.Bitmap(fullPath);
                            int frameWidth = FrameWidth > 0 ? FrameWidth : 192;
                            int frameHeight = FrameHeight > 0 ? FrameHeight : 208;
                            if (frameWidth <= bitmap.PixelSize.Width &&
                                frameHeight <= bitmap.PixelSize.Height)
                            {
                                // Crop the first configured animation frame for the settings preview.
                                var rect = new PixelRect(0, 0, frameWidth, frameHeight);
                                _icon = new Avalonia.Media.Imaging.CroppedBitmap(bitmap, rect);
                            }
                            else
                            {
                                bitmap.Dispose();
                            }
                        }
                    }
                    catch { }
                }
                return _icon;
            }
        }

        [JsonIgnore]
        public bool IsSelected => App.SharedViewModel?.SelectedPetId == Id;

        [JsonIgnore]
        public bool IsNotSelected => !IsSelected;

        [JsonIgnore]
        public bool IsCustom =>
            Id != "douya-chick" &&
            Id != "doro" &&
            Id != "endminguga" &&
            Id != "ikkun";

        [JsonIgnore]
        public bool IsDeletable => Id != "douya-chick";

        public void RefreshSelection()
        {
            OnPropertyChanged(nameof(IsSelected));
            OnPropertyChanged(nameof(IsNotSelected));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
