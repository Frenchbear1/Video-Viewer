using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using VideoViewer.Models;

namespace VideoViewer.Services
{
    /// <summary>
    /// Owns the set of attached folders and every video inside them.
    /// Loads a cached index instantly, then reconciles with disk in the background
    /// and keeps itself in sync with FileSystemWatchers.
    /// </summary>
    public sealed class Library
    {
        public static readonly HashSet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".m4v", ".mov", ".qt", ".mkv", ".webm", ".avi", ".wmv", ".asf", ".flv", ".f4v",
            ".mpg", ".mpeg", ".mpe", ".mpv", ".m2v", ".m2ts", ".mts", ".ts", ".m2t", ".tts", ".vob",
            ".3gp", ".3g2", ".ogv", ".ogm", ".divx", ".xvid", ".rm", ".rmvb", ".dv", ".mxf", ".hevc",
            ".h264", ".264", ".h265", ".265", ".y4m", ".amv", ".nut", ".mk3d", ".evo", ".gxf", ".wtv",
            ".dvr-ms", ".mod", ".tod", ".bik", ".insv", ".lrv", ".avchd", ".mjpg", ".mjpeg", ".ivf", ".av1"
        };

        public static bool IsVideo(string path)
        {
            var name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name) || name.StartsWith("._")) return false; // macOS/iPhone AppleDouble junk
            return VideoExtensions.Contains(Path.GetExtension(path) ?? "");
        }

        readonly Dispatcher _ui;
        readonly Dictionary<string, VideoItem> _items = new Dictionary<string, VideoItem>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, FileSystemWatcher> _watchers = new Dictionary<string, FileSystemWatcher>(StringComparer.OrdinalIgnoreCase);
        readonly ConcurrentDictionary<string, byte> _dirty = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        readonly ConcurrentDictionary<string, byte> _dirtyDirs = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        readonly DispatcherTimer _flush, _saveTimer;
        int _scanning;

        public List<string> Folders { get; } = new List<string>();
        public IReadOnlyCollection<VideoItem> Items => _items.Values;
        public bool IsScanning => _scanning > 0;

        /// <summary>Raised on the UI thread whenever the set of videos changes.</summary>
        public event Action Changed;
        public event Action ScanningChanged;

        public Library(Dispatcher ui)
        {
            _ui = ui;
            _flush = new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background, (s, e) => FlushDirty(), ui) { IsEnabled = false };
            _saveTimer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (s, e) => { _saveTimer.Stop(); SaveIndex(); }, ui) { IsEnabled = false };
            Thumbnails.MetadataArrived += _ => ScheduleSave();
        }

        // ---------------- load / save ----------------
        public void LoadCached(IEnumerable<string> folders)
        {
            Folders.Clear();
            Folders.AddRange(folders.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase));
            var index = Json.Load<LibraryIndex>(AppPaths.LibraryFile);
            foreach (var e in index.Items ?? new List<IndexEntry>())
            {
                if (string.IsNullOrEmpty(e.Path) || !IsUnderAnyFolder(e.Path)) continue;
                var it = new VideoItem(e.Path)
                {
                    Size = e.Size,
                    Modified = new DateTime(e.ModifiedTicks),
                    Date = new DateTime(e.DateTicks),
                };
                if (e.DurationMs >= 0) { it.Width = e.Width; it.Height = e.Height; it.DurationMs = e.DurationMs; }
                _items[e.Path] = it;
            }
        }

        public void ScheduleSave() { _saveTimer.Stop(); _saveTimer.Start(); }

        public void SaveIndex(bool sync = false)
        {
            _saveTimer.Stop();
            var index = new LibraryIndex
            {
                Items = _items.Values.Select(i => new IndexEntry
                {
                    Path = i.Path, Size = i.Size, ModifiedTicks = i.Modified.Ticks, DateTicks = i.Date.Ticks,
                    DurationMs = i.DurationMs, Width = i.Width, Height = i.Height
                }).ToList()
            };
            if (sync) Json.Save(AppPaths.LibraryFile, index);
            else Task.Run(() => Json.Save(AppPaths.LibraryFile, index));
        }

        // ---------------- folders ----------------
        public bool AddFolder(string folder)
        {
            folder = Normalize(folder);
            if (folder == null || !Directory.Exists(folder)) return false;
            if (Folders.Any(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase))) return false;
            Folders.Add(folder);
            Watch(folder);
            ScanFolder(folder);
            return true;
        }

        public void RemoveFolder(string folder)
        {
            Folders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
            if (_watchers.TryGetValue(folder, out var w)) { w.EnableRaisingEvents = false; w.Dispose(); _watchers.Remove(folder); }
            foreach (var key in _items.Keys.Where(p => !IsUnderAnyFolder(p)).ToList())
            {
                Thumbnails.Forget(_items[key]);
                _items.Remove(key);
            }
            ScheduleSave();
            Changed?.Invoke();
        }

        public string RootOf(VideoItem item) =>
            Folders.Where(f => IsUnder(item.Path, f)).OrderByDescending(f => f.Length).FirstOrDefault();

        public int CountIn(string folder) => _items.Values.Count(i => IsUnder(i.Path, folder));

        public void StartWatchingAndRescan()
        {
            foreach (var f in Folders.ToList())
            {
                Watch(f);
                ScanFolder(f);
            }
        }

        public void RescanAll()
        {
            foreach (var f in Folders.ToList()) ScanFolder(f);
        }

        static string Normalize(string p)
        {
            try
            {
                var full = Path.GetFullPath(p);
                if (full.Length > 3) full = full.TrimEnd('\\', '/');
                return full;
            }
            catch { return null; }
        }

        static bool IsUnder(string path, string folder)
        {
            if (folder.EndsWith("\\")) return path.StartsWith(folder, StringComparison.OrdinalIgnoreCase);
            return path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase);
        }

        bool IsUnderAnyFolder(string path) => Folders.Any(f => IsUnder(path, f));

        // ---------------- scanning ----------------
        void ScanFolder(string folder)
        {
            _scanning++;
            ScanningChanged?.Invoke();
            Task.Run(() =>
            {
                var found = new List<FileInfo>();
                var stack = new Stack<string>();
                stack.Push(folder);
                while (stack.Count > 0)
                {
                    var dir = stack.Pop();
                    try
                    {
                        var di = new DirectoryInfo(dir);
                        foreach (var fi in di.EnumerateFiles())
                        {
                            try { if (IsVideo(fi.FullName) && (fi.Attributes & FileAttributes.Hidden) == 0) found.Add(fi); }
                            catch { }
                        }
                        foreach (var sub in di.EnumerateDirectories())
                        {
                            try
                            {
                                if ((sub.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0) continue;
                                var n = sub.Name;
                                if (n.StartsWith("$") || n.StartsWith(".") || n.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)) continue;
                                stack.Push(sub.FullName);
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
                return found;
            }).ContinueWith(t =>
            {
                _scanning--;
                if (t.Status == TaskStatus.RanToCompletion && Folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                    Reconcile(folder, t.Result);
                ScanningChanged?.Invoke();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void Reconcile(string folder, List<FileInfo> found)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var touched = new List<VideoItem>();
            foreach (var fi in found)
            {
                seen.Add(fi.FullName);
                var item = Upsert(fi, out bool changed);
                if (changed) touched.Add(item);
            }
            foreach (var key in _items.Keys.Where(p => IsUnder(p, folder) && !seen.Contains(p)).ToList())
            {
                // Only drop it if no other attached folder still covers it
                if (Folders.Any(f => !string.Equals(f, folder, StringComparison.OrdinalIgnoreCase) && IsUnder(key, f) && File.Exists(key))) continue;
                Thumbnails.Forget(_items[key]);
                _items.Remove(key);
            }
            ScheduleSave();
            Changed?.Invoke();
            Thumbnails.Prefetch(_items.Values.Where(i => IsUnder(i.Path, folder)).OrderByDescending(i => i.Date));
        }

        VideoItem Upsert(FileInfo fi, out bool changed)
        {
            changed = false;
            var modified = fi.LastWriteTime;
            var created = fi.CreationTime;
            var date = created < modified && created.Year > 1980 ? created : modified;
            if (_items.TryGetValue(fi.FullName, out var existing))
            {
                if (existing.Size == fi.Length && existing.Modified == modified) return existing;
                existing.Size = fi.Length;
                existing.Modified = modified;
                existing.Date = date;
                existing.DurationMs = -1;
                existing.Width = 0; existing.Height = 0;
                Thumbnails.Invalidate(existing);
                changed = true;
                return existing;
            }
            var item = new VideoItem(fi.FullName) { Size = fi.Length, Modified = modified, Date = date };
            _items[fi.FullName] = item;
            changed = true;
            return item;
        }

        // ---------------- live updates ----------------
        void Watch(string folder)
        {
            if (_watchers.ContainsKey(folder)) return;
            try
            {
                var w = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite
                };
                w.Created += (s, e) => MarkDirty(e.FullPath);
                w.Changed += (s, e) => MarkDirty(e.FullPath);
                w.Deleted += (s, e) => MarkDirty(e.FullPath);
                w.Renamed += (s, e) => { MarkDirty(e.OldFullPath); MarkDirty(e.FullPath); };
                w.Error += (s, e) => _ui.BeginInvoke(new Action(() => ScanFolder(folder)));
                w.EnableRaisingEvents = true;
                _watchers[folder] = w;
            }
            catch { }
        }

        void MarkDirty(string path)
        {
            _dirty[path] = 0;
            _ui.BeginInvoke(new Action(() => { _flush.Stop(); _flush.Start(); }));
        }

        void FlushDirty()
        {
            _flush.Stop();
            var paths = _dirty.Keys.ToList();
            foreach (var p in paths) _dirty.TryRemove(p, out _);
            bool any = false;
            var fresh = new List<VideoItem>();
            foreach (var p in paths)
            {
                if (!IsUnderAnyFolder(p)) continue;
                if (Directory.Exists(p))
                {
                    // A whole folder appeared or was renamed into place: scan it.
                    _dirtyDirs[p] = 0;
                    continue;
                }
                if (File.Exists(p))
                {
                    if (!IsVideo(p)) continue;
                    try
                    {
                        var fi = new FileInfo(p);
                        var it = Upsert(fi, out bool changed);
                        if (changed) { any = true; fresh.Add(it); }
                    }
                    catch { }
                }
                else
                {
                    // Deleted file -> remove. Deleted folder -> remove everything under it.
                    var gone = _items.Keys.Where(k => string.Equals(k, p, StringComparison.OrdinalIgnoreCase) || IsUnder(k, p)).ToList();
                    foreach (var k in gone) { Thumbnails.Forget(_items[k]); _items.Remove(k); any = true; }
                }
            }
            foreach (var d in _dirtyDirs.Keys.ToList())
            {
                _dirtyDirs.TryRemove(d, out _);
                ScanSubfolder(d);
            }
            if (any)
            {
                ScheduleSave();
                Changed?.Invoke();
                if (fresh.Count > 0) Thumbnails.Prefetch(fresh);
            }
        }

        void ScanSubfolder(string dir)
        {
            Task.Run(() =>
            {
                try
                {
                    return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories)
                        .Where(f => IsVideo(f.FullName)).ToList();
                }
                catch { return new List<FileInfo>(); }
            }).ContinueWith(t =>
            {
                var fresh = new List<VideoItem>();
                foreach (var fi in t.Result)
                {
                    var it = Upsert(fi, out bool changed);
                    if (changed) fresh.Add(it);
                }
                if (fresh.Count > 0)
                {
                    ScheduleSave();
                    Changed?.Invoke();
                    Thumbnails.Prefetch(fresh);
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }
}
