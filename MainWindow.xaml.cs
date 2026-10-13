using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace SpotifyIsland
{
    public partial class MainWindow : Window
    {
        // ---------- Settings you can tweak ----------
        private const string TargetApp = "Spotify";        // matched against the app's ID. "" = any media app
        private const bool ShowWeatherWhenIdle = true;     // true: notch stays visible with the weather when no music plays
        private const double ManualLatitude = double.NaN;  // e.g. 50.95  (NaN = detect automatically)
        private const double ManualLongitude = double.NaN; // e.g. 3.55

        private const double CompactW = 220, CompactH = 38, CompactR = 18;
        private const double ExpandedW = 420, ExpandedH = 148, ExpandedR = 32;
        private const double HiddenW = 140, HiddenH = 4, HiddenR = 2;
        private const double EarSize = 14;                 // curved flare at the top-left / top-right
        private const double CollapseDelaySeconds = 0.6;   // after the mouse leaves
        private const double PausedHideSeconds = 20;       // after pausing, fall back to weather / hide
        // --------------------------------------------

        private enum IslandState { Hidden, Compact, Expanded }
        private enum Mode { None, Music, Weather }
        private enum PageKind { Music, Weather }

        private IslandState _state = IslandState.Hidden;
        private Mode _mode = Mode.None;
        private PageKind _page = PageKind.Music;

        private GlobalSystemMediaTransportControlsSessionManager _manager;
        private GlobalSystemMediaTransportControlsSession _session;

        private readonly WeatherService _weather;

        private bool _playing;
        private bool _musicActive;   // a Spotify session exists and is playing (or paused only recently)
        private bool _hovering;
        private string _trackKey;

        private Color _accent;
        private Color _musicAccent;
        private Color _weatherAccent;
        private Color _glowColor, _midColor;
        private readonly SolidColorBrush _fillBrush = new SolidColorBrush(Colors.White);

        // Timeline snapshot. Spotify only reports position on changes, so we
        // remember it and compute the live position ourselves.
        private TimeSpan _start, _end, _pos;
        private DateTimeOffset _stamp = DateTimeOffset.Now;

        // Timers only run when needed.
        private readonly DispatcherTimer _progressTimer = new DispatcherTimer();
        private readonly DispatcherTimer _collapseTimer = new DispatcherTimer();
        private readonly DispatcherTimer _hideTimer = new DispatcherTimer();
        private readonly DispatcherTimer _feedTimer = new DispatcherTimer();   // weather: every 10 min
        private readonly DispatcherTimer _rainTimer = new DispatcherTimer();   // rain forecast: every 5 min

        private static readonly string[] DayNames = { "zo", "ma", "di", "wo", "do", "vr", "za" };

        private static Brush MakeBrush(byte a, byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            br.Freeze();
            return br;
        }
        private static readonly Brush DimBrush = MakeBrush(0xB3, 255, 255, 255);
        private static readonly Brush DotDim = MakeBrush(0x55, 255, 255, 255);
        private static readonly Brush RainBlue = MakeBrush(0xFF, 0x5B, 0xB5, 0xFF);
        private static readonly Brush RainFaint = MakeBrush(0x33, 255, 255, 255);
        private static readonly Brush TempCold = MakeBrush(0xFF, 143, 211, 255);
        private static readonly Brush TempWarm = MakeBrush(0xFF, 255, 211, 138);
        private static readonly Brush TempHot = MakeBrush(0xFF, 255, 154, 98);

        // Animatable corner radius (only the bottom corners are rounded)
        public static readonly DependencyProperty IslandRadiusProperty =
            DependencyProperty.Register(nameof(IslandRadius), typeof(double), typeof(MainWindow),
                new PropertyMetadata(HiddenR, (d, e) => ((MainWindow)d).UpdateClip()));

        public double IslandRadius
        {
            get => (double)GetValue(IslandRadiusProperty);
            set => SetValue(IslandRadiusProperty, value);
        }

        public MainWindow()
        {
            InitializeComponent();

            _weather = new WeatherService(ManualLatitude, ManualLongitude);

            FillBar.Background = _fillBrush;
            CompactFillBar.Background = _fillBrush;
            Island.SizeChanged += (_, _) => UpdateClip();

            // Create the window handle now (without showing) so we can set window styles.
            new WindowInteropHelper(this).EnsureHandle();

            PositionWindow();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged +=
                (_, _) => Dispatcher.InvokeAsync(PositionWindow);

            InstallFullscreenWatcher();
            CheckFullscreen();

            _progressTimer.Tick += (_, _) => UpdateProgress();
            _collapseTimer.Tick += (_, _) =>
            {
                _collapseTimer.Stop();
                if (!_hovering && _state == IslandState.Expanded) SetState(IslandState.Compact);
            };
            _hideTimer.Interval = TimeSpan.FromSeconds(PausedHideSeconds);
            _hideTimer.Tick += (_, _) =>
            {
                _hideTimer.Stop();
                if (!_playing && !_hovering)
                {
                    _musicActive = false;
                    UpdateMode();
                }
            };

            _ = InitAsync();
            _ = InitWeatherAsync();
        }

        // ================= Window setup =================

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;  // hide from Alt+Tab
        private const int WS_EX_NOACTIVATE = 0x08000000;  // never steal focus

        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        }

        private void PositionWindow()
        {
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = 0;
        }

        // ================= Fullscreen detection (event-driven) =================
        //
        // The notch hides while a real full-screen app is in front (YouTube fullscreen, games, video
        // players, presentations) and comes back when you leave it. A normal maximized window,
        // like a maximized browser, does NOT count: it still has a title bar and leaves the taskbar visible.

        private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        private const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
        private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
        private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
        private const int GWL_STYLE = -16;
        private const int WS_CAPTION = 0x00C00000;      // title bar
        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const uint MONITORINFOF_PRIMARY = 1;

        private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild,
                                           uint thread, uint time);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

        [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax,
            IntPtr hmodWinEventProc, WinEventProc proc, uint idProcess, uint idThread, uint flags);
        [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder text, int maxCount);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        private WinEventProc _winEventProc;               // kept in a field so it isn't garbage collected
        private IntPtr _hookForeground, _hookLocation;
        private readonly DispatcherTimer _fsTimer = new DispatcherTimer();
        private bool _suppressed;                          // true while a full-screen app is in front

        private void InstallFullscreenWatcher()
        {
            // Wait a moment after the last event, so things like window animations don't cause flicker.
            _fsTimer.Interval = TimeSpan.FromMilliseconds(250);
            _fsTimer.Tick += (_, _) => { _fsTimer.Stop(); CheckFullscreen(); };

            _winEventProc = (hook, evt, hwnd, idObject, idChild, thread, time) =>
            {
                if (idObject != 0) return; // only whole windows (not caret, cursor, menus ...)
                if (evt == EVENT_OBJECT_LOCATIONCHANGE && hwnd != GetForegroundWindow()) return;
                _fsTimer.Stop();
                _fsTimer.Start();
            };

            uint flags = WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS;
            _hookForeground = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_MINIMIZEEND,
                IntPtr.Zero, _winEventProc, 0, 0, flags);
            _hookLocation = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE,
                IntPtr.Zero, _winEventProc, 0, 0, flags);
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_hookForeground != IntPtr.Zero) UnhookWinEvent(_hookForeground);
            if (_hookLocation != IntPtr.Zero) UnhookWinEvent(_hookLocation);
            base.OnClosed(e);
        }

        private void CheckFullscreen()
        {
            bool fs = IsFullscreenForeground() || ExclusiveFullscreen();
            if (fs == _suppressed) return;

            _suppressed = fs;
            if (fs)
            {
                _hovering = false;
                _collapseTimer.Stop();
            }
            UpdateMode(); // hides (with animation) or brings the notch back
        }

        // Shell says an exclusive-mode Direct3D game is running.
        private static bool ExclusiveFullscreen()
        {
            return SHQueryUserNotificationState(out int s) == 0 && s == 3;
        }

        private static bool IsFullscreenForeground()
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            if (hwnd == GetShellWindow() || hwnd == GetDesktopWindow()) return false;

            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == (uint)Environment.ProcessId) return false;

            // The desktop and taskbar also cover the screen but are not "full-screen apps".
            var cls = new StringBuilder(64);
            GetClassName(hwnd, cls, cls.Capacity);
            string name = cls.ToString();
            if (name == "Progman" || name == "WorkerW" || name == "Shell_TrayWnd" || name == "Shell_SecondaryTrayWnd")
                return false;

            if (!GetWindowRect(hwnd, out RECT r)) return false;

            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref mi)) return false;

            // The notch lives on the primary monitor, so only react to full-screen apps there.
            if ((mi.dwFlags & MONITORINFOF_PRIMARY) == 0) return false;

            // Must cover the whole monitor, taskbar included (a maximized window leaves the taskbar visible) ...
            const int tol = 4;
            bool covers = r.Left <= mi.rcMonitor.Left + tol && r.Top <= mi.rcMonitor.Top + tol &&
                          r.Right >= mi.rcMonitor.Right - tol && r.Bottom >= mi.rcMonitor.Bottom - tol;
            if (!covers) return false;

            // ... and have no title bar (maximized windows, even with an auto-hiding taskbar, always do).
            int style = GetWindowLong(hwnd, GWL_STYLE);
            return (style & WS_CAPTION) != WS_CAPTION;
        }

        // ================= Weather =================

        private async Task InitWeatherAsync()
        {
            try
            {
                await _weather.ResolveLocationAsync();
                await _weather.RefreshFeedAsync();
                await _weather.RefreshRainAsync();
                OnWeatherUpdated();
            }
            catch { }

            _feedTimer.Interval = TimeSpan.FromMinutes(10);
            _feedTimer.Tick += async (_, _) =>
            {
                try { await _weather.RefreshFeedAsync(); OnWeatherUpdated(); } catch { }
            };

            _rainTimer.Interval = TimeSpan.FromMinutes(5);
            _rainTimer.Tick += async (_, _) =>
            {
                try { await _weather.RefreshRainAsync(); OnWeatherUpdated(); } catch { }
            };

            _feedTimer.Start();
            _rainTimer.Start();

            // After sleep: wait for the network, re-detect the location and refresh.
            Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) =>
            {
                if (e.Mode == Microsoft.Win32.PowerModes.Resume)
                    Dispatcher.InvokeAsync(() => { _ = ResumeRefreshAsync(); });
            };
        }

        private async Task ResumeRefreshAsync()
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(6));
                await _weather.ResolveLocationAsync();
                await _weather.RefreshFeedAsync();
                await _weather.RefreshRainAsync();
                OnWeatherUpdated();
            }
            catch { }
        }

        private void OnWeatherUpdated()
        {
            var d = _weather.Data;
            if (d != null) ApplyWeatherToUi(d);
            UpdateMode();
        }

        private static string Deg(double t)
            => ((int)Math.Round(t, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture) + "°";

        private static Brush TempBrush(double t)
        {
            if (t < 5) return TempCold;
            if (t < 16) return Brushes.White;
            if (t < 25) return TempWarm;
            return TempHot;
        }

        private void ApplyWeatherToUi(WeatherData d)
        {
            var kind = WeatherIcons.FromCode(d.IconCode, d.IsNight, d.Description);
            var icon = WeatherIcons.Get(kind);
            _weatherAccent = WeatherIcons.Accent(kind);

            WxIcon.Source = icon;
            CwIcon.Source = icon;
            MiniIcon.Source = icon;

            string temp = Deg(d.Temperature);
            WxTemp.Text = temp;
            CwTemp.Text = temp;
            MiniTemp.Text = temp;
            WxTemp.Foreground = TempBrush(d.Temperature);

            string desc = d.Description ?? "";
            WxDesc.Text = desc;
            WxFeel.Text = d.FeelTemperature.HasValue ? $"Voelt als {Deg(d.FeelTemperature.Value)}" : "";
            WxWind.Text = d.WindKmh.HasValue
                ? $"{Math.Round(d.WindKmh.Value).ToString(CultureInfo.InvariantCulture)} km/u {d.WindDirection}".Trim()
                : "";

            // compact line: rain warning if there is rain coming, otherwise the description
            CwText.Text = _weather.RainComing && !string.IsNullOrEmpty(_weather.RainText)
                ? _weather.RainText
                : desc;

            BuildDays(d);
            BuildRain();
            RefreshAccent();
        }

        private void BuildDays(WeatherData d)
        {
            DaysGrid.Children.Clear();
            foreach (var day in d.Days.Take(5))
            {
                var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };

                string label = day.Date.Date == DateTime.Today ? "Vandaag" : DayNames[(int)day.Date.DayOfWeek];
                col.Children.Add(new TextBlock
                {
                    Text = label, FontSize = 10, Foreground = DimBrush,
                    HorizontalAlignment = HorizontalAlignment.Center
                });

                var kind = WeatherIcons.FromCode(day.Code, false, day.Description);
                col.Children.Add(new Image
                {
                    Source = WeatherIcons.Get(kind), Width = 24, Height = 24,
                    Margin = new Thickness(0, 3, 0, 2)
                });

                col.Children.Add(new TextBlock
                {
                    Text = Deg(day.Max), FontSize = 13, FontWeight = FontWeights.SemiBold,
                    Foreground = TempBrush(day.Max), HorizontalAlignment = HorizontalAlignment.Center
                });
                col.Children.Add(new TextBlock
                {
                    Text = Deg(day.Min), FontSize = 11, Foreground = DimBrush,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                col.Children.Add(new TextBlock
                {
                    Text = day.RainChance >= 20 ? day.RainChance + "%" : " ",
                    FontSize = 10, Foreground = RainBlue, HorizontalAlignment = HorizontalAlignment.Center
                });

                DaysGrid.Children.Add(col);
            }
        }

        private void BuildRain()
        {
            var rain = _weather.RainMm;
            bool has = rain != null && rain.Length > 0;
            RainCaption.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            RainBars.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            if (!has) return;

            RainCaption.Text = _weather.RainText ?? "";
            RainBars.Children.Clear();
            for (int i = 0; i < 24; i++)
            {
                bool known = i < rain.Length;
                double mm = known ? rain[i] : 0;
                bool wet = known && mm >= 0.1;
                double h = wet ? 4 + Math.Min(1, Math.Sqrt(mm / 4.0)) * 10 : 2;

                RainBars.Children.Add(new Border
                {
                    Width = 6.5,
                    Height = h,
                    CornerRadius = new CornerRadius(2),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Background = wet ? RainBlue : RainFaint
                });
            }
        }

        private void Source_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            try { Process.Start(new ProcessStartInfo("https://www.buienradar.nl") { UseShellExecute = true }); }
            catch { }
        }

        // ================= Mode (music / weather / nothing) =================

        private void UpdateMode()
        {
            Mode desired = _suppressed
                ? Mode.None                      // a full-screen app is in front: stay hidden
                : _musicActive
                    ? Mode.Music
                    : (ShowWeatherWhenIdle && _weather.Data != null ? Mode.Weather : Mode.None);

            _mode = desired;

            if (desired == Mode.None)
            {
                SetState(IslandState.Hidden);
                return;
            }

            if (_state == IslandState.Hidden) SetState(IslandState.Compact);
            ApplyContent();
        }

        // Decides what the compact notch and the pages show.
        private void ApplyContent()
        {
            bool music = _mode == Mode.Music;
            bool weatherAvail = _weather.Data != null;

            Fade(CompactMusic, music ? 1 : 0, 200);
            Fade(CompactWeather, music ? 0 : 1, 200);

            bool mini = music && weatherAvail;
            CompactMiniWeather.Visibility = mini ? Visibility.Visible : Visibility.Collapsed;
            CompactTitle.Margin = new Thickness(44, 0, mini ? 66 : 16, 2);
            PagerDots.Visibility = mini ? Visibility.Visible : Visibility.Collapsed;

            SetPage(_page, _state == IslandState.Expanded);
            RefreshAccent();
        }

        // ================= Pages (music <-> weather) =================

        private void SetPage(PageKind p, bool animate)
        {
            if (_mode != Mode.Music) p = PageKind.Weather;
            else if (_weather.Data == null) p = PageKind.Music;
            _page = p;

            bool toWeather = p == PageKind.Weather;

            ShiftTo(MusicShift, toWeather ? -ExpandedH : 0, animate);
            ShiftTo(WeatherShift, toWeather ? 0 : ExpandedH, animate);
            FadeOrSet(MusicPage, toWeather ? 0 : 1, animate);
            FadeOrSet(WeatherPage, toWeather ? 1 : 0, animate);
            MusicPage.IsHitTestVisible = !toWeather;
            WeatherPage.IsHitTestVisible = toWeather;

            DotMusic.Fill = toWeather ? DotDim : Brushes.White;
            DotWeather.Fill = toWeather ? Brushes.White : DotDim;

            RefreshAccent();
        }

        private static void ShiftTo(TranslateTransform t, double y, bool animate)
        {
            if (animate)
            {
                t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, TimeSpan.FromMilliseconds(380))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            }
            else
            {
                t.BeginAnimation(TranslateTransform.YProperty, null);
                t.Y = y;
            }
        }

        private static void FadeOrSet(UIElement e, double to, bool animate)
        {
            if (animate) Fade(e, to, 250);
            else
            {
                e.BeginAnimation(OpacityProperty, null);
                e.Opacity = to;
            }
        }

        private void DotMusic_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
            => SetPage(PageKind.Music, true);

        private void DotWeather_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
            => SetPage(PageKind.Weather, true);

        private void Island_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (_state != IslandState.Expanded || _mode != Mode.Music || _weather.Data == null) return;
            SetPage(e.Delta < 0 ? PageKind.Weather : PageKind.Music, true);
            e.Handled = true;
        }

        // ================= Media session =================

        private async Task InitAsync()
        {
            try
            {
                _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                _manager.SessionsChanged += (_, _) => Dispatcher.InvokeAsync(AttachSession);
                AttachSession();
            }
            catch { /* media API unavailable */ }
        }

        private void AttachSession()
        {
            var target = _manager.GetSessions().FirstOrDefault(s =>
                s.SourceAppUserModelId.IndexOf(TargetApp, StringComparison.OrdinalIgnoreCase) >= 0);

            if (target?.SourceAppUserModelId == _session?.SourceAppUserModelId) return;

            if (_session != null)
            {
                _session.MediaPropertiesChanged -= OnMediaChanged;
                _session.PlaybackInfoChanged -= OnPlaybackChanged;
                _session.TimelinePropertiesChanged -= OnTimelineChanged;
            }

            _session = target;
            _trackKey = null;

            if (_session == null)
            {
                _playing = false;
                _musicActive = false;
                _progressTimer.Stop();
                _collapseTimer.Stop();
                _hideTimer.Stop();
                UpdateMode();
                return;
            }

            _session.MediaPropertiesChanged += OnMediaChanged;
            _session.PlaybackInfoChanged += OnPlaybackChanged;
            _session.TimelinePropertiesChanged += OnTimelineChanged;

            _ = ApplySessionAsync();
        }

        private async Task ApplySessionAsync()
        {
            UpdateTimeline();
            await UpdateMediaAsync();
            UpdatePlayback();
        }

        // These fire on background threads, so hop to the UI thread.
        private void OnMediaChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs a)
            => Dispatcher.InvokeAsync(() => { _ = UpdateMediaAsync(); });

        private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs a)
            => Dispatcher.InvokeAsync(UpdatePlayback);

        private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs a)
            => Dispatcher.InvokeAsync(() => { UpdateTimeline(); UpdateProgress(); });

        // ================= Updating music data =================

        private async Task UpdateMediaAsync()
        {
            var session = _session;
            if (session == null) return;

            GlobalSystemMediaTransportControlsSessionMediaProperties p;
            try { p = await session.TryGetMediaPropertiesAsync(); }
            catch { return; }
            if (session != _session || p == null) return;

            string key = $"{p.Title}|{p.Artist}|{p.AlbumTitle}";
            bool changed = key != _trackKey;
            _trackKey = key;

            TitleText.Text = p.Title ?? "";
            ArtistText.Text = p.Artist ?? "";
            CompactTitle.Text = string.IsNullOrEmpty(p.Artist) ? p.Title : $"{p.Title} · {p.Artist}";

            // The album art sometimes arrives a moment late, so try on every update.
            var art = await LoadThumbnailAsync(p.Thumbnail);
            if (art != null)
            {
                ArtBrush.ImageSource = art;
                CompactArtBrush.ImageSource = art;
                _musicAccent = ExtractAccent(art);
                RefreshAccent();
            }

            // New song: just reset the progress bar. The notch does NOT expand.
            if (changed)
            {
                UpdateTimeline();
                UpdateProgress();
            }
        }

        private void UpdatePlayback()
        {
            if (_session == null) return;

            var info = _session.GetPlaybackInfo();
            _playing = info != null &&
                       info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            // The button shows the action you'd get by pressing it.
            PlayPauseButton.Content = _playing ? "\uE769" : "\uE768"; // pause / play glyph
            UpdateTimeline();

            if (_playing)
            {
                _musicActive = true;
                _hideTimer.Stop();
            }
            else if (_musicActive && !_hovering)
            {
                _hideTimer.Start(); // after a while the notch falls back to the weather (or hides)
            }

            UpdateMode();

            if (_playing && _mode == Mode.Music && _state != IslandState.Hidden)
                _progressTimer.Start();
            else
            {
                _progressTimer.Stop();
                UpdateProgress();
            }
        }

        private void UpdateTimeline()
        {
            if (_session == null) return;
            var t = _session.GetTimelineProperties();
            if (t == null) return;

            _start = t.StartTime;
            _end = t.EndTime;
            _pos = t.Position;
            _stamp = t.LastUpdatedTime;
            if (_stamp.Year < 2000 || _stamp > DateTimeOffset.Now) _stamp = DateTimeOffset.Now;
        }

        private double CurrentPositionSeconds()
        {
            double pos = _pos.TotalSeconds;
            if (_playing) pos += (DateTimeOffset.Now - _stamp).TotalSeconds;
            double dur = (_end - _start).TotalSeconds;
            if (dur > 0) pos = Math.Min(pos, dur);
            return Math.Max(pos, 0);
        }

        private void UpdateProgress()
        {
            if (_state == IslandState.Hidden || _mode != Mode.Music) return;

            double dur = Math.Max((_end - _start).TotalSeconds, 0);
            double pos = CurrentPositionSeconds();
            double frac = dur > 0 ? Math.Min(pos / dur, 1) : 0;

            FillScale.ScaleX = frac;
            CompactScale.ScaleX = frac;

            if (_state == IslandState.Expanded)
            {
                ElapsedText.Text = Fmt(pos);
                TotalText.Text = Fmt(dur);
            }
        }

        private static string Fmt(double seconds)
        {
            var t = TimeSpan.FromSeconds(seconds);
            return $"{(int)t.TotalMinutes}:{t.Seconds:00}";
        }

        private static async Task<BitmapImage> LoadThumbnailAsync(IRandomAccessStreamReference reference)
        {
            if (reference == null) return null;
            try
            {
                using var ras = await reference.OpenReadAsync();
                using var stream = ras.AsStreamForRead();
                var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                ms.Position = 0;

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 128;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        // ================= Playback controls =================

        private async void Prev_Click(object sender, RoutedEventArgs e)
        {
            var s = _session;
            if (s == null) return;
            try { await s.TrySkipPreviousAsync(); } catch { }
        }

        private async void PlayPause_Click(object sender, RoutedEventArgs e)
        {
            var s = _session;
            if (s == null) return;
            try { await s.TryTogglePlayPauseAsync(); } catch { }
        }

        private async void Next_Click(object sender, RoutedEventArgs e)
        {
            var s = _session;
            if (s == null) return;
            try { await s.TrySkipNextAsync(); } catch { }
        }

        // ================= Lighting (album art / weather) =================

        private bool ShowingWeatherVisuals()
        {
            if (_mode == Mode.Weather) return true;
            return _mode == Mode.Music && _state == IslandState.Expanded && _page == PageKind.Weather;
        }

        private void RefreshAccent()
        {
            ApplyAccent(ShowingWeatherVisuals() ? _weatherAccent : _musicAccent);
        }

        // Picks a vivid representative colour: pixels that are saturated and bright count the most.
        private static Color ExtractAccent(BitmapSource src)
        {
            var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight, stride = w * 4;
            var px = new byte[stride * h];
            conv.CopyPixels(px, stride, 0);

            double r = 0, g = 0, b = 0, total = 0;
            for (int i = 0; i + 3 < px.Length; i += 4)
            {
                double B = px[i], G = px[i + 1], R = px[i + 2];
                double max = Math.Max(R, Math.Max(G, B));
                double min = Math.Min(R, Math.Min(G, B));
                double sat = max > 0 ? (max - min) / max : 0;
                double val = max / 255.0;
                double weight = sat * sat * val + 0.02;
                r += R * weight; g += G * weight; b += B * weight;
                total += weight;
            }
            if (total <= 0) return Colors.Gray;
            return Color.FromRgb((byte)(r / total), (byte)(g / total), (byte)(b / total));
        }

        private void ApplyAccent(Color accent)
        {
            if (accent == _accent) return;
            _accent = accent;

            ToHsv(accent, out double h, out double s, out double v);
            double sat = Math.Min(1, s * 2);

            _glowColor = FromHsv(h, Math.Min(1, s * 1.15), 0.30 + 0.35 * sat); // strong spot behind the art / icon
            _midColor = FromHsv(h, Math.Min(1, s * 1.10), 0.10 + 0.14 * sat);  // soft falloff
            var bar = FromHsv(h, s * 0.45, 1.0);                                // light tint for the progress bar

            ApplyGlow(700);
            _fillBrush.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(bar, TimeSpan.FromMilliseconds(700)));
        }

        // Compact: mostly black, a small dim glow right around the art / icon.
        // Expanded: a bigger, brighter glow.
        private void ApplyGlow(int ms)
        {
            bool expanded = _state == IslandState.Expanded;
            double strongF = expanded ? 1.0 : 0.5;
            double midF = expanded ? 1.0 : 0.3;
            var dur = TimeSpan.FromMilliseconds(ms);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            Glow0.BeginAnimation(GradientStop.ColorProperty,
                new ColorAnimation(Scale(_glowColor, strongF), dur));
            Glow1.BeginAnimation(GradientStop.ColorProperty,
                new ColorAnimation(Scale(_midColor, midF), dur));

            var center = expanded ? new Point(0.17, 0.56) : new Point(0.153, 0.5);
            double rx = expanded ? 0.80 : 0.31;
            double ry = expanded ? 1.2 : 0.9;

            GlowBrush.BeginAnimation(RadialGradientBrush.CenterProperty,
                new PointAnimation(center, dur) { EasingFunction = ease });
            GlowBrush.BeginAnimation(RadialGradientBrush.GradientOriginProperty,
                new PointAnimation(center, dur) { EasingFunction = ease });
            GlowBrush.BeginAnimation(RadialGradientBrush.RadiusXProperty,
                new DoubleAnimation(rx, dur) { EasingFunction = ease });
            GlowBrush.BeginAnimation(RadialGradientBrush.RadiusYProperty,
                new DoubleAnimation(ry, dur) { EasingFunction = ease });
        }

        private static Color Scale(Color c, double f)
            => Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));

        private static void ToHsv(Color c, out double h, out double s, out double v)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            double d = max - min;
            v = max;
            s = max <= 0 ? 0 : d / max;
            if (d <= 0) h = 0;
            else if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
            if (h < 0) h += 360;
        }

        private static Color FromHsv(double h, double s, double v)
        {
            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
            double m = v - c;
            double r, g, b;
            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }
            return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
        }

        // ================= Notch shape (curved ears + rounded bottom) =================

        // Draws the outline: a wide top edge that curves inward (the "ears") into the notch
        // body, which has rounded bottom corners. Using a clip means the glow gradient runs
        // through the whole shape with no seams.
        private void UpdateClip()
        {
            double w = Island.ActualWidth, h = Island.ActualHeight;
            if (w <= 0 || h <= 0) return;

            double e = Math.Min(EarSize, h);
            double r = Math.Min(IslandRadius, Math.Max(0, h - e));
            r = Math.Min(r, Math.Max(0, (w - 2 * e) / 2));

            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(0, 0), true, true);
                ctx.LineTo(new Point(w, 0), true, false);                                   // top edge
                ctx.ArcTo(new Point(w - e, e), new Size(e, e), 0, false,
                          SweepDirection.Counterclockwise, true, false);                    // right ear
                ctx.LineTo(new Point(w - e, h - r), true, false);                           // right side
                ctx.ArcTo(new Point(w - e - r, h), new Size(r, r), 0, false,
                          SweepDirection.Clockwise, true, false);                           // bottom-right
                ctx.LineTo(new Point(e + r, h), true, false);                               // bottom
                ctx.ArcTo(new Point(e, h - r), new Size(r, r), 0, false,
                          SweepDirection.Clockwise, true, false);                           // bottom-left
                ctx.LineTo(new Point(e, e), true, false);                                   // left side
                ctx.ArcTo(new Point(0, 0), new Size(e, e), 0, false,
                          SweepDirection.Counterclockwise, true, false);                    // left ear
            }
            g.Freeze();
            Island.Clip = g;
        }

        // ================= Island states & animation =================

        private void StartCollapseTimer(double seconds)
        {
            _collapseTimer.Stop();
            if (_hovering) return;
            _collapseTimer.Interval = TimeSpan.FromSeconds(seconds);
            _collapseTimer.Start();
        }

        private void SetState(IslandState next)
        {
            if (next == _state) return;

            var prev = _state;

            _state = next;
            ExpandedContent.IsHitTestVisible = next == IslandState.Expanded;

            switch (next)
            {
                case IslandState.Hidden:
                    _progressTimer.Stop();
                    _collapseTimer.Stop();
                    _hideTimer.Stop();
                    AnimateSize(HiddenW, HiddenH, HiddenR, 250, false);
                    Fade(CompactContent, 0, 120);
                    Fade(ExpandedContent, 0, 120);
                    var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
                    fadeOut.Completed += (_, _) => { if (_state == IslandState.Hidden) Hide(); };
                    Island.BeginAnimation(OpacityProperty, fadeOut);
                    break;

                case IslandState.Compact:
                    if (prev == IslandState.Hidden) ShowIsland();
                    _progressTimer.Interval = TimeSpan.FromSeconds(1);
                    AnimateSize(CompactW, CompactH, CompactR, 350, false);
                    Fade(ExpandedContent, 0, 120);
                    Fade(CompactContent, 1, 200, 120);
                    ApplyGlow(350);
                    break;

                case IslandState.Expanded:
                    if (prev == IslandState.Hidden) ShowIsland();
                    _progressTimer.Interval = TimeSpan.FromMilliseconds(500);
                    // always start on the music page when music is playing, otherwise on the weather
                    SetPage(PageKind.Music, false);
                    AnimateSize(ExpandedW, ExpandedH, ExpandedR, 500, true);
                    Fade(CompactContent, 0, 100);
                    Fade(ExpandedContent, 1, 250, 150);
                    ApplyGlow(500);
                    break;
            }

            RefreshAccent();

            if (next != IslandState.Hidden)
            {
                UpdateProgress();
                if (_playing && _mode == Mode.Music) _progressTimer.Start();
                else if (_musicActive && !_playing && !_hovering) _hideTimer.Start();
            }
        }

        private void ShowIsland()
        {
            if (!IsVisible) Show();
            Island.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
        }

        private void AnimateSize(double w, double h, double r, int ms, bool springy)
        {
            IEasingFunction ease = springy
                ? new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut }
                : new CubicEase { EasingMode = EasingMode.EaseOut };
            var dur = TimeSpan.FromMilliseconds(ms);

            Island.BeginAnimation(WidthProperty, new DoubleAnimation(w + 2 * EarSize, dur) { EasingFunction = ease });
            Island.BeginAnimation(HeightProperty, new DoubleAnimation(h, dur) { EasingFunction = ease });
            BeginAnimation(IslandRadiusProperty, new DoubleAnimation(r, dur) { EasingFunction = ease });
        }

        private static void Fade(UIElement element, double to, int ms, int delayMs = 0)
        {
            element.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
            {
                BeginTime = TimeSpan.FromMilliseconds(delayMs)
            });
        }

        // ================= Hover: expand when you mouse over the notch =================

        private void Island_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _hovering = true;
            _collapseTimer.Stop();
            _hideTimer.Stop();
            if (_state == IslandState.Compact) SetState(IslandState.Expanded);
        }

        private void Island_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _hovering = false;
            if (_state == IslandState.Expanded) StartCollapseTimer(CollapseDelaySeconds);
            else if (_musicActive && !_playing && _state != IslandState.Hidden) _hideTimer.Start();
        }
    }
}
