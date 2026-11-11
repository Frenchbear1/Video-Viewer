using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using VideoViewer.Models;
using VideoViewer.Services;

namespace VideoViewer
{
    public partial class PlayerOverlay : UserControl
    {
        static readonly float[] Speeds = { 0.25f, 0.5f, 0.75f, 1f, 1.25f, 1.5f, 1.75f, 2f, 3f };

        LibVLC _vlc;
        MediaPlayer _mp;
        Media _media;
        Settings _settings;
        VideoItem _current;

        readonly DispatcherTimer _tick;
        readonly DispatcherTimer _hide;
        readonly object _chainLock = new object();
        Task _chain = Task.CompletedTask;

        bool _dragging, _updatingSeek, _controlsVisible = true, _fullscreen, _ended;
        int _speedIndex = 3;
        long _lastScrub;
        Point _lastMouse;
        Window _overlayWindow;

        public event Action CloseRequested;
        public event Action FullscreenRequested;
        public event Action NextRequested;
        public event Action PrevRequested;

        public PlayerOverlay()
        {
            InitializeComponent();
            _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Render, (s, e) => UpdatePosition(), Dispatcher) { IsEnabled = false };
            _hide = new DispatcherTimer(TimeSpan.FromMilliseconds(2600), DispatcherPriority.Background, (s, e) => AutoHide(), Dispatcher) { IsEnabled = false };

            Seek.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((s, e) => _dragging = true));
            Seek.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((s, e) =>
            {
                _dragging = false;
                SeekTo((long)Seek.Value);
            }));

            Chrome.Loaded += (s, e) =>
            {
                // The controls live in LibVLCSharp's transparent overlay window; route its keys to us.
                var w = Window.GetWindow(Chrome);
                if (w != null && w != _overlayWindow && w != Window.GetWindow(this))
                {
                    _overlayWindow = w;
                    w.PreviewKeyDown += (o, k) => { if (HandleKey(k.Key)) k.Handled = true; };
                }
            };
        }

        public bool IsOpen => Visibility == Visibility.Visible;
        public VideoItem Current => _current;

        public void Init(Settings settings)
        {
            _settings = settings;
            VolumeSlider.Value = settings.Volume;
            UpdateMuteIcon();
            UpdateLoopIcon();
        }

        // ================= open / close =================
        public async void Open(VideoItem item, bool hasPrev, bool hasNext)
        {
            _current = item;
            _ended = false;
            TitleText.Text = item.Title;
            InfoText.Text = BuildInfo(item);
            PrevButton.IsEnabled = hasPrev;
            NextButton.IsEnabled = hasNext;
            ErrorPanel.Visibility = Visibility.Collapsed;
            SetSpinner(true);
            _updatingSeek = true;
            Seek.Maximum = Math.Max(1, item.DurationMs);
            Seek.Value = 0;
            _updatingSeek = false;
            TimeText.Text = "0:00";
            LengthText.Text = item.DurationMs > 0 ? Format.Clock(item.DurationMs) : "0:00";
            _speedIndex = 3;
            SpeedButton.Content = "1×";
            PlayButton.Content = "";

            bool wasOpen = IsOpen;
            Chrome.Visibility = Visibility.Visible;
            Visibility = Visibility.Visible;
            if (!wasOpen) AnimateIn();
            ShowControls();

            try
            {
                _vlc = await Vlc.Engine;
                await EnsurePlayerAsync();
            }
            catch (Exception ex)
            {
                ShowError("The playback engine failed to start: " + ex.Message);
                return;
            }
            if (_current != item || !IsOpen) return;

            var vlc = _vlc;
            var path = item.Path;
            Enqueue(() =>
            {
                var media = new Media(vlc, path, FromType.FromPath);
                _mp.Play(media);
                var old = _media;
                _media = media;
                old?.Dispose();
            });
            _tick.Start();
            Chrome.Focus();
        }

        public void Close()
        {
            if (!IsOpen) return;
            _tick.Stop();
            _hide.Stop();
            Chrome.Visibility = Visibility.Collapsed;
            Visibility = Visibility.Collapsed;
            Chrome.Cursor = null;
            _current = null;
            if (_mp != null) Enqueue(() => _mp.Stop());
        }

        public void SetFullscreen(bool on)
        {
            _fullscreen = on;
            VideoFrame.Margin = on ? new Thickness(0) : new Thickness(40, 64, 40, 36);
            FrameShadow.CornerRadius = new CornerRadius(on ? 0 : 6);
            FrameGlow.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            FullscreenButton.Content = on ? "" : "";
            FullscreenButton.ToolTip = on ? "Exit full screen (F / Esc)" : "Full screen (F)";
            ShowControls();
        }

        async Task EnsurePlayerAsync()
        {
            if (_mp == null)
            {
                _mp = new MediaPlayer(_vlc) { EnableHardwareDecoding = true, EnableKeyInput = false, EnableMouseInput = false };
                _mp.Playing += (s, e) => Ui(OnPlaying);
                _mp.Paused += (s, e) => Ui(() => { PlayButton.Content = ""; ShowControls(); });
                _mp.EndReached += (s, e) => Ui(OnEnded);
                _mp.EncounteredError += (s, e) => Ui(() => ShowError(null));
                _mp.LengthChanged += (s, e) => Ui(() => { if (e.Length > 0) { Seek.Maximum = e.Length; LengthText.Text = Format.Clock(e.Length); } });
            }

            if (!Video.IsLoaded)
            {
                var tcs = new TaskCompletionSource<bool>();
                RoutedEventHandler h = null;
                h = (s, e) => { Video.Loaded -= h; tcs.TrySetResult(true); };
                Video.Loaded += h;
                if (!Video.IsLoaded) await tcs.Task;
                else Video.Loaded -= h;
            }
            if (Video.MediaPlayer != _mp)
            {
                Video.MediaPlayer = _mp;
                await Dispatcher.Yield(DispatcherPriority.Loaded);
            }
        }

        void Enqueue(Action a)
        {
            lock (_chainLock)
                _chain = _chain.ContinueWith(_ => { try { a(); } catch { } }, TaskScheduler.Default);
        }

        void Ui(Action a) => Dispatcher.BeginInvoke(a);

        // ================= player events =================
        void OnPlaying()
        {
            if (_current == null) return;
            SetSpinner(false);
            ErrorPanel.Visibility = Visibility.Collapsed;
            PlayButton.Content = "";
            _ended = false;
            var vol = (int)VolumeSlider.Value;
            var muted = _settings?.Muted ?? false;
            var rate = Speeds[_speedIndex];
            Enqueue(() =>
            {
                _mp.Volume = vol;
                _mp.Mute = muted;
                if (Math.Abs(_mp.Rate - rate) > 0.01) _mp.SetRate(rate);
            });
            ScheduleHide();
        }

        void OnEnded()
        {
            if (_current == null) return;
            if (_settings != null && _settings.Loop)
            {
                Enqueue(() => { _mp.Stop(); _mp.Play(); });
                return;
            }
            _ended = true;
            PlayButton.Content = "";
            _updatingSeek = true;
            Seek.Value = Seek.Maximum;
            _updatingSeek = false;
            ShowControls();
        }

        void ShowError(string detail)
        {
            SetSpinner(false);
            ErrorDetail.Text = detail ?? "The file may be damaged, incomplete, or still being copied.";
            ErrorPanel.Visibility = Visibility.Visible;
            ShowControls();
        }

        void UpdatePosition()
        {
            if (_mp == null || _current == null || _dragging) return;
            long len = _mp.Length;
            long t = _mp.Time;
            if (len <= 0) len = Math.Max(1, _current.DurationMs);
            if (t < 0) return;
            _updatingSeek = true;
            if (Math.Abs(Seek.Maximum - len) > 1) Seek.Maximum = len;
            if (!_ended) Seek.Value = Math.Min(t, len);
            _updatingSeek = false;
            TimeText.Text = Format.Clock(t);
            if (len > 1) LengthText.Text = Format.Clock(len);
        }

        // ================= transport =================
        public void TogglePlay()
        {
            if (_mp == null || _current == null) return;
            if (_ended || _mp.State == VLCState.Ended || _mp.State == VLCState.Stopped)
            {
                _ended = false;
                Enqueue(() => { _mp.Stop(); _mp.Play(); });
                Flash("");
                return;
            }
            bool playing = _mp.IsPlaying;
            Enqueue(() => _mp.SetPause(playing));
            PlayButton.Content = playing ? "" : "";
            Flash(playing ? "" : "");
            if (playing) ShowControls(); else ScheduleHide();
        }

        void SeekBy(long deltaMs)
        {
            if (_mp == null) return;
            long len = _mp.Length > 0 ? _mp.Length : _current?.DurationMs ?? 0;
            long t = Math.Max(0, _mp.Time + deltaMs);
            if (len > 0) t = Math.Min(t, len - 200);
            SeekTo(t);
            Flash(deltaMs < 0 ? "" : "");
        }

        void SeekTo(long ms)
        {
            if (_mp == null) return;
            if (_ended || _mp.State == VLCState.Ended)
            {
                _ended = false;
                Enqueue(() => { _mp.Stop(); _mp.Play(); _mp.Time = ms; });
                return;
            }
            _mp.Time = Math.Max(0, ms);
            _updatingSeek = true;
            Seek.Value = ms;
            _updatingSeek = false;
            TimeText.Text = Format.Clock(ms);
        }

        void Seek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updatingSeek || _mp == null || Seek == null) return;
            TimeText.Text = Format.Clock((long)e.NewValue);
            if (_dragging)
            {
                // live scrubbing, throttled
                long now = Environment.TickCount;
                if (now - _lastScrub > 70) { _lastScrub = now; _mp.Time = (long)e.NewValue; }
            }
            else SeekTo((long)e.NewValue);
        }

        void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_settings == null) return;
            int v = (int)Math.Round(e.NewValue);
            _settings.Volume = v;
            if (_settings.Muted && v > 0) _settings.Muted = false;
            if (_mp != null) { _mp.Volume = v; _mp.Mute = _settings.Muted; }
            UpdateMuteIcon();
        }

        void ChangeVolume(int delta)
        {
            VolumeSlider.Value = Math.Max(0, Math.Min(100, VolumeSlider.Value + delta));
            Flash(VolumeSlider.Value <= 0 ? "" : "");
        }

        void ToggleMute()
        {
            if (_settings == null) return;
            _settings.Muted = !_settings.Muted;
            if (_mp != null) _mp.Mute = _settings.Muted;
            UpdateMuteIcon();
            Flash(_settings.Muted ? "" : "");
        }

        void UpdateMuteIcon()
        {
            if (MuteButton == null || _settings == null) return;
            bool silent = _settings.Muted || VolumeSlider.Value <= 0;
            MuteButton.Content = silent ? "" : VolumeSlider.Value < 34 ? "" : VolumeSlider.Value < 67 ? "" : "";
            MuteButton.ToolTip = silent ? "Unmute (M)" : "Mute (M)";
        }

        void UpdateLoopIcon()
        {
            bool on = _settings?.Loop ?? false;
            LoopButton.Foreground = on ? (Brush)FindResource("Accent2Brush") : new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
            LoopButton.ToolTip = on ? "Loop is on (L)" : "Loop is off (L)";
        }

        void CycleSpeed(int dir = 1)
        {
            _speedIndex = (_speedIndex + dir + Speeds.Length) % Speeds.Length;
            var r = Speeds[_speedIndex];
            SpeedButton.Content = r.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "×";
            if (_mp != null) Enqueue(() => _mp.SetRate(r));
        }

        // ================= keyboard =================
        public bool HandleKey(Key key)
        {
            if (!IsOpen) return false;
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            switch (key)
            {
                case Key.Space:
                case Key.K:
                case Key.MediaPlayPause:
                    TogglePlay(); break;
                case Key.Left: SeekBy(ctrl ? -30000 : -5000); break;
                case Key.Right: SeekBy(ctrl ? 30000 : 5000); break;
                case Key.J: SeekBy(-10000); break;
                case Key.OemPeriod:
                    if (_mp != null && !_mp.IsPlaying) Enqueue(() => _mp.NextFrame()); else SeekBy(10000);
                    break;
                case Key.Up: ChangeVolume(5); break;
                case Key.Down: ChangeVolume(-5); break;
                case Key.M: ToggleMute(); break;
                case Key.F:
                case Key.F11:
                    FullscreenRequested?.Invoke(); break;
                case Key.L: Loop_Click(null, null); break;
                case Key.Escape:
                    if (_fullscreen) FullscreenRequested?.Invoke(); else CloseRequested?.Invoke();
                    break;
                case Key.PageDown:
                case Key.N:
                case Key.MediaNextTrack:
                    if (NextButton.IsEnabled) NextRequested?.Invoke(); break;
                case Key.PageUp:
                case Key.P:
                case Key.MediaPreviousTrack:
                    if (PrevButton.IsEnabled) PrevRequested?.Invoke(); break;
                case Key.Home: SeekTo(0); break;
                case Key.OemPlus: case Key.Add: CycleSpeed(1); break;
                case Key.OemMinus: case Key.Subtract: CycleSpeed(-1); break;
                default:
                    if (key >= Key.D0 && key <= Key.D9 && _mp != null)
                    {
                        long len = _mp.Length > 0 ? _mp.Length : _current?.DurationMs ?? 0;
                        if (len > 0) SeekTo(len * (key - Key.D0) / 10);
                        break;
                    }
                    return false;
            }
            ShowControls();
            return true;
        }

        // ================= mouse =================
        void HitLayer_MouseDown(object sender, MouseButtonEventArgs e)
        {
            Chrome.Focus();
            if (e.ClickCount == 2)
            {
                TogglePlay(); // undo the first click's toggle
                FullscreenRequested?.Invoke();
            }
            else TogglePlay();
            e.Handled = true;
        }

        void Chrome_MouseMove(object sender, MouseEventArgs e)
        {
            var p = e.GetPosition(Chrome);
            if (Math.Abs(p.X - _lastMouse.X) < 2 && Math.Abs(p.Y - _lastMouse.Y) < 2) return;
            _lastMouse = p;
            ShowControls();
        }

        void Chrome_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            ChangeVolume(e.Delta > 0 ? 5 : -5);
            ShowControls();
            e.Handled = true;
        }

        void Backdrop_MouseDown(object sender, MouseButtonEventArgs e) => CloseRequested?.Invoke();

        // ================= buttons =================
        void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();
        void Back_Click(object sender, RoutedEventArgs e) => SeekBy(-10000);
        void Forward_Click(object sender, RoutedEventArgs e) => SeekBy(10000);
        void Prev_Click(object sender, RoutedEventArgs e) => PrevRequested?.Invoke();
        void Next_Click(object sender, RoutedEventArgs e) => NextRequested?.Invoke();
        void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
        void Fullscreen_Click(object sender, RoutedEventArgs e) => FullscreenRequested?.Invoke();
        void Mute_Click(object sender, RoutedEventArgs e) => ToggleMute();
        void Speed_Click(object sender, RoutedEventArgs e) => CycleSpeed(1);

        void Loop_Click(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return;
            _settings.Loop = !_settings.Loop;
            UpdateLoopIcon();
            Flash("");
        }

        void Reveal_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            try { Process.Start("explorer.exe", "/select,\"" + _current.Path + "\""); } catch { }
        }

        // ================= visuals =================
        void ShowControls()
        {
            if (!_controlsVisible)
            {
                _controlsVisible = true;
                ControlsLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));
                ControlsLayer.IsHitTestVisible = true;
            }
            Chrome.Cursor = null;
            ScheduleHide();
        }

        void ScheduleHide()
        {
            _hide.Stop();
            _hide.Start();
        }

        void AutoHide()
        {
            _hide.Stop();
            if (_mp == null || !_mp.IsPlaying || _dragging) return;
            if (BottomBar.IsMouseOver || TopBar.IsMouseOver) { ScheduleHide(); return; }
            _controlsVisible = false;
            ControlsLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(320)));
            ControlsLayer.IsHitTestVisible = false;
            Chrome.Cursor = Cursors.None;
        }

        void Flash(string glyph)
        {
            FlashGlyph.Text = glyph;
            var d = TimeSpan.FromMilliseconds(520);
            FlashBadge.BeginAnimation(OpacityProperty, new DoubleAnimationUsingKeyFrames
            {
                KeyFrames =
                {
                    new LinearDoubleKeyFrame(0.95, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(60))),
                    new LinearDoubleKeyFrame(0.95, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220))),
                    new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(d)),
                }
            });
            var grow = new DoubleAnimation(0.8, 1.15, d) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            FlashScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            FlashScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }

        void SetSpinner(bool on)
        {
            Spinner.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (on) SpinRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(800)) { RepeatBehavior = RepeatBehavior.Forever });
            else SpinRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        }

        void AnimateIn()
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            Backdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            var s = new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease };
            FrameScale.BeginAnimation(ScaleTransform.ScaleXProperty, s);
            FrameScale.BeginAnimation(ScaleTransform.ScaleYProperty, s);
        }

        static string BuildInfo(VideoItem i)
        {
            var parts = new System.Collections.Generic.List<string>();
            if (i.Width > 0) parts.Add($"{i.Width}×{i.Height}");
            if (i.DurationMs > 0) parts.Add(i.DurationText);
            parts.Add(i.SizeText);
            parts.Add(i.DateText);
            parts.Add(i.DirectoryName);
            return string.Join("   ·   ", parts);
        }

        public void Shutdown()
        {
            try
            {
                _tick.Stop();
                _hide.Stop();
                var mp = _mp;
                if (mp != null)
                {
                    Video.MediaPlayer = null;
                    Task.Run(() => { try { mp.Stop(); mp.Dispose(); } catch { } }).Wait(1500);
                }
            }
            catch { }
        }
    }
}
