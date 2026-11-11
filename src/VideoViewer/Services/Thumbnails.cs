using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VideoViewer.Models;

namespace VideoViewer.Services
{
    /// <summary>
    /// Thumbnail + metadata pipeline.
    ///  1. Disk cache hit  -> decoded on the thread pool in a few ms (what makes scrolling feel instant)
    ///  2. Visible cards   -> LIFO stack, so whatever you're looking at right now is generated first
    ///  3. Everything else -> background queue so the whole library is ready before you scroll to it
    /// Generation tries the Windows thumbnail first (fast), then falls back to VLC (plays anything).
    /// </summary>
    public static class Thumbnails
    {
        const int MaxInMemory = 900;
        static readonly object _lock = new object();
        static readonly Stack<VideoItem> _visible = new Stack<VideoItem>();
        static readonly Queue<VideoItem> _background = new Queue<VideoItem>();
        static readonly HashSet<VideoItem> _inBackground = new HashSet<VideoItem>();
        static readonly SemaphoreSlim _diskGate = new SemaphoreSlim(6);
        static readonly List<VideoItem> _loaded = new List<VideoItem>();
        static readonly AutoResetEvent _signal = new AutoResetEvent(false);
        static Dispatcher _ui;
        static int _pending;
        static bool _started;

        public static event Action<int> PendingChanged;
        /// <summary>Raised (on UI thread) when duration/size info arrives for an item.</summary>
        public static event Action<VideoItem> MetadataArrived;

        public static void Start(Dispatcher ui)
        {
            if (_started) return;
            _started = true;
            _ui = ui;
            int workers = Math.Max(2, Math.Min(4, Environment.ProcessorCount / 2));
            for (int i = 0; i < workers; i++)
            {
                var t = new Thread(WorkerLoop) { IsBackground = true, Name = "thumb-" + i, Priority = ThreadPriority.BelowNormal };
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
            }
        }

        public static string KeyFor(VideoItem item)
        {
            using (var md5 = MD5.Create())
            {
                var s = item.Path.ToLowerInvariant() + "|" + item.Size + "|" + item.Modified.Ticks;
                var h = md5.ComputeHash(Encoding.UTF8.GetBytes(s));
                var sb = new StringBuilder(32);
                foreach (var b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        static string CachePath(VideoItem item) => Path.Combine(AppPaths.Thumbs, item.CacheKey + ".jpg");

        /// <summary>Called when a card becomes visible.</summary>
        public static void Request(VideoItem item)
        {
            item.ThumbState = ThumbState.Queued;
            var file = CachePath(item);
            if (File.Exists(file))
            {
                Task.Run(async () =>
                {
                    await _diskGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        var img = LoadImage(file);
                        if (img != null) Deliver(item, img);
                        else Enqueue(item, true);
                    }
                    finally { _diskGate.Release(); }
                });
                return;
            }
            Enqueue(item, true);
        }

        /// <summary>Queue items for background processing (metadata + thumbnail) at low priority.</summary>
        public static void Prefetch(IEnumerable<VideoItem> items)
        {
            var list = items.ToList();
            Task.Run(() =>
            {
                var todo = list.Where(it => !(it.HasMeta && File.Exists(CachePath(it)))).ToList();
                if (todo.Count == 0) return;
                lock (_lock)
                {
                    foreach (var it in todo)
                        if (_inBackground.Add(it)) _background.Enqueue(it);
                    UpdatePending();
                }
                _signal.Set();
            });
        }

        public static void Forget(VideoItem item)
        {
            lock (_lock) { _inBackground.Remove(item); _loaded.Remove(item); }
        }

        static void Enqueue(VideoItem item, bool visible)
        {
            lock (_lock)
            {
                if (visible) _visible.Push(item);
                else if (_inBackground.Add(item)) _background.Enqueue(item);
                UpdatePending();
            }
            _signal.Set();
        }

        static void UpdatePending()
        {
            int n = _visible.Count + _background.Count;
            if (n == _pending) return;
            _pending = n;
            _ui?.BeginInvoke(new Action(() => PendingChanged?.Invoke(_pending)), DispatcherPriority.Background);
        }

        static void WorkerLoop()
        {
            FrameGrabber grabber = null;
            while (true)
            {
                VideoItem item = null;
                bool fromVisible = false;
                lock (_lock)
                {
                    while (_visible.Count > 0)
                    {
                        var c = _visible.Pop();
                        if (c.ThumbState == ThumbState.Queued) { item = c; fromVisible = true; break; }
                    }
                    if (item == null && _background.Count > 0)
                    {
                        item = _background.Dequeue();
                        _inBackground.Remove(item);
                    }
                    UpdatePending();
                }
                if (item == null) { _signal.WaitOne(150); continue; }
                _signal.Set(); // wake a sibling worker in case more work is waiting

                try
                {
                    Process(item, fromVisible, ref grabber);
                }
                catch
                {
                    try { grabber?.Dispose(); } catch { }
                    grabber = null;
                    if (fromVisible) item.ThumbState = ThumbState.Failed;
                }
            }
        }

        static void Process(VideoItem item, bool visible, ref FrameGrabber grabber)
        {
            if (!File.Exists(item.Path)) return;
            var file = CachePath(item);
            bool haveThumb = File.Exists(file);

            // Fast path: Windows already knows how to thumbnail this file.
            if (!haveThumb)
            {
                var shell = Native.ShellThumbnail(item.Path, 400);
                var usable = shell != null ? ToOpaque(shell) : null;
                if (usable != null)
                {
                    SaveJpeg(usable, file);
                    haveThumb = File.Exists(file);
                }
            }

            bool needMeta = !item.HasMeta;
            if (needMeta || !haveThumb)
            {
                if (grabber == null) grabber = new FrameGrabber(Vlc.Get());
                var info = grabber.Probe(item.Path, !haveThumb);
                if (info.Frame != null)
                {
                    SaveJpeg(info.Frame, file);
                    haveThumb = File.Exists(file);
                }
                var dur = info.DurationMs; int w = info.Width, h = info.Height;
                _ui.BeginInvoke(new Action(() =>
                {
                    item.Width = w; item.Height = h; item.DurationMs = Math.Max(0, dur);
                    MetadataArrived?.Invoke(item);
                }), DispatcherPriority.Background);
            }

            if (item.ThumbState == ThumbState.Queued || visible)
            {
                if (haveThumb)
                {
                    var img = LoadImage(file);
                    if (img != null) { Deliver(item, img); return; }
                }
                item.ThumbState = ThumbState.Failed;
                _ui.BeginInvoke(new Action(() => item.Refresh(nameof(VideoItem.HasThumbnail))));
            }
        }

        static BitmapSource ToOpaque(BitmapSource src)
        {
            try
            {
                var conv = new FormatConvertedBitmap(src, PixelFormats.Bgr32, null, 0);
                int w = conv.PixelWidth, h = conv.PixelHeight;
                if (w < 8 || h < 8) return null;
                int stride = w * 4;
                var px = new byte[stride * h];
                conv.CopyPixels(px, stride, 0);
                long sum = 0; int n = 0;
                for (int i = 0; i < px.Length; i += 4 * 29) { sum += px[i] + px[i + 1] + px[i + 2]; n++; }
                if (n == 0 || sum / (3.0 * n) < 10) return null; // blank/black -> let VLC pick a better frame
                var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, px, stride);
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        static void SaveJpeg(BitmapSource src, string file)
        {
            try
            {
                var enc = new JpegBitmapEncoder { QualityLevel = 86 };
                enc.Frames.Add(BitmapFrame.Create(src));
                var tmp = file + ".tmp";
                using (var fs = File.Create(tmp)) enc.Save(fs);
                if (File.Exists(file)) File.Delete(file);
                File.Move(tmp, file);
            }
            catch { }
        }

        static BitmapImage LoadImage(string file)
        {
            try
            {
                var bi = new BitmapImage();
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    bi.DecodePixelWidth = 420;
                    bi.StreamSource = fs;
                    bi.EndInit();
                }
                bi.Freeze();
                return bi;
            }
            catch { return null; }
        }

        static void Deliver(VideoItem item, ImageSource img)
        {
            _ui.BeginInvoke(new Action(() =>
            {
                item.ThumbState = ThumbState.Loaded;
                item.SetThumbnail(img);
                TrackMemory(item);
            }), DispatcherPriority.Background);
        }

        /// <summary>Keep memory bounded on huge libraries: drop the least recently seen thumbnails.</summary>
        static void TrackMemory(VideoItem item)
        {
            List<VideoItem> drop = null;
            lock (_lock)
            {
                _loaded.Add(item);
                if (_loaded.Count > MaxInMemory)
                {
                    drop = _loaded.OrderBy(i => i.LastSeen).Take(_loaded.Count - MaxInMemory + 100).ToList();
                    foreach (var d in drop) _loaded.Remove(d);
                }
            }
            if (drop != null) foreach (var d in drop) d.ReleaseThumbnail();
        }

        /// <summary>File changed on disk: throw away the old thumbnail.</summary>
        public static void Invalidate(VideoItem item)
        {
            Forget(item);
            item.Invalidate();
        }
    }
}
