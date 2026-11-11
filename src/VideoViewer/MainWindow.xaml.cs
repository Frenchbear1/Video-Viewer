using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shell;
using System.Windows.Threading;
using VideoViewer.Models;
using VideoViewer.Services;

namespace VideoViewer
{
    public partial class MainWindow : Window
    {
        static readonly (string Key, string Label)[] SortOptions =
        {
            ("Newest", "Newest first"), ("Oldest", "Oldest first"),
            ("NameAsc", "Name A–Z"), ("NameDesc", "Name Z–A"),
            ("Largest", "Largest"), ("Smallest", "Smallest"),
            ("Longest", "Longest"), ("Shortest", "Shortest"),
        };
        static readonly (string Key, string Label)[] LengthOptions =
            { ("any", "Any"), ("short", "Under 1 min"), ("medium", "1–5 min"), ("long", "5–20 min"), ("xl", "20 min +") };
        static readonly (string Key, string Label)[] ShapeOptions =
            { ("any", "Any"), ("landscape", "Landscape"), ("portrait", "Portrait"), ("square", "Square") };
        static readonly (string Key, string Label)[] DateOptions =
            { ("any", "Any time"), ("today", "Today"), ("week", "Past week"), ("month", "Past month"), ("year", "This year") };

        readonly Settings _settings;
        readonly Library _library;
        readonly DispatcherTimer _searchDebounce, _metaRefresh, _toastTimer;

        List<VideoItem> _view = new List<VideoItem>();
        int _columns = 4;
        string _folderFilter;
        readonly HashSet<string> _types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string _length = "any", _shape = "any", _date = "any", _search = "";
        string _typeSignature = "";
        bool _metaDirty, _isFullscreen;
        WindowState _preFsState;
        Rect _preFsBounds;
        VideoItem _pressed;
        int _pending;

        public MainWindow()
        {
            InitializeComponent();
            _settings = Json.Load<Settings>(AppPaths.SettingsFile);
            _library = new Library(Dispatcher);
            _library.LoadCached(_settings.Folders);

            _searchDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(110), DispatcherPriority.Input, (s, e) =>
            {
                _searchDebounce.Stop();
                _search = SearchBox.Text.Trim();
                Refresh(scrollTop: true);
            }, Dispatcher) { IsEnabled = false };
            _metaRefresh = new DispatcherTimer(TimeSpan.FromMilliseconds(1500), DispatcherPriority.Background, (s, e) =>
            {
                if (_metaDirty) { _metaDirty = false; Refresh(); }
            }, Dispatcher);
            _toastTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(2400), DispatcherPriority.Background, (s, e) =>
            {
                _toastTimer.Stop();
                Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)));
            }, Dispatcher) { IsEnabled = false };

            Player.Init(_settings);
            Player.CloseRequested += ClosePlayer;
            Player.FullscreenRequested += ToggleFullscreen;
            Player.NextRequested += () => Step(1);
            Player.PrevRequested += () => Step(-1);

            _library.Changed += OnLibraryChanged;
            _library.ScanningChanged += UpdateStatus;
            Thumbnails.PendingChanged += n => { _pending = n; UpdateStatus(); };
            Thumbnails.MetadataArrived += item =>
            {
                if (_length != "any" || _shape != "any" || _settings.Sort == "Longest" || _settings.Sort == "Shortest")
                    _metaDirty = true;
            };

            SizeSlider.Value = _settings.CardWidth;
            SortText.Text = SortOptions.FirstOrDefault(o => o.Key == _settings.Sort).Label ?? "Newest first";

            BuildChips(LengthChips, LengthOptions, () => _length, v => _length = v);
            BuildChips(ShapeChips, ShapeOptions, () => _shape, v => _shape = v);
            BuildChips(DateChips, DateOptions, () => _date, v => _date = v);

            if (_settings.WindowWidth > 600 && _settings.WindowHeight > 400)
            {
                Width = Math.Min(_settings.WindowWidth, SystemParameters.VirtualScreenWidth);
                Height = Math.Min(_settings.WindowHeight, SystemParameters.VirtualScreenHeight);
            }
            if (_settings.WindowMaximized) WindowState = WindowState.Maximized;

            Loaded += OnLoaded;
            StateChanged += (s, e) => UpdateMaximizedLayout();
            PreviewKeyDown += OnPreviewKeyDown;
            DragEnter += OnDragOver;
            DragOver += OnDragOver;
            DragLeave += (s, e) => DropHint.Visibility = Visibility.Collapsed;
            Drop += OnDrop;
            Closing += OnClosing;

            RebuildFolderList();
            RebuildTypeChips();
            Refresh(scrollTop: true);
        }

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            UpdateMaximizedLayout();
            Thumbnails.Start(Dispatcher);
            Vlc.WarmUp();
            _library.StartWatchingAndRescan();
            Thumbnails.Prefetch(_library.Items.OrderByDescending(i => i.Date));

            // "Open with" / drag onto exe support
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1)
            {
                var p = args[1];
                if (Directory.Exists(p)) AddFolders(new[] { p });
                else if (File.Exists(p)) PlayFile(p);
            }
        }

        void OnClosing(object sender, CancelEventArgs e)
        {
            if (_isFullscreen) ToggleFullscreen();
            _settings.Folders = _library.Folders.ToList();
            _settings.CardWidth = SizeSlider.Value;
            _settings.WindowMaximized = WindowState == WindowState.Maximized;
            var rb = RestoreBounds;
            if (!rb.IsEmpty) { _settings.WindowWidth = rb.Width; _settings.WindowHeight = rb.Height; }
            Json.Save(AppPaths.SettingsFile, _settings);
            _library.SaveIndex(sync: true);
            Player.Shutdown();
        }

        // ======================================================================
        // Library -> view
        // ======================================================================
        void OnLibraryChanged()
        {
            RebuildFolderList();
            RebuildTypeChips();
            Refresh();
        }

        void Refresh(bool scrollTop = false)
        {
            IEnumerable<VideoItem> q = _library.Items;

            if (_folderFilter != null)
            {
                var f = _folderFilter.EndsWith("\\") ? _folderFilter : _folderFilter + "\\";
                q = q.Where(i => i.Path.StartsWith(f, StringComparison.OrdinalIgnoreCase));
            }

            if (_search.Length > 0)
            {
                var tokens = _search.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                q = q.Where(i => tokens.All(t =>
                    i.Name.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    i.DirectoryName.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0));
            }

            if (_types.Count > 0) q = q.Where(i => _types.Contains(i.Extension));

            switch (_length)
            {
                case "short": q = q.Where(i => i.HasMeta && i.DurationMs < 60_000); break;
                case "medium": q = q.Where(i => i.DurationMs >= 60_000 && i.DurationMs < 300_000); break;
                case "long": q = q.Where(i => i.DurationMs >= 300_000 && i.DurationMs < 1_200_000); break;
                case "xl": q = q.Where(i => i.DurationMs >= 1_200_000); break;
            }
            switch (_shape)
            {
                case "landscape": q = q.Where(i => i.IsLandscape); break;
                case "portrait": q = q.Where(i => i.IsPortrait); break;
                case "square": q = q.Where(i => i.IsSquare); break;
            }
            var now = DateTime.Now;
            switch (_date)
            {
                case "today": q = q.Where(i => i.Date >= now.Date); break;
                case "week": q = q.Where(i => i.Date >= now.AddDays(-7)); break;
                case "month": q = q.Where(i => i.Date >= now.AddDays(-30)); break;
                case "year": q = q.Where(i => i.Date.Year == now.Year); break;
            }

            switch (_settings.Sort)
            {
                case "Oldest": q = q.OrderBy(i => i.Date); break;
                case "NameAsc": q = q.OrderBy(i => i.Name, NaturalComparer.Instance); break;
                case "NameDesc": q = q.OrderByDescending(i => i.Name, NaturalComparer.Instance); break;
                case "Largest": q = q.OrderByDescending(i => i.Size); break;
                case "Smallest": q = q.OrderBy(i => i.Size); break;
                case "Longest": q = q.OrderByDescending(i => i.DurationMs); break;
                case "Shortest": q = q.OrderBy(i => i.DurationMs < 0 ? long.MaxValue : i.DurationMs); break;
                default: q = q.OrderByDescending(i => i.Date); break;
            }

            _view = q.ToList();
            BuildRows(scrollTop);
            UpdateHeader();
        }

        void BuildRows(bool scrollTop)
        {
            var scroller = GetScroller();
            double fraction = 0;
            if (!scrollTop && scroller != null && scroller.ExtentHeight > 0)
                fraction = scroller.VerticalOffset / scroller.ExtentHeight;

            var rows = new List<VideoRow>((_view.Count + _columns - 1) / Math.Max(1, _columns));
            for (int i = 0; i < _view.Count; i += _columns)
                rows.Add(new VideoRow(_view.GetRange(i, Math.Min(_columns, _view.Count - i)), _columns));
            VideoGrid.ItemsSource = rows;

            if (scroller != null)
            {
                if (scrollTop) scroller.ScrollToTop();
                else if (fraction > 0)
                    Dispatcher.BeginInvoke(new Action(() => scroller.ScrollToVerticalOffset(fraction * scroller.ExtentHeight)), DispatcherPriority.Loaded);
            }
        }

        ScrollViewer GetScroller()
        {
            VideoGrid.ApplyTemplate();
            return VideoGrid.Template?.FindName("PART_Scroller", VideoGrid) as ScrollViewer;
        }

        void RecalcColumns()
        {
            double avail = VideoGrid.ActualWidth - 30 - 12;
            if (avail <= 0) return;
            int cols = Math.Max(1, (int)Math.Floor(avail / Math.Max(150, SizeSlider.Value)));
            if (cols != _columns)
            {
                _columns = cols;
                BuildRows(false);
            }
        }

        void UpdateHeader()
        {
            int total = _library.Items.Count;
            AllCount.Text = total.ToString("N0");
            HeadingText.Text = _folderFilter == null ? "All videos" : Path.GetFileName(_folderFilter.TrimEnd('\\')) is string n && n.Length > 0 ? n : _folderFilter;

            long bytes = 0;
            foreach (var v in _view) bytes += v.Size;
            int scope = _folderFilter == null ? total : _library.CountIn(_folderFilter);
            string count = _view.Count == scope
                ? $"{_view.Count:N0} video{(_view.Count == 1 ? "" : "s")}"
                : $"{_view.Count:N0} of {scope:N0} videos";
            CountText.Text = _view.Count > 0 ? $"{count}  ·  {Format.Bytes(bytes)}" : count;

            bool hasFolders = _library.Folders.Count > 0;
            EmptyState.Visibility = hasFolders || total > 0 ? Visibility.Collapsed : Visibility.Visible;
            NoResults.Visibility = (hasFolders || total > 0) && _view.Count == 0 && !_library.IsScanning ? Visibility.Visible : Visibility.Collapsed;
            VideoGrid.Visibility = EmptyState.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

            bool filtered = _types.Count > 0 || _length != "any" || _shape != "any" || _date != "any";
            ResetFiltersButton.Visibility = filtered ? Visibility.Visible : Visibility.Collapsed;
        }

        // ======================================================================
        // Sidebar
        // ======================================================================
        void RebuildFolderList()
        {
            FolderList.Children.Clear();
            foreach (var folder in _library.Folders)
            {
                var name = Path.GetFileName(folder.TrimEnd('\\'));
                if (string.IsNullOrEmpty(name)) name = folder;

                var remove = new Button
                {
                    Style = (Style)FindResource("IconButton"),
                    Content = "",
                    FontSize = 10,
                    Width = 24,
                    Height = 24,
                    Opacity = 0,
                    Margin = new Thickness(6, 0, -4, 0),
                    ToolTip = "Remove from library (files are not touched)"
                };
                var count = new TextBlock
                {
                    Text = _library.CountIn(folder).ToString("N0"),
                    FontSize = 12,
                    Foreground = (Brush)FindResource("MutedTextBrush"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var icon = new TextBlock { Style = (Style)FindResource("Glyph"), Text = "", FontSize = 15, Margin = new Thickness(2, 0, 12, 0) };
                var label = new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(label, 1); Grid.SetColumn(count, 2); Grid.SetColumn(remove, 3);
                grid.Children.Add(icon); grid.Children.Add(label); grid.Children.Add(count); grid.Children.Add(remove);

                var nav = new ToggleButton
                {
                    Style = (Style)FindResource("NavItem"),
                    Content = grid,
                    ToolTip = folder,
                    IsChecked = string.Equals(_folderFilter, folder, StringComparison.OrdinalIgnoreCase),
                    Tag = folder
                };
                nav.MouseEnter += (s, e) => remove.Opacity = 1;
                nav.MouseLeave += (s, e) => remove.Opacity = 0;
                nav.Click += (s, e) => SelectFolder(folder);
                var f = folder;
                remove.Click += (s, e) =>
                {
                    e.Handled = true;
                    if (string.Equals(_folderFilter, f, StringComparison.OrdinalIgnoreCase)) _folderFilter = null;
                    _library.RemoveFolder(f);
                    SaveSettingsSoon();
                    ShowToast($"Removed “{name}” from the library");
                };
                FolderList.Children.Add(nav);
            }
            AllNav.IsChecked = _folderFilter == null;
        }

        void SelectFolder(string folder)
        {
            _folderFilter = folder;
            foreach (ToggleButton b in FolderList.Children)
                b.IsChecked = string.Equals(b.Tag as string, folder, StringComparison.OrdinalIgnoreCase);
            AllNav.IsChecked = folder == null;
            Refresh(scrollTop: true);
        }

        void AllNav_Click(object sender, RoutedEventArgs e) => SelectFolder(null);

        void BuildChips(WrapPanel host, (string Key, string Label)[] options, Func<string> get, Action<string> set)
        {
            host.Children.Clear();
            foreach (var o in options)
            {
                var chip = new ToggleButton { Style = (Style)FindResource("Chip"), Content = o.Label, Tag = o.Key, IsChecked = get() == o.Key };
                chip.Click += (s, e) =>
                {
                    set(o.Key);
                    foreach (ToggleButton c in host.Children) c.IsChecked = (string)c.Tag == o.Key;
                    Refresh(scrollTop: true);
                };
                host.Children.Add(chip);
            }
        }

        void RebuildTypeChips()
        {
            var groups = _library.Items.GroupBy(i => i.Extension).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
            var sig = string.Join(",", groups);
            if (sig == _typeSignature && TypeChips.Children.Count > 0) return;
            _typeSignature = sig;
            _types.RemoveWhere(t => !groups.Contains(t));

            TypeChips.Children.Clear();
            var all = new ToggleButton { Style = (Style)FindResource("Chip"), Content = "All", IsChecked = _types.Count == 0 };
            all.Click += (s, e) =>
            {
                _types.Clear();
                foreach (ToggleButton c in TypeChips.Children) c.IsChecked = c == all;
                Refresh(scrollTop: true);
            };
            TypeChips.Children.Add(all);
            foreach (var ext in groups)
            {
                var chip = new ToggleButton { Style = (Style)FindResource("Chip"), Content = ext.Length > 0 ? ext : "?", Tag = ext, IsChecked = _types.Contains(ext) };
                chip.Click += (s, e) =>
                {
                    if (chip.IsChecked == true) _types.Add(ext); else _types.Remove(ext);
                    all.IsChecked = _types.Count == 0;
                    Refresh(scrollTop: true);
                };
                TypeChips.Children.Add(chip);
            }
        }

        void ResetFilters_Click(object sender, RoutedEventArgs e) => ResetFilters();

        void ResetFilters()
        {
            _types.Clear(); _length = "any"; _shape = "any"; _date = "any";
            _typeSignature = "";
            RebuildTypeChips();
            BuildChips(LengthChips, LengthOptions, () => _length, v => _length = v);
            BuildChips(ShapeChips, ShapeOptions, () => _shape, v => _shape = v);
            BuildChips(DateChips, DateOptions, () => _date, v => _date = v);
            Refresh(scrollTop: true);
        }

        void ClearAll_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = "";
            _search = "";
            ResetFilters();
        }

        void UpdateStatus()
        {
            string text = null;
            if (_library.IsScanning) text = "Scanning folders…";
            else if (_pending > 0) text = $"Preparing previews  ·  {_pending:N0} left";
            if (text == null)
            {
                StatusBox.Visibility = Visibility.Collapsed;
                StatusDot.BeginAnimation(OpacityProperty, null);
            }
            else
            {
                StatusText.Text = text;
                if (StatusBox.Visibility != Visibility.Visible)
                {
                    StatusBox.Visibility = Visibility.Visible;
                    StatusDot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
                }
            }
            if (!_library.IsScanning) UpdateHeader();
        }

        // ======================================================================
        // Folders
        // ======================================================================
        void AddFolder_Click(object sender, RoutedEventArgs e)
        {
            var picked = Native.PickFolders(this, "Choose folders with videos");
            AddFolders(picked);
        }

        void AddFolders(IEnumerable<string> folders)
        {
            int added = 0;
            string last = null;
            foreach (var f in folders)
                if (_library.AddFolder(f)) { added++; last = f; }
            if (added == 0) return;
            RebuildFolderList();
            UpdateHeader();
            UpdateStatus();
            SaveSettingsSoon();
            ShowToast(added == 1 ? $"Added “{Path.GetFileName(last.TrimEnd('\\'))}” — watching for changes" : $"Added {added} folders — watching for changes");
        }

        void SaveSettingsSoon()
        {
            _settings.Folders = _library.Folders.ToList();
            Json.Save(AppPaths.SettingsFile, _settings);
        }

        void Rescan_Click(object sender, RoutedEventArgs e)
        {
            _library.RescanAll();
            UpdateStatus();
        }

        // ======================================================================
        // Cards
        // ======================================================================
        void Card_MouseDown(object sender, MouseButtonEventArgs e)
        {
            _pressed = (sender as FrameworkElement)?.DataContext as VideoItem;
        }

        void Card_MouseUp(object sender, MouseButtonEventArgs e)
        {
            var item = (sender as FrameworkElement)?.DataContext as VideoItem;
            if (item != null && item == _pressed) OpenPlayer(item);
            _pressed = null;
        }

        void Card_RightClick(object sender, MouseButtonEventArgs e)
        {
            var el = sender as FrameworkElement;
            if (!(el?.DataContext is VideoItem item)) return;
            var menu = new ContextMenu { PlacementTarget = el, Placement = PlacementMode.MousePoint };
            menu.Items.Add(MakeMenuItem("", "Play", () => OpenPlayer(item)));
            menu.Items.Add(MakeMenuItem("", "Show in folder", () => Reveal(item.Path)));
            menu.Items.Add(MakeMenuItem("", "Open with default app", () => { try { Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true }); } catch { } }));
            menu.Items.Add(MakeMenuItem("", "Copy path", () => { try { Clipboard.SetText(item.Path); ShowToast("Path copied"); } catch { } }));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeMenuItem("", "Refresh thumbnail", () =>
            {
                try { File.Delete(Path.Combine(AppPaths.Thumbs, item.CacheKey + ".jpg")); } catch { }
                item.DurationMs = -1;
                Thumbnails.Invalidate(item);
            }));
            menu.IsOpen = true;
            e.Handled = true;
        }

        MenuItem MakeMenuItem(string glyph, string text, Action click, bool isChecked = false)
        {
            var mi = new MenuItem
            {
                Header = text,
                Icon = new TextBlock { Text = glyph, Style = (Style)FindResource("MenuGlyph") },
                IsChecked = isChecked
            };
            mi.Click += (s, e) => click();
            return mi;
        }

        static void Reveal(string path)
        {
            try { Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch { }
        }

        // ======================================================================
        // Player
        // ======================================================================
        void OpenPlayer(VideoItem item)
        {
            int idx = _view.IndexOf(item);
            bool wasOpen = Player.IsOpen;
            Player.Open(item, idx > 0, idx >= 0 && idx < _view.Count - 1);
            if (!wasOpen)
            {
                SearchHost.Visibility = Visibility.Collapsed;
                var blur = new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
                ShellGrid.Effect = blur;
                blur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(22, TimeSpan.FromMilliseconds(260)));
            }
            Title = item.Name + " — Video Viewer";
        }

        void PlayFile(string path)
        {
            var existing = _library.Items.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
            if (existing != null) { OpenPlayer(existing); return; }
            var fi = new FileInfo(path);
            OpenPlayer(new VideoItem(path) { Size = fi.Length, Modified = fi.LastWriteTime, Date = fi.LastWriteTime });
        }

        void ClosePlayer()
        {
            if (_isFullscreen) ToggleFullscreen();
            Player.Close();
            ShellGrid.Effect = null;
            SearchHost.Visibility = Visibility.Visible;
            Title = "Video Viewer";
            Activate();
        }

        void Step(int dir)
        {
            var cur = Player.Current;
            if (cur == null) return;
            int idx = _view.IndexOf(cur);
            if (idx < 0) return;
            int next = idx + dir;
            if (next < 0 || next >= _view.Count) return;
            OpenPlayer(_view[next]);
        }

        void ToggleFullscreen()
        {
            var chrome = WindowChrome.GetWindowChrome(this);
            if (!_isFullscreen)
            {
                _isFullscreen = true;
                _preFsState = WindowState;
                _preFsBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;

                chrome.CaptionHeight = 0;
                chrome.ResizeBorderThickness = new Thickness(0);
                CaptionBar.Visibility = Visibility.Collapsed;
                WindowState = WindowState.Normal;
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;

                var screen = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
                var b = screen.Bounds;
                var src = PresentationSource.FromVisual(this);
                var m = src?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
                var tl = m.Transform(new Point(b.Left, b.Top));
                var br = m.Transform(new Point(b.Right, b.Bottom));
                Left = tl.X; Top = tl.Y; Width = br.X - tl.X; Height = br.Y - tl.Y;
                Topmost = true;
                RootGrid.Margin = new Thickness(0);
                Player.SetFullscreen(true);
            }
            else
            {
                _isFullscreen = false;
                Topmost = false;
                WindowStyle = WindowStyle.SingleBorderWindow;
                ResizeMode = ResizeMode.CanResize;
                chrome.CaptionHeight = 52;
                chrome.ResizeBorderThickness = new Thickness(6);
                CaptionBar.Visibility = Visibility.Visible;
                if (!_preFsBounds.IsEmpty)
                {
                    Left = _preFsBounds.Left; Top = _preFsBounds.Top; Width = _preFsBounds.Width; Height = _preFsBounds.Height;
                }
                WindowState = _preFsState;
                Player.SetFullscreen(false);
                UpdateMaximizedLayout();
            }
        }

        // ======================================================================
        // Window chrome
        // ======================================================================
        void UpdateMaximizedLayout()
        {
            if (_isFullscreen) return;
            bool max = WindowState == WindowState.Maximized;
            RootGrid.Margin = max ? new Thickness(7) : new Thickness(0);
            MaxButton.Content = max ? "" : "";
        }

        void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        void Maximize_Click(object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

        // ======================================================================
        // Top bar
        // ======================================================================
        void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (ClearSearchButton == null) return;
            ClearSearchButton.Visibility = SearchBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _searchDebounce.Stop();
            _searchDebounce.Start();
        }

        void ClearSearch_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = "";
            SearchBox.Focus();
        }

        void Sort_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu { PlacementTarget = SortButton, Placement = PlacementMode.Bottom, HorizontalOffset = -4, VerticalOffset = 6 };
            foreach (var o in SortOptions)
            {
                var key = o.Key;
                var mi = new MenuItem { Header = o.Label, IsChecked = _settings.Sort == key };
                mi.Click += (s, a) =>
                {
                    _settings.Sort = key;
                    SortText.Text = o.Label;
                    Refresh(scrollTop: true);
                };
                menu.Items.Add(mi);
            }
            menu.IsOpen = true;
        }

        void SizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_settings == null) return;
            _settings.CardWidth = e.NewValue;
            RecalcColumns();
        }

        void VideoGrid_SizeChanged(object sender, SizeChangedEventArgs e) => RecalcColumns();

        // ======================================================================
        // Keyboard
        // ======================================================================
        void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Player.IsOpen)
            {
                if (Player.HandleKey(e.Key)) e.Handled = true;
                return;
            }
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (ctrl && e.Key == Key.F) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
            else if (ctrl && e.Key == Key.O) { AddFolder_Click(null, null); e.Handled = true; }
            else if (ctrl && (e.Key == Key.OemPlus || e.Key == Key.Add)) { SizeSlider.Value += 40; e.Handled = true; }
            else if (ctrl && (e.Key == Key.OemMinus || e.Key == Key.Subtract)) { SizeSlider.Value -= 40; e.Handled = true; }
            else if (e.Key == Key.F5) { Rescan_Click(null, null); e.Handled = true; }
            else if (e.Key == Key.Escape && (SearchBox.IsKeyboardFocused || SearchBox.Text.Length > 0))
            {
                SearchBox.Text = "";
                Keyboard.ClearFocus();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && SearchBox.IsKeyboardFocused)
            {
                _searchDebounce.Stop();
                _search = SearchBox.Text.Trim();
                Refresh(scrollTop: true);
                if (_view.Count > 0) OpenPlayer(_view[0]);
                e.Handled = true;
            }
        }

        // ======================================================================
        // Drag & drop
        // ======================================================================
        void OnDragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                DropHint.Visibility = Visibility.Visible;
            }
            else e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        void OnDrop(object sender, DragEventArgs e)
        {
            DropHint.Visibility = Visibility.Collapsed;
            if (!(e.Data.GetData(DataFormats.FileDrop) is string[] paths)) return;
            var dirs = paths.Where(Directory.Exists).ToList();
            if (dirs.Count > 0) AddFolders(dirs);
            var file = paths.FirstOrDefault(p => File.Exists(p) && Library.IsVideo(p));
            if (file != null && dirs.Count == 0) PlayFile(file);
        }

        // ======================================================================
        // Toast
        // ======================================================================
        void ShowToast(string text)
        {
            ToastText.Text = text;
            Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
            _toastTimer.Stop();
            _toastTimer.Start();
        }
    }

    // ==========================================================================
    public sealed class VideoRow
    {
        public VideoRow(List<VideoItem> items, int columns) { Items = items; Columns = columns; }
        public List<VideoItem> Items { get; }
        public int Columns { get; }
    }

    public sealed class SixteenNineConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is double d && d > 0 ? Math.Round(d * 9.0 / 16.0) : 120.0;
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>Sorts "clip 2" before "clip 10".</summary>
    public sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new NaturalComparer();
        [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int StrCmpLogicalW(string a, string b);
        public int Compare(string x, string y) => StrCmpLogicalW(x ?? "", y ?? "");
    }
}
