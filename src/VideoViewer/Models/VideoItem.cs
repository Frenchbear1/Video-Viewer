using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using VideoViewer.Services;

namespace VideoViewer.Models
{
    public enum ThumbState { None, Queued, Loaded, Failed }

    public sealed class VideoItem : INotifyPropertyChanged
    {
        public VideoItem(string path)
        {
            Path = path;
            Name = System.IO.Path.GetFileName(path);
            Title = System.IO.Path.GetFileNameWithoutExtension(path);
            Extension = (System.IO.Path.GetExtension(path) ?? "").TrimStart('.').ToUpperInvariant();
            Directory = System.IO.Path.GetDirectoryName(path) ?? "";
            DirectoryName = System.IO.Path.GetFileName(Directory);
        }

        public string Path { get; }
        public string Name { get; }
        public string Title { get; }
        public string Extension { get; }
        public string Directory { get; }
        public string DirectoryName { get; }

        public long Size { get; set; }
        public DateTime Modified { get; set; }
        public DateTime Date { get; set; }

        // ---- media info (filled in by the background worker) ----
        long _durationMs = -1;
        public long DurationMs
        {
            get => _durationMs;
            set { if (_durationMs != value) { _durationMs = value; OnChanged(); OnChanged(nameof(DurationText)); OnChanged(nameof(HasDuration)); } }
        }

        int _width, _height;
        public int Width { get => _width; set { if (_width != value) { _width = value; OnChanged(); OnChanged(nameof(QualityBadge)); OnChanged(nameof(HasQualityBadge)); } } }
        public int Height { get => _height; set { if (_height != value) { _height = value; OnChanged(); OnChanged(nameof(QualityBadge)); OnChanged(nameof(HasQualityBadge)); } } }

        public bool HasMeta => _durationMs >= 0;
        public bool HasDuration => _durationMs > 0;
        public bool IsPortrait => _width > 0 && _height > _width * 1.05;
        public bool IsLandscape => _width > 0 && _width > _height * 1.05;
        public bool IsSquare => _width > 0 && !IsPortrait && !IsLandscape;

        public string DurationText => Format.Duration(_durationMs);
        public string SizeText => Format.Bytes(Size);
        public string DateText => Date.ToString("MMM d, yyyy");

        public string QualityBadge
        {
            get
            {
                int p = Math.Min(_width, _height);
                if (p <= 0) return "";
                if (p >= 2000) return "4K";
                if (p >= 1400) return "1440p";
                if (p >= 1000) return "HD";
                return "";
            }
        }
        public bool HasQualityBadge => QualityBadge.Length > 0;

        // ---- thumbnail (lazy: asking for it is what queues it) ----
        internal ThumbState ThumbState;
        internal long LastSeen;
        internal string CacheKey => Thumbnails.KeyFor(this);

        ImageSource _thumb;
        public ImageSource Thumbnail
        {
            get
            {
                LastSeen = Environment.TickCount;
                if (_thumb == null && ThumbState == ThumbState.None)
                    Thumbnails.Request(this);
                return _thumb;
            }
        }
        public bool HasThumbnail => _thumb != null;

        internal void SetThumbnail(ImageSource src)
        {
            _thumb = src;
            OnChanged(nameof(Thumbnail));
            OnChanged(nameof(HasThumbnail));
        }

        internal void ReleaseThumbnail()
        {
            _thumb = null;
            ThumbState = ThumbState.None;
            // no notify: it's off-screen; it'll reload on next realize
        }

        internal void Invalidate()
        {
            _thumb = null;
            ThumbState = ThumbState.None;
            OnChanged(nameof(Thumbnail));
            OnChanged(nameof(HasThumbnail));
        }

        public event PropertyChangedEventHandler PropertyChanged;
        void OnChanged([CallerMemberName] string n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        internal void Refresh(string n) => OnChanged(n);
    }

    public static class Format
    {
        public static string Duration(long ms)
        {
            if (ms <= 0) return "";
            var t = TimeSpan.FromMilliseconds(ms);
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }

        public static string Clock(long ms)
        {
            if (ms < 0) ms = 0;
            var t = TimeSpan.FromMilliseconds(ms);
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }

        public static string Bytes(long b)
        {
            if (b <= 0) return "0 B";
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            double v = b; int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return i <= 1 ? $"{v:0} {u[i]}" : $"{v:0.#} {u[i]}";
        }
    }
}
