using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace VideoViewer.Services
{
    public sealed class MediaInfo
    {
        public long DurationMs = -1;
        public int Width, Height; // display size (rotation + pixel aspect applied)
        public BitmapSource Frame;
    }

    /// <summary>
    /// Pulls metadata and a representative frame out of ANY file VLC can open
    /// (HEVC/HDR iPhone .mov, mkv, avi, wmv, ...), decoding off-screen into memory.
    /// One instance per worker thread.
    /// </summary>
    internal sealed class FrameGrabber : IDisposable
    {
        const int TargetW = 480, TargetH = 270;

        readonly LibVLC _vlc;
        readonly MediaPlayer _mp;

        // keep delegates alive for native callbacks
        readonly MediaPlayer.LibVLCVideoFormatCb _formatCb;
        readonly MediaPlayer.LibVLCVideoCleanupCb _cleanupCb;
        readonly MediaPlayer.LibVLCVideoLockCb _lockCb;
        readonly MediaPlayer.LibVLCVideoDisplayCb _displayCb;

        IntPtr _buffer = IntPtr.Zero;
        int _bufferSize;
        volatile uint _w, _h, _pitch;
        volatile bool _want;
        int _frameCount;
        byte[] _result;
        readonly ManualResetEventSlim _got = new ManualResetEventSlim(false);
        readonly object _sync = new object();

        public FrameGrabber(LibVLC vlc)
        {
            _vlc = vlc;
            _mp = new MediaPlayer(vlc) { EnableHardwareDecoding = false, EnableKeyInput = false, EnableMouseInput = false };
            _formatCb = OnFormat;
            _cleanupCb = OnCleanup;
            _lockCb = OnLock;
            _displayCb = OnDisplay;
            _mp.SetVideoFormatCallbacks(_formatCb, _cleanupCb);
            _mp.SetVideoCallbacks(_lockCb, null, _displayCb);
        }

        public MediaInfo Probe(string path, bool wantFrame)
        {
            var info = new MediaInfo();
            using (var media = new Media(_vlc, path, FromType.FromPath))
            {
                try { media.Parse(MediaParseOptions.ParseLocal, 5000).Wait(6000); } catch { }

                info.DurationMs = media.Duration > 0 ? media.Duration : 0;
                try
                {
                    foreach (var t in media.Tracks)
                    {
                        if (t.TrackType != TrackType.Video) continue;
                        var v = t.Data.Video;
                        double w = v.Width, h = v.Height;
                        if (v.SarNum > 0 && v.SarDen > 0 && v.SarNum != v.SarDen) w = w * v.SarNum / v.SarDen;
                        bool rotated = v.Orientation == VideoOrientation.LeftTop || v.Orientation == VideoOrientation.LeftBottom
                                    || v.Orientation == VideoOrientation.RightTop || v.Orientation == VideoOrientation.RightBottom;
                        if (rotated) { var tmp = w; w = h; h = tmp; }
                        info.Width = (int)Math.Round(w);
                        info.Height = (int)Math.Round(h);
                        break;
                    }
                }
                catch { }

                if (!wantFrame) return info;

                double startSec = 0;
                if (info.DurationMs > 4000) startSec = Math.Min(info.DurationMs / 1000.0 * 0.12, 90);
                else if (info.DurationMs > 1200) startSec = 0.6;

                media.AddOption(":no-audio");
                media.AddOption(":no-spu");
                media.AddOption(":avcodec-hw=none");
                media.AddOption(":input-fast-seek");
                media.AddOption(":start-time=" + startSec.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));

                lock (_sync) { _result = null; _frameCount = 0; _want = true; }
                _got.Reset();
                try
                {
                    _mp.Play(media);
                    _got.Wait(info.DurationMs > 0 && info.DurationMs < 4000 ? 4000 : 7000);
                }
                finally
                {
                    _want = false;
                    try { _mp.Stop(); } catch { }
                }

                byte[] px;
                uint w2, h2, pitch;
                lock (_sync) { px = _result; w2 = _w; h2 = _h; pitch = _pitch; }
                if (px != null && w2 > 0 && h2 > 0)
                {
                    var bmp = BitmapSource.Create((int)w2, (int)h2, 96, 96, PixelFormats.Bgr32, null, px, (int)pitch);
                    bmp.Freeze();
                    info.Frame = bmp;
                    if (info.Width == 0) { info.Width = (int)w2; info.Height = (int)h2; }
                }
            }
            return info;
        }

        // ---- libvlc callbacks (run on VLC threads) ----
        uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
        {
            double sw = Math.Max(1, width), sh = Math.Max(1, height);
            double scale = Math.Min(1.0, Math.Max(TargetW / sw, TargetH / sh));
            uint w = (uint)Math.Max(2, Math.Round(sw * scale)) & ~1u;
            uint h = (uint)Math.Max(2, Math.Round(sh * scale)) & ~1u;

            Marshal.Copy(new byte[] { (byte)'R', (byte)'V', (byte)'3', (byte)'2' }, 0, chroma, 4);
            width = w; height = h;
            pitches = w * 4; lines = h;

            lock (_sync)
            {
                int need = (int)(w * 4 * h);
                if (_buffer == IntPtr.Zero || _bufferSize < need)
                {
                    if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
                    _buffer = Marshal.AllocHGlobal(need + 64);
                    _bufferSize = need;
                }
                _w = w; _h = h; _pitch = w * 4;
            }
            return 1;
        }

        void OnCleanup(ref IntPtr opaque) { }

        IntPtr OnLock(IntPtr opaque, IntPtr planes)
        {
            Marshal.WriteIntPtr(planes, _buffer);
            return IntPtr.Zero;
        }

        void OnDisplay(IntPtr opaque, IntPtr picture)
        {
            if (!_want) return;
            lock (_sync)
            {
                if (!_want || _buffer == IntPtr.Zero) return;
                int len = (int)(_pitch * _h);
                var px = new byte[len];
                Marshal.Copy(_buffer, px, 0, len);
                _frameCount++;
                // skip black / fade-in frames for a little while
                if (_frameCount < 24 && IsMostlyBlack(px)) return;
                _result = px;
                _want = false;
            }
            _got.Set();
        }

        static bool IsMostlyBlack(byte[] px)
        {
            long sum = 0; int n = 0;
            for (int i = 0; i < px.Length; i += 4 * 37) { sum += px[i] + px[i + 1] + px[i + 2]; n++; }
            return n > 0 && sum / (3.0 * n) < 14;
        }

        public void Dispose()
        {
            try { _mp.Stop(); } catch { }
            _mp.Dispose();
            lock (_sync)
            {
                if (_buffer != IntPtr.Zero) { Marshal.FreeHGlobal(_buffer); _buffer = IntPtr.Zero; }
            }
        }
    }
}
