using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using AirTun.App.Services;
using AirTun.Core;
using AirTun.Core.Resolvers;
using AirTun.Core.Geo;
using AirTun.Core.Routing;
using H.NotifyIcon;

namespace AirTun.App;

public sealed partial class MainWindow : Window
{
    private readonly AppController _controller = new();
    private readonly DispatcherTimer _durationTimer = new();
    private DateTimeOffset _connectedStart = DateTimeOffset.UtcNow;
    private AppWindow? _appWindow;
    private TaskbarIcon? _trayIcon;
    private LanDiscovery.Device? _selectedDevice;

    private readonly List<double> _downHistory = new(30);
    private readonly List<double> _upHistory = new(30);
    private double _peakSpeed = 0;

    private readonly Polygon _polygonDownload = new();
    private readonly Polyline _polylineDownload = new();
    private readonly Polyline _polylineUpload = new();

    private const int GWL_STYLE = -16;
    private const int WS_MAXIMIZEBOX = 0x00010000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    public MainWindow()
    {
        this.InitializeComponent();
        this.Title = "AirTun";

        ConfigureWindow(440, 700);
        UpdateTrayIconState();
        InitializeTrafficGraph();


        _controller.StateChanged += OnStateChanged;
        _controller.DevicesChanged += OnDevicesChanged;
        _controller.StatsSampled += OnStatsSampled;
        _controller.GeoLocationUpdated += OnGeoLocationUpdated;
        LocalLog.Changed += OnLogChanged;
        _controller.Routing.RulesChanged += RefreshCustomRulesList;

        _durationTimer.Interval = TimeSpan.FromSeconds(1);
        _durationTimer.Tick += (_, _) => UpdateDuration();

        _controller.RecoverOnStartup();
        _controller.StartDiscovery();

        SwitchBypassDomestic.IsOn = _controller.Settings.BypassDomestic;
        SwitchBypassLan.IsOn = _controller.Settings.BypassLan;
        SwitchCloseToTray.IsOn = _controller.Settings.CloseToTray;
        SwitchMinimizeToTray.IsOn = _controller.Settings.MinimizeToTray;
        SwitchStartWithWindows.IsOn = _controller.Settings.StartWithWindows;

        RefreshCustomRulesList();
        UpdateModeCardsUi();
        ApplyStrings();
        SelectTab(0);

        // Prepopulate waveform with baseline zeros
        for (int i = 0; i < 30; i++)
        {
            _downHistory.Add(0);
            _upHistory.Add(0);
        }

        // Check if launched with --minimized / --autostart
        var args = Environment.GetCommandLineArgs();
        if (_controller.Settings.StartWithWindows && args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase) || a.Equals("--autostart", StringComparison.OrdinalIgnoreCase)))
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _appWindow?.Hide();
            });
        }
        else
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _appWindow?.Show();
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                ShowWindow(hwnd, SW_SHOW);
                SetForegroundWindow(hwnd);
            });
        }
    }

    private void InitializeTrafficGraph()
    {
        _polygonDownload.Fill = (SolidColorBrush)Application.Current.Resources["AccentBrush"];
        _polygonDownload.Opacity = 0.22;

        _polylineDownload.Stroke = (SolidColorBrush)Application.Current.Resources["AccentBrush"];
        _polylineDownload.StrokeThickness = 2.2;

        _polylineUpload.Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 56, 189, 248));
        _polylineUpload.StrokeThickness = 1.6;

        CanvasTrafficGraph.Children.Add(_polygonDownload);
        CanvasTrafficGraph.Children.Add(_polylineDownload);
        CanvasTrafficGraph.Children.Add(_polylineUpload);
    }

    private void ConfigureWindow(int width, int height)
    {
        var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        try
        {
            if (IntPtr.Size == 8)
            {
                var style = GetWindowLongPtr64(hWnd, GWL_STYLE).ToInt64();
                SetWindowLongPtr64(hWnd, GWL_STYLE, new IntPtr(style & ~WS_MAXIMIZEBOX));
            }
            else
            {
                var style = GetWindowLong32(hWnd, GWL_STYLE);
                SetWindowLong32(hWnd, GWL_STYLE, (int)(style & ~WS_MAXIMIZEBOX));
            }
        }
        catch { }

        if (_appWindow is not null)
        {
            var appIconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(appIconPath))
            {
                try { _appWindow.SetIcon(appIconPath); } catch { }
            }

            uint dpi = 96;
            try { dpi = GetDpiForWindow(hWnd); } catch { }
            if (dpi < 96) dpi = 96;
            double scale = dpi / 96.0;
            int scaledW = (int)Math.Round(width * scale);
            int scaledH = (int)Math.Round(height * scale);

            _appWindow.Resize(new Windows.Graphics.SizeInt32(scaledW, scaledH));
            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
            }

            _appWindow.Closing += (sender, args) =>
            {
                if (_controller.Settings.CloseToTray)
                {
                    args.Cancel = true;
                    _appWindow.Hide();
                    _trayIcon?.ShowNotification("AirTun", "Minimized to system tray. Active in background.");
                }
                else
                {
                    ExitApp();
                }
            };

            _appWindow.Changed += (sender, args) =>
            {
                if (args.DidPresenterChange && _appWindow.Presenter is OverlappedPresenter p)
                {
                    if (p.State == OverlappedPresenterState.Minimized && _controller.Settings.MinimizeToTray)
                    {
                        _appWindow.Hide();
                    }
                }
            };

            // Register title bar drag region using WinUI 3's own input pipeline.
            // This is the correct approach — no Win32 modal loops, no XAML/Win32 pipeline mismatch.
            // AppTitleBar height = 64px (Row 0 in MainWindow.xaml), button area on right ≈ 140px.
            // Drag region covers the full-width title bar area excluding the right-side window buttons.
            var nonClientInput = InputNonClientPointerSource.GetForWindowId(_appWindow.Id);
            _appWindow.Changed += (_, _) => UpdateDragRegion(nonClientInput);
            UpdateDragRegion(nonClientInput);
        }
    }

    private void UpdateDragRegion(InputNonClientPointerSource nonClientInput)
    {
        if (_appWindow is null) return;

        var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        uint dpi = 96;
        try { dpi = GetDpiForWindow(hWnd); } catch { }
        if (dpi < 96) dpi = 96;
        double scale = dpi / 96.0;

        int titleBarHeight = (int)Math.Round(64 * scale);      // Grid Row 0 height in xaml
        int rightButtonsWidth = (int)Math.Round(140 * scale);  // window control buttons area
        int totalWidth = _appWindow.Size.Width;

        int dragWidth = totalWidth - rightButtonsWidth;
        if (dragWidth < 1) dragWidth = 1;

        nonClientInput.SetRegionRects(NonClientRegionKind.Caption, new RectInt32[]
        {
            new RectInt32(0, 0, dragWidth, titleBarHeight)
        });
    }

    private void BtnWinMin_Click(object sender, RoutedEventArgs e)
    {
        if (_controller.Settings.MinimizeToTray)
        {
            _appWindow?.Hide();
        }
        else
        {
            if (_appWindow?.Presenter is OverlappedPresenter presenter)
            {
                presenter.Minimize();
            }
            else
            {
                var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                ShowWindow(hWnd, 6 /* SW_MINIMIZE */);
            }
        }
    }

    private void BtnWinClose_Click(object sender, RoutedEventArgs e)
    {
        if (_controller.Settings.CloseToTray)
        {
            _appWindow?.Hide();
            _trayIcon?.ShowNotification("AirTun", "Minimized to system tray. Active in background.");
        }
        else
        {
            ExitApp();
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;

    private void UpdateTrayIconState()
    {
        bool needsTray = _controller.Settings.CloseToTray
                      || _controller.Settings.MinimizeToTray
                      || _controller.Settings.StartWithWindows;

        if (needsTray)
        {
            if (_trayIcon == null)
            {
                InitializeTray();
            }
        }
        else
        {
            try { _trayIcon?.Dispose(); } catch { }
            _trayIcon = null;
        }
    }

    private void InitializeTray()
    {
        if (_trayIcon != null) return;
        try
        {
            var openItem = new MenuFlyoutItem
            {
                Text = Strings.TrayOpen,
                Command = new RelayCommand(ShowAppWindow)
            };
            openItem.Click += (_, _) => ShowAppWindow();

            var exitItem = new MenuFlyoutItem
            {
                Text = Strings.TrayExit,
                Command = new RelayCommand(ExitApp)
            };
            exitItem.Click += (_, _) => ExitApp();

            var flyout = new MenuFlyout();
            flyout.Items.Add(openItem);
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(exitItem);

            var trayIconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "TrayIcon.ico");
            var icon = File.Exists(trayIconPath)
                ? new System.Drawing.Icon(trayIconPath)
                : System.Drawing.SystemIcons.Shield;

            _trayIcon = new TaskbarIcon
            {
                ToolTipText = "AirTun - Phone Internet Sharing",
                Icon = icon,
                ContextFlyout = flyout,
                NoLeftClickDelay = true,
                LeftClickCommand = new RelayCommand(ToggleAppWindow),
                DoubleClickCommand = new RelayCommand(ShowAppWindow)
            };

            _trayIcon.ForceCreate();
        }
        catch (Exception ex)
        {
            LocalLog.Add($"System Tray notice: {ex.Message}");
        }
    }

    private void ShowAppWindow()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (_appWindow is not null)
                {
                    _appWindow.Show();
                    if (_appWindow.Presenter is OverlappedPresenter p)
                    {
                        p.Restore();
                    }
                }
                this.Activate();
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
            }
            catch (Exception ex)
            {
                LocalLog.Add($"ShowAppWindow: {ex.Message}");
            }
        });
    }

    private void ToggleAppWindow()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_appWindow is not null && _appWindow.IsVisible)
            {
                _appWindow.Hide();
            }
            else
            {
                ShowAppWindow();
            }
        });
    }

    private void ExitApp()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            try { _controller.Disconnect(); } catch { }
            try { _trayIcon?.Dispose(); } catch { }
            try { Application.Current.Exit(); } catch { }
            Environment.Exit(0);
        });
    }

    private sealed class RelayCommand(Action execute) : System.Windows.Input.ICommand
    {
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }

    private void ApplyStrings()
    {
        Root.FlowDirection = FlowDirection.LeftToRight;
        BtnLangToggle.Content = Strings.IsPersian ? "EN" : "FA";

        NavTextConnect.Text = Strings.TabConnect;
        NavTextDns.Text = Strings.TabDns;
        NavTextAi.Text = Strings.TabAi;
        NavTextRouting.Text = Strings.TabRouting;
        NavTextLogs.Text = Strings.TabLogs;
        NavTextAbout.Text = Strings.TabAbout;

        TextDnsTitle.Text = Strings.DnsTitle;
        TextDnsSubtitle.Text = Strings.DnsSubtitle;
        BtnTestAllDns.Content = Strings.DnsTestAll;
        BtnApplyDns.Content = Strings.DnsSet;
        BtnFlushDns.Content = Strings.DnsFlush;
        BtnUnsetDns.Content = Strings.DnsUnset;
        BtnAddDns.Content = Strings.DnsAddCustom;

        TextStatus.Text = Strings.StatusIdle;
        TextTunSub.Text = Strings.ModeTunSubtitle;
        TextProxySub.Text = Strings.ModeProxySubtitle;
        TextStatusMode.Text = _controller.ActiveMode == "tun" ? "⚡ TUN" : "🌐 Proxy";

        // Quick Tips
        TextQuickTipsLabel.Text = Strings.QuickTipsLabel;
        TextTip1.Text = Strings.Tip1;
        TextTip2.Text = Strings.Tip2;
        TextTip3.Text = Strings.Tip3;

        // Routing Tab
        TextRoutingTitle.Text = Strings.RoutingTitle;
        TextRoutingSubtitle.Text = Strings.RoutingSubtitle;
        TextBypassTitle.Text = Strings.BypassDomesticTitle;
        TextBypassDesc.Text = Strings.BypassDomesticDesc;
        TextBypassLanTitle.Text = Strings.BypassLanTitle;
        TextBypassLanDesc.Text = Strings.BypassLanDesc;
        TextCustomRulesHeader.Text = Strings.CustomRulesHeader;
        TextCustomRulesDesc.Text = Strings.CustomRulesDesc;
        InputNewRulePattern.PlaceholderText = Strings.RulePatternPlaceholder;
        BtnAddRule.Content = Strings.AddRuleAction;

        // Connect / Discovery
        TextPinHint.Text = Strings.PinHint;
        BtnConnect.Content = Strings.ConnectAction;
        BtnDisconnect.Content = Strings.DisconnectAction;
        BtnErrorDismiss.Content = Strings.DismissAction;
        BtnErrorRetry.Content = Strings.RetryAction;

        // Logs Tab
        TextLogsHeader.Text = Strings.LogsHeader;
        BtnCopyLogs.Content = Strings.CopyLogsAction;
        BtnClearLogs.Content = Strings.ClearLogsAction;

        // Settings Tab
        TextSettingsHeader.Text = Strings.SettingsHeader;
        TextStartWithWindowsTitle.Text = Strings.StartWithWindowsTitle;
        TextStartWithWindowsDesc.Text = Strings.StartWithWindowsDesc;
        TextCloseToTrayTitle.Text = Strings.CloseToTrayTitle;
        TextCloseToTrayDesc.Text = Strings.CloseToTrayDesc;
        TextMinimizeToTrayTitle.Text = Strings.MinimizeToTrayTitle;
        TextMinimizeToTrayDesc.Text = Strings.MinimizeToTrayDesc;
        TextGithubLabel.Text = Strings.GithubCardTitle;
        BtnOpenGithub.Content = Strings.GithubCardAction;

        TextLiveTrafficHeader.Text = Strings.LiveTrafficHeader;
        var flowDir = Strings.IsPersian ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        TextBypassDesc.FlowDirection = flowDir;
        TextBypassLanDesc.FlowDirection = flowDir;
        TextStartWithWindowsDesc.FlowDirection = flowDir;
        TextCloseToTrayDesc.FlowDirection = flowDir;
        TextMinimizeToTrayDesc.FlowDirection = flowDir;
        TextPinHint.FlowDirection = flowDir;
    }

    private void OnStateChanged(ConnectionState state)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            switch (state)
            {
                case ConnectionState.IdleState or ConnectionState.DiscoveringState:
                    PanelIdle.Visibility = Visibility.Visible;
                    PanelConnected.Visibility = Visibility.Collapsed;
                    PanelError.Visibility = Visibility.Collapsed;
                    TextStatus.Text = Strings.StatusIdle;
                    StatusDot.Fill = (SolidColorBrush)Application.Current.Resources["AccentBrush"];
                    _durationTimer.Stop();
                    break;

                case ConnectionState.PreparingState:
                    PanelIdle.Visibility = Visibility.Visible;
                    PanelConnected.Visibility = Visibility.Collapsed;
                    PanelError.Visibility = Visibility.Collapsed;
                    TextStatus.Text = Strings.StatusPreparing;
                    StatusDot.Fill = (SolidColorBrush)Application.Current.Resources["WarningBrush"];
                    break;

                case ConnectionState.ConnectedState connected:
                    PanelIdle.Visibility = Visibility.Collapsed;
                    PanelConnected.Visibility = Visibility.Visible;
                    PanelError.Visibility = Visibility.Collapsed;
                    TextStatus.Text = Strings.StatusConnected;
                    StatusDot.Fill = (SolidColorBrush)Application.Current.Resources["AccentBrush"];
                    TextConnectedDevice.Text = connected.DeviceName;
                    var bypassInfo = _controller.Routing.BypassDomestic ? " | 🇮🇷 Bypass IR: ON" : "";
                    TextConnectedEndpoint.Text = $"{connected.Host}:{connected.Port} ({connected.Mode.ToUpperInvariant()} Mode){bypassInfo}";
                    _connectedStart = DateTimeOffset.UtcNow;
                    _durationTimer.Start();
                    break;

                case ConnectionState.ErrorState err:
                    PanelIdle.Visibility = Visibility.Collapsed;
                    PanelConnected.Visibility = Visibility.Collapsed;
                    PanelError.Visibility = Visibility.Visible;
                    TextStatus.Text = Strings.StatusError;
                    StatusDot.Fill = (SolidColorBrush)Application.Current.Resources["DangerBrush"];
                    TextErrorTitle.Text = Strings.GetErrorTitle(err.Code.ToString());
                    TextErrorBody.Text = err.Message ?? Strings.GetErrorBody(err.Code.ToString());
                    _durationTimer.Stop();
                    break;
            }
        });
    }

    private void OnGeoLocationUpdated(GeoIpInfo? geo)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (geo is not null)
            {
                TextGeoLocation.Text = $"{geo.FlagEmoji} {geo.Country} ({geo.Ip})";
                TextGeoIsp.Text = $"{geo.City} · {geo.Isp}";
            }
            else
            {
                TextGeoLocation.Text = "🌐 Public IP Hidden";
                TextGeoIsp.Text = "Traffic routing active";
            }
        });
    }

    private void OnDevicesChanged(IReadOnlyList<LanDiscovery.Device> devices)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (devices.Count > 0)
            {
                _selectedDevice = devices[0];
                TextDetectedPhoneName.Text = _selectedDevice.DeviceName;
                TextDetectedPhoneIp.Text = $"{_selectedDevice.Host}:{_selectedDevice.PortNumber}";
                if (!string.IsNullOrWhiteSpace(_selectedDevice.Pin))
                {
                    InputPin.Text = _selectedDevice.Pin;
                }
                TextSignalStatus.Text = Strings.IsPersian ? "● آماده اتصال" : "● Ready";
                BadgeSignal.Background = (SolidColorBrush)Application.Current.Resources["AccentSoftBrush"];
            }
            else
            {
                _selectedDevice = null;
                TextDetectedPhoneName.Text = Strings.IsPersian ? "در حال جستجوی گوشی..." : "Searching for Phone...";
                TextDetectedPhoneIp.Text = Strings.IsPersian ? "هات‌اسپات یا وای‌فای را متصل کرده و دکمه شروع را در اپ بزنید" : "Connect to Wi-Fi / Hotspot and tap START in Android App";
                TextSignalStatus.Text = Strings.IsPersian ? "📡 در حال اسکن" : "📡 Scanning";
                BadgeSignal.Background = (SolidColorBrush)Application.Current.Resources["FillTertiary"];
            }
        });
    }


    private void OnStatsSampled(TunnelStats.Sample traffic)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            TextDownSpeed.Text = $"{FormatBytes((long)traffic.DownloadRateBps)}/s";
            TextUpSpeed.Text = $"{FormatBytes((long)traffic.UploadRateBps)}/s";
            TextDownTotal.Text = $"Total: {FormatBytes(traffic.BytesDown)}";
            TextUpTotal.Text = $"Total: {FormatBytes(traffic.BytesUp)}";
            TextLatency.Text = $"{Strings.LatencyLabel}: {traffic.LatencyMs} ms";

            // Update traffic history
            _downHistory.Add(traffic.DownloadRateBps);
            if (_downHistory.Count > 30) _downHistory.RemoveAt(0);

            _upHistory.Add(traffic.UploadRateBps);
            if (_upHistory.Count > 30) _upHistory.RemoveAt(0);

            if (traffic.DownloadRateBps > _peakSpeed)
            {
                _peakSpeed = traffic.DownloadRateBps;
            }
            TextPeakSpeed.Text = $"Peak: {FormatBytes((long)_peakSpeed)}/s";

            RedrawTrafficGraph();
        });
    }

    private void RedrawTrafficGraph()
    {
        var width = CanvasTrafficGraph.ActualWidth;
        var height = CanvasTrafficGraph.ActualHeight;
        if (width <= 10 || height <= 10 || _downHistory.Count < 2) return;

        var maxVal = Math.Max(_peakSpeed, 1024 * 50); // min scale 50 KB/s
        var stepX = width / (_downHistory.Count - 1);

        var downLinePoints = new PointCollection();
        var downPolyPoints = new PointCollection();
        var upLinePoints = new PointCollection();

        downPolyPoints.Add(new Windows.Foundation.Point(0, height));

        for (int i = 0; i < _downHistory.Count; i++)
        {
            var x = i * stepX;
            var downNorm = Math.Clamp(_downHistory[i] / maxVal, 0.0, 1.0);
            var yDown = height - (downNorm * (height - 8)) - 4;

            downLinePoints.Add(new Windows.Foundation.Point(x, yDown));
            downPolyPoints.Add(new Windows.Foundation.Point(x, yDown));

            var upNorm = Math.Clamp(_upHistory[i] / maxVal, 0.0, 1.0);
            var yUp = height - (upNorm * (height - 8)) - 4;
            upLinePoints.Add(new Windows.Foundation.Point(x, yUp));
        }

        downPolyPoints.Add(new Windows.Foundation.Point(width, height));

        _polylineDownload.Points = downLinePoints;
        _polygonDownload.Points = downPolyPoints;
        _polylineUpload.Points = upLinePoints;
    }

    private bool _autoScrollEnabled = true;

    private void ScrollLogs_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (ScrollLogs.ScrollableHeight > 0)
        {
            bool isNearBottom = ScrollLogs.VerticalOffset >= (ScrollLogs.ScrollableHeight - 40);
            if (_autoScrollEnabled != isNearBottom)
            {
                _autoScrollEnabled = isNearBottom;
                UpdateAutoScrollButtonUi();
            }
        }
    }

    private void BtnAutoScrollToggle_Click(object sender, RoutedEventArgs e)
    {
        _autoScrollEnabled = !_autoScrollEnabled;
        UpdateAutoScrollButtonUi();
        if (_autoScrollEnabled)
        {
            ScrollLogs.UpdateLayout();
            ScrollLogs.ChangeView(null, ScrollLogs.ScrollableHeight, null, disableAnimation: false);
        }
    }

    private void UpdateAutoScrollButtonUi()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var accent = (Brush)Application.Current.Resources["AccentBrush"];
            var muted = (Brush)Application.Current.Resources["LabelSecondary"];
            BtnAutoScrollToggle.Foreground = _autoScrollEnabled ? accent : muted;
            BtnAutoScrollToggle.Content = _autoScrollEnabled ? "⇣ Auto: ON" : "⇣ Auto: OFF";
        });
    }

    private void OnLogChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var text = LocalLog.GetFormattedLogText();
            TextLogsViewer.Text = text;
            TextLogCount.Text = $"{LocalLog.Snapshot().Count} entries";

            if (_autoScrollEnabled)
            {
                ScrollLogs.UpdateLayout();
                ScrollLogs.ChangeView(null, ScrollLogs.ScrollableHeight, null, disableAnimation: false);
            }
        });
    }

    private void RefreshCustomRulesList()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ListCustomRules.ItemsSource = null;
            ListCustomRules.ItemsSource = _controller.Routing.CustomRules.ToList();
        });
    }

    private void UpdateDuration()
    {
        var elapsed = DateTimeOffset.UtcNow - _connectedStart;
        TextDuration.Text = $"{Strings.DurationLabel}: {elapsed:hh\\:mm\\:ss}";
    }

    private void SelectTab(int tabIndex)
    {
        ViewTabConnect.Visibility = tabIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        ViewTabDns.Visibility = tabIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        ViewTabAiAccess.Visibility = tabIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        ViewTabRouting.Visibility = tabIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        ViewTabLogs.Visibility = tabIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
        ViewTabAbout.Visibility = tabIndex == 5 ? Visibility.Visible : Visibility.Collapsed;

        var accent = (SolidColorBrush)Application.Current.Resources["AccentBrush"];
        var muted = (SolidColorBrush)Application.Current.Resources["LabelSecondary"];

        NavTextConnect.Foreground = tabIndex == 0 ? accent : muted;
        NavTextDns.Foreground = tabIndex == 1 ? accent : muted;
        NavTextAi.Foreground = tabIndex == 2 ? accent : muted;
        NavTextRouting.Foreground = tabIndex == 3 ? accent : muted;
        NavTextLogs.Foreground = tabIndex == 4 ? accent : muted;
        NavTextAbout.Foreground = tabIndex == 5 ? accent : muted;

        NavTextConnect.FontWeight = tabIndex == 0 ? Microsoft.UI.Text.FontWeights.ExtraBold : Microsoft.UI.Text.FontWeights.Normal;
        NavTextDns.FontWeight = tabIndex == 1 ? Microsoft.UI.Text.FontWeights.ExtraBold : Microsoft.UI.Text.FontWeights.Normal;
        NavTextAi.FontWeight = tabIndex == 2 ? Microsoft.UI.Text.FontWeights.ExtraBold : Microsoft.UI.Text.FontWeights.Normal;
        NavTextRouting.FontWeight = tabIndex == 3 ? Microsoft.UI.Text.FontWeights.ExtraBold : Microsoft.UI.Text.FontWeights.Normal;
        NavTextLogs.FontWeight = tabIndex == 4 ? Microsoft.UI.Text.FontWeights.ExtraBold : Microsoft.UI.Text.FontWeights.Normal;
        NavTextAbout.FontWeight = tabIndex == 5 ? Microsoft.UI.Text.FontWeights.ExtraBold : Microsoft.UI.Text.FontWeights.Normal;

        // Sunken active highlight for the selected rail button (matches HTML mockup)
        var sunken = (Brush)Application.Current.Resources["FillSunken"];
        var sunkenBorder = (Brush)Application.Current.Resources["NmSunkenBorderBrush"];
        var transparent = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));

        NavBtnConnect.Background = tabIndex == 0 ? sunken : transparent;
        NavBtnConnect.BorderBrush = tabIndex == 0 ? sunkenBorder : transparent;
        NavBtnConnect.BorderThickness = new Thickness(tabIndex == 0 ? 1 : 0);

        NavBtnDns.Background = tabIndex == 1 ? sunken : transparent;
        NavBtnDns.BorderBrush = tabIndex == 1 ? sunkenBorder : transparent;
        NavBtnDns.BorderThickness = new Thickness(tabIndex == 1 ? 1 : 0);

        NavBtnAi.Background = tabIndex == 2 ? sunken : transparent;
        NavBtnAi.BorderBrush = tabIndex == 2 ? sunkenBorder : transparent;
        NavBtnAi.BorderThickness = new Thickness(tabIndex == 2 ? 1 : 0);

        NavBtnRouting.Background = tabIndex == 3 ? sunken : transparent;
        NavBtnRouting.BorderBrush = tabIndex == 3 ? sunkenBorder : transparent;
        NavBtnRouting.BorderThickness = new Thickness(tabIndex == 3 ? 1 : 0);

        NavBtnLogs.Background = tabIndex == 4 ? sunken : transparent;
        NavBtnLogs.BorderBrush = tabIndex == 4 ? sunkenBorder : transparent;
        NavBtnLogs.BorderThickness = new Thickness(tabIndex == 4 ? 1 : 0);

        NavBtnAbout.Background = tabIndex == 5 ? sunken : transparent;
        NavBtnAbout.BorderBrush = tabIndex == 5 ? sunkenBorder : transparent;
        NavBtnAbout.BorderThickness = new Thickness(tabIndex == 5 ? 1 : 0);

        // SVG icon stroke color — active = cyan, inactive = muted
        NavIconConnect.Stroke = tabIndex == 0 ? accent : muted;
        NavIconDns.Stroke = tabIndex == 1 ? accent : muted;
        NavIconAi.Stroke = tabIndex == 2 ? accent : muted;
        NavIconRouting.Stroke = tabIndex == 3 ? accent : muted;
        NavIconLogs.Stroke = tabIndex == 4 ? accent : muted;
        NavIconAbout.Stroke = tabIndex == 5 ? accent : muted;

        if (tabIndex == 1) LoadDnsTab();

        if (tabIndex == 4)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_autoScrollEnabled)
                        {
                            ScrollLogs.UpdateLayout();
                            ScrollLogs.ChangeView(null, ScrollLogs.ScrollableHeight, null, disableAnimation: true);
                        }
                    });
                }
            }

            private void NavBtnConnect_Click(object sender, RoutedEventArgs e) => SelectTab(0);
            private void NavBtnDns_Click(object sender, RoutedEventArgs e) => SelectTab(1);
            private void NavBtnAi_Click(object sender, RoutedEventArgs e) => SelectTab(2);
            private void NavBtnRouting_Click(object sender, RoutedEventArgs e) => SelectTab(3);
            private void NavBtnLogs_Click(object sender, RoutedEventArgs e) => SelectTab(4);
            private void NavBtnAbout_Click(object sender, RoutedEventArgs e) => SelectTab(5);

    // ================= DNS TAB =================
    private List<DnsServer> _dnsServers = new();
    private string _dnsActiveId = "builtin-system";
    private string? _pendingDnsId;          // row clicked but Set not yet pressed
    private readonly Dictionary<string, TextBlock> _latencyCells = new();
    private readonly Dictionary<string, (int ms, bool ok)> _lastResults = new();
    private readonly Dictionary<string, DnsServer> _rowServers = new();

    private void LoadDnsTab()
    {
        try { (_dnsServers, _dnsActiveId) = DnsStore.Load(); }
        catch { _dnsServers = DnsStore.BuiltIns(); _dnsActiveId = "builtin-system"; }
        _pendingDnsId = null;
        RenderDnsGroups();
        RefreshActiveCard();
    }

    private (string title, List<DnsServer> items)[] GroupDns()
    {
        var iranIds = new[] { "builtin-403", "builtin-shecan", "builtin-electro", "builtin-radar",
                              "builtin-vanilla", "builtin-beshkan", "builtin-shelter", "builtin-begzar", "builtin-pishgaman" };
        return new[]
        {
            (Strings.DnsGroupIran, _dnsServers.FindAll(s => iranIds.Contains(s.Id))),
            (Strings.DnsGroupGlobal, _dnsServers.FindAll(s =>
                s.Id.StartsWith("builtin-") && !iranIds.Contains(s.Id))),
            (Strings.DnsGroupCustom, _dnsServers.FindAll(s => !s.BuiltIn)),
        };
    }

    private void RenderDnsGroups()
    {
        _latencyCells.Clear();
        _rowServers.Clear();
        DnsGroupsPanel.Children.Clear();

        foreach (var (title, items) in GroupDns())
        {
            if (items.Count == 0) continue;

            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 5) };
            header.Children.Add(new TextBlock { Text = title, FontSize = 10.5, FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                                                Foreground = (Brush)Application.Current.Resources["LabelSecondary"] });
            header.Children.Add(new Border { Width = 60, Height = 1, VerticalAlignment = VerticalAlignment.Center,
                                             Background = (Brush)Application.Current.Resources["NmBorderBrush"] });
            DnsGroupsPanel.Children.Add(header);

            var list = new StackPanel { Spacing = 2,
                Background = (Brush)Application.Current.Resources["NmCardBrush"],
                BorderBrush = (Brush)Application.Current.Resources["NmBorderBrush"],
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12) };

            foreach (var s in items)
            {
                var selected = s.Id == (_pendingDnsId ?? _dnsActiveId);
                var active = s.Id == _dnsActiveId;

                var row = new Grid { Padding = new Thickness(11, 9, 11, 9), Background = TransparentBrush() };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });   // radio
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });                     // name
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });   // addr
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });                     // latency

                var radio = new Ellipse { Width = 14, Height = 14, StrokeThickness = 2,
                    Stroke = (Brush)Application.Current.Resources["LabelSecondary"],
                    Fill = TransparentBrush(), VerticalAlignment = VerticalAlignment.Center };
                if (selected) radio.Fill = (Brush)Application.Current.Resources["AccentBrush"];
                Grid.SetColumn(radio, 0);

                var name = new TextBlock { Text = s.Label, FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 0, 0),
                    Foreground = selected ? (Brush)Application.Current.Resources["AccentBrush"]
                                          : (Brush)Application.Current.Resources["LabelPrimary"] };
                Grid.SetColumn(name, 1);

                // Flag badges under the name: AI recommendation / outage-proof
                var flagStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(9, 2, 0, 0) };
                if (s.AiFlag)
                    flagStack.Children.Add(new Border { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 229, 255)),
                        CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 1, 5, 1),
                        Child = new TextBlock { Text = "AI", FontSize = 8, FontWeight = Microsoft.UI.Text.FontWeights.Black, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 35, 44)) } });
                if (s.OutageProof)
                    flagStack.Children.Add(new Border { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 52, 211, 153)),
                        CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 1, 5, 1),
                        Child = new TextBlock { Text = Strings.IsPersian ? "قطع‌پذیر" : "OUTAGE-OK", FontSize = 8, FontWeight = Microsoft.UI.Text.FontWeights.Black, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 6, 40, 28)) } });
                var nameCol = new StackPanel { Spacing = 0 };
                nameCol.Children.Add(name);
                if (flagStack.Children.Count > 0)
                    nameCol.Children.Add(flagStack);
                Grid.SetColumn(nameCol, 1);

                var addrStack = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
                if (s.Kind == "system")
                {
                    addrStack.Children.Add(new TextBlock { Text = "OS default", FontSize = 10, FontFamily = new FontFamily("Consolas"),
                        Foreground = (Brush)Application.Current.Resources["LabelSecondary"] });
                }
                else if (s.Kind == "doh")
                {
                    addrStack.Children.Add(new TextBlock { Text = s.DohUrl ?? "", FontSize = 10, FontFamily = new FontFamily("Consolas"),
                        Foreground = (Brush)Application.Current.Resources["LabelSecondary"], TextTrimming = TextTrimming.CharacterEllipsis });
                }
                else
                {
                    addrStack.Children.Add(new TextBlock { Text = s.Primary, FontSize = 10, FontFamily = new FontFamily("Consolas"),
                        Foreground = (Brush)Application.Current.Resources["LabelSecondary"] });
                    if (!string.IsNullOrEmpty(s.Secondary))
                        addrStack.Children.Add(new TextBlock { Text = s.Secondary, FontSize = 10, FontFamily = new FontFamily("Consolas"),
                            Foreground = (Brush)Application.Current.Resources["LabelSecondary"] });
                }
                Grid.SetColumn(addrStack, 2);

                var lat = new TextBlock { FontSize = 10, FontFamily = new FontFamily("Consolas"),
                    HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)Application.Current.Resources["LabelSecondary"] };
                if (active && s.Kind != "system")
                {
                    lat.Text = "✓";
                    lat.Foreground = (Brush)Application.Current.Resources["AccentBrush"];
                }
                if (_lastResults.TryGetValue(s.Id, out var last))
                {
                    lat.Text = last.ok ? $"{last.ms} ms" : "✗";
                    lat.Foreground = new SolidColorBrush(last.ok
                        ? (last.ms < 80 ? Windows.UI.Color.FromArgb(255, 52, 211, 153)
                          : last.ms < 200 ? Windows.UI.Color.FromArgb(255, 251, 191, 36)
                          : Windows.UI.Color.FromArgb(255, 248, 113, 113))
                        : Windows.UI.Color.FromArgb(255, 248, 113, 113));
                }
                Grid.SetColumn(lat, 3);
                _latencyCells[s.Id] = lat;

                row.Children.Add(radio); row.Children.Add(name); row.Children.Add(addrStack); row.Children.Add(lat);
                row.PointerPressed += (_, _) => { _pendingDnsId = s.Id; RenderDnsGroups(); };
                _rowServers[s.Id] = s;

                list.Children.Add(row);
            }
            DnsGroupsPanel.Children.Add(list);
        }
    }

    private void RefreshActiveCard()
    {
        var s = _dnsServers.FirstOrDefault(x => x.Id == _dnsActiveId);
        if (s is null) { TextActiveName.Text = "—"; return; }
        TextActiveName.Text = s.Label;
        TextActiveBadge.Text = Strings.DnsActiveBadge;
        TextActiveAddr.Text = s.Kind == "system" ? "OS default"
            : s.Kind == "doh" ? s.DohUrl ?? ""
            : s.Primary + (string.IsNullOrEmpty(s.Secondary) ? "" : $" / {s.Secondary}");
        TextActiveLatency.Text = "";

        var target = SmartDnsApplier.Current;
        TextDnsStatus.Visibility = Visibility.Visible;
        TextDnsStatus.Text = target is null
            ? (Strings.IsPersian
                ? "برای اعمال روی سیستم، انتخاب کنید و دکمه ✓ تنظیم را بزنید."
                : "Pick a resolver, then press ✓ Set to apply it system-wide.")
            : (Strings.IsPersian
                ? $"📍 اعمالشده روی: {target.AdapterName}"
                : $"📍 Applied to: {target.AdapterName}");
    }

    private async void BtnTestAllDns_Click(object sender, RoutedEventArgs e)
    {
        BtnTestAllDns.IsEnabled = false;
        foreach (var kv in _latencyCells) { kv.Value.Text = "…"; kv.Value.Foreground = (Brush)Application.Current.Resources["LabelSecondary"]; }

        // Sequential testing (top to bottom) so results appear in list order.
        foreach (var kv in _rowServers)
        {
            if (!_latencyCells.TryGetValue(kv.Key, out var cell)) continue;
            var res = await DnsTester.TestAsync(kv.Value);
            _lastResults[kv.Key] = (res.LatencyMs, res.Success);
            if (res.Success)
            {
                cell.Text = $"{res.LatencyMs} ms";
                cell.Foreground = new SolidColorBrush(res.LatencyMs < 80 ? Windows.UI.Color.FromArgb(255, 52, 211, 153)
                                              : res.LatencyMs < 200 ? Windows.UI.Color.FromArgb(255, 251, 191, 36)
                                                                     : Windows.UI.Color.FromArgb(255, 248, 113, 113));
            }
            else { cell.Text = "✗"; cell.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 248, 113, 113)); }
            if (kv.Key == _dnsActiveId) TextActiveLatency.Text = res.Success ? $"{res.LatencyMs} ms" : "✗";
        }
        BtnTestAllDns.IsEnabled = true;
    }

    private void BtnApplyDns_Click(object sender, RoutedEventArgs e)
    {
        var id = _pendingDnsId ?? _dnsActiveId;
        var server = _dnsServers.FirstOrDefault(x => x.Id == id);
        if (server is null) return;

        bool tunRunning = _controller.State is ConnectionState.ConnectedState && _controller.ActiveMode == "tun";
        var (ok, msg) = SmartDnsApplier.Apply(server, tunRunning);

        if (ok)
        {
            _dnsActiveId = id;
            _pendingDnsId = null;
            DnsStore.Save(_dnsServers, _dnsActiveId);
            LocalLog.Info($"DNS applied: {server.Label} — {msg}");
        }
        TextDnsStatus.Visibility = Visibility.Visible;
        TextDnsStatus.Text = (ok ? "✓ " : "✗ ") + msg;
        TextDnsStatus.Foreground = ok ? (Brush)Application.Current.Resources["AccentBrush"]
                                      : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 248, 113, 113));
        RenderDnsGroups();
        RefreshActiveCard();
    }

    private void BtnUnsetDns_Click(object sender, RoutedEventArgs e)
    {
        var (ok, msg) = SmartDnsApplier.Unset();
        if (ok)
        {
            _dnsActiveId = "builtin-system";
            _pendingDnsId = null;
            DnsStore.Save(_dnsServers, _dnsActiveId);
            LocalLog.Info("DNS reverted to system default");
        }
        TextDnsStatus.Visibility = Visibility.Visible;
        TextDnsStatus.Text = (ok ? "✓ " : "✗ ") + msg;
        RenderDnsGroups();
        RefreshActiveCard();
    }

    private void BtnFlushDns_Click(object sender, RoutedEventArgs e)
    {
        SmartDnsApplier.FlushCache();
        BtnFlushDns.Content = "✓ Flushed";
        DispatcherQueue.TryEnqueue(() => { Task.Delay(1400).Wait(); BtnFlushDns.Content = "🧹 Flush DNS"; });
    }

    private static Brush TransparentBrush() => new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));

    private static StackPanel wrapNameOnly(TextBlock name)
    {
        var p = new StackPanel { Spacing = 0 };
        p.Children.Add(name);
        return p;
    }

    private void BtnAddDns_Click(object sender, RoutedEventArgs e)
    {
        // Simple inline prompt: reuse a ContentDialog with two text boxes.
        var panel = new StackPanel { Spacing = 10 };
        var tbLabel = new TextBox { PlaceholderText = "Name (e.g. My resolver)", Header = "Name" };
        var tbPrimary = new TextBox { PlaceholderText = "e.g. 1.1.1.1 or https://.../dns-query", Header = "Primary IP or DoH URL" };
        panel.Children.Add(tbLabel); panel.Children.Add(tbPrimary);

        var dlg = new ContentDialog
        {
            Title = "Add custom DNS",
            Content = panel,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.Content.XamlRoot,
        };
        var tcs = new TaskCompletionSource<ContentDialogResult>();
        dlg.Closed += (_, args) => tcs.TrySetResult(args.Result);
        _ = dlg.ShowAsync();
        _ = tcs.Task.ContinueWith(t =>
        {
            if (t.Result != ContentDialogResult.Primary) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                var label = string.IsNullOrWhiteSpace(tbLabel.Text) ? "Custom DNS" : tbLabel.Text.Trim();
                var primary = tbPrimary.Text.Trim();
                var isDoH = primary.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || primary.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                var srv = new DnsServer
                {
                    Label = label,
                    Kind = isDoH ? "doh" : "udp",
                    Primary = isDoH ? "" : primary,
                    DohUrl = isDoH ? primary : null,
                };
                _dnsServers.Add(srv);
                DnsStore.Save(_dnsServers, _dnsActiveId);
                RenderDnsGroups(); RefreshActiveCard();
            });
        });
    }

    // ================= AI ACCESS TAB =================
    private async void BtnIpv6Test_Click(object sender, RoutedEventArgs e)
    {
        BtnIpv6Test.IsEnabled = false;
        TextIpv6Result.Text = Strings.IsPersian ? "در حال تست…" : "Testing…";
        TextIpv6Result.Foreground = (Brush)Application.Current.Resources["LabelSecondary"];
        try
        {
            using var v4 = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(8) };
            var v4Task = v4.GetStringAsync("https://api-ipv4.ip.sb/ip");
            using var v6 = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler()) { Timeout = TimeSpan.FromSeconds(8) };
            var v6Task = v6.GetStringAsync("https://api64.ipify.org");

            string? v4Ip = null, v6Ip = null;
            try { v4Ip = (await v4Task).Trim(); } catch { }
            try { v6Ip = (await v6Task).Trim(); } catch { }

            if (string.IsNullOrEmpty(v6Ip))
            {
                TextIpv6Result.Text = (Strings.IsPersian ? "✓ نشتی IPv6 نیست — ترافیک روی IPv4 میماند (" : "✓ No IPv6 leak — traffic stays on IPv4 (") + (v4Ip ?? "?") + ")";
                TextIpv6Result.Foreground = (Brush)Application.Current.Resources["AccentBrush"];
            }
            else
            {
                TextIpv6Result.Text = (Strings.IsPersian
                    ? $"⚠ نشتی IPv6: {v6Ip} (IPv4: {v4Ip ?? "ندارد"}). IPv6 را ببندید — علت اصلی 403 گوگل."
                    : $"⚠ IPv6 LEAK: {v6Ip} (IPv4: {v4Ip ?? "none"}). Block IPv6 — the #1 hidden cause of Google 403s.");
                TextIpv6Result.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 248, 113, 113));
            }
        }
        catch (Exception ex) { TextIpv6Result.Text = "✗ " + ex.Message; }
        finally { BtnIpv6Test.IsEnabled = true; }
    }

    private void BtnIpv6Block_Click(object sender, RoutedEventArgs e)
    {
        // Toggle IPv6 preference via prefix policy (Microsoft-recommended, instant, no reboot).
        // Prefer ::ffff:0:0/96 (IPv4-mapped) over native IPv6 — does NOT disable IPv6.
        // Fallback for persistence across reboots: DisabledComponents=0x20 in registry.
        try
        {
            bool isBlocked;
            using (var check = Process.Start(new ProcessStartInfo("netsh",
                "interface ipv6 show prefixpolicies") { CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true }))
            {
                check!.WaitForExit(6000);
                isBlocked = check.StandardOutput.ReadToEnd().Contains("::ffff:0:0/96", StringComparison.OrdinalIgnoreCase);
            }

            string args;
            if (isBlocked)
            {
                // Revert: delete the custom policy → default precedence 35 restored
                args = "interface ipv6 delete prefixpolicy ::ffff:0:0/96";
                RunNetsh(args);
                TextIpv6Result.Text = Strings.IsPersian
                    ? "✓ بلاک IPv6 برداشته شد — اولویتها به حالت پیشفرض برگشت."
                    : "✓ IPv6 block removed — prefix policies restored to default.";
                TextIpv6Result.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 52, 211, 153));
                BtnIpv6Block.Content = Strings.IsPersian ? "🚫 بلاک IPv6" : "🚫 Block IPv6";
            }
            else
            {
                // Add policy with higher precedence than ::/0 → IPv4 preferred
                args = "interface ipv6 add prefixpolicy ::ffff:0:0/96 46 4";
                var ok = RunNetsh(args);
                if (!ok) RunNetsh("interface ipv6 set prefixpolicy ::ffff:0:0/96 46 4"); // already exists → set

                TextIpv6Result.Text = Strings.IsPersian
                    ? "✓ IPv6 ترجیح داده نمیشود — مرورگر را ریاستارت کنید و دوباره تست بگیرید."
                    : "✓ IPv6 deprioritized — restart your browser and re-test.";
                TextIpv6Result.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 52, 211, 153));
                BtnIpv6Block.Content = Strings.IsPersian ? "↩ برداشتن بلاک" : "↩ Unblock";
            }
            SmartDnsApplier.FlushCache();
        }
        catch (Exception ex)
        {
            TextIpv6Result.Text = (Strings.IsPersian ? "✗ خطا: " : "✗ Error: ") + ex.Message;
        }
    }

    private static bool RunNetsh(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", args)
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            p.WaitForExit(8000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    public sealed record AiSiteResult(string Site, int StatusCode, string? FinalUrl, string? Error, bool Ok403);

    private static async Task<AiSiteResult> ProbeAiSite(System.Net.Http.HttpClient http, string name, string url)
    {
        try
        {
            using var req = new System.Net.Http.HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126 Safari/537.36");
            req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            using var resp = await http.SendAsync(req, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
            var code = (int)resp.StatusCode;
            var final = resp.RequestMessage?.RequestUri?.Host ?? url;
            // 200/302 within same host = reachable; 403/429 = blocked; 301→consent.google = still ok
            bool ok = code is >= 200 and < 400 && !code.Equals(403);
            return new AiSiteResult(name, code, final, null, ok);
        }
        catch (Exception ex)
        {
            return new AiSiteResult(name, 0, null, ex.Message, false);
        }
    }

    private async void BtnAiCheck_Click(object sender, RoutedEventArgs e)
    {
        BtnAiCheck.IsEnabled = false;
        TextAiCheckResult.Visibility = Visibility.Visible;
        TextAiCheckResult.Text = Strings.IsPersian ? "در حال بررسی سرویس‌ها…" : "Probing services…";
        TextAiCheckResult.Foreground = (Brush)Application.Current.Resources["LabelSecondary"];

        try
        {
            using var http = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            }) { Timeout = TimeSpan.FromSeconds(15) };

            var sites = new[]
            {
                ("Gemini",   "https://gemini.google.com/"),
                ("ChatGPT",  "https://chatgpt.com/"),
                ("Claude",   "https://claude.ai/"),
                ("YouTube",  "https://www.youtube.com/"),
                ("AI Studio","https://aistudio.google.com/"),
            };

            var tasks = sites.Select(s => ProbeAiSite(http, s.Item1, s.Item2)).ToList();
            var lines = new List<string>();
            while (tasks.Count > 0)
            {
                var done = await Task.WhenAny(tasks);
                tasks.Remove(done);
                var r = await done;
                string icon, line;
                if (r.Error is not null)
                {
                    icon = "✗"; line = $"{icon} {r.Site}: {r.Error}";
                }
                else if (r.StatusCode == 403 || r.StatusCode == 429)
                {
                    icon = "⛔"; line = Strings.IsPersian
                        ? $"{icon} {r.Site}: مسدود ({r.StatusCode}) — تحریم IP خروجی"
                        : $"{icon} {r.Site}: BLOCKED ({r.StatusCode}) — exit-IP restriction";
                    TextAiCheckResult.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 248, 113, 113));
                }
                else if (r.StatusCode >= 500)
                {
                    icon = "⚠"; line = $"{icon} {r.Site}: HTTP {r.StatusCode}";
                }
                else
                {
                    icon = "✓"; line = $"{icon} {r.Site}: OK ({r.StatusCode})";
                }
                lines.Add(line);
            }

            TextAiCheckResult.Text = string.Join("\n", lines);
            if (!lines.Any(l => l.StartsWith("⛔")))
                TextAiCheckResult.Foreground = (Brush)Application.Current.Resources["AccentBrush"];
        }
        catch (Exception ex)
        {
            TextAiCheckResult.Text = "✗ " + ex.Message;
        }
        finally { BtnAiCheck.IsEnabled = true; }
    }

    private async void BtnWarpDetect_Click(object sender, RoutedEventArgs e)
    {
        BtnWarpDetect.IsEnabled = false;
        TextWarpStatus.Text = Strings.IsPersian ? "در حال بررسی…" : "Detecting…";
        try
        {
            // 1) Is the Cloudflare WARP service installed?
            var warpSvc = Process.GetProcessesByName("warp-svc").FirstOrDefault();
            var cloudflareDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Cloudflare", "Cloudflare WARP");
            bool installed = warpSvc is not null || Directory.Exists(cloudflareDir);

            if (!installed)
            {
                TextWarpStatus.Text = Strings.IsPersian
                    ? "✗ WARP نصب نیست. از one.one.one.one دانلود و نصب کنید، سپس در تنظیمات آن حالت Proxy را فعال کنید (پورت 40000)."
                    : "✗ WARP not installed. Download from one.one.one.one, install it, then enable Proxy mode in its settings (port 40000).";
                TextWarpStatus.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 248, 113, 113));
                return;
            }

            // 2) Is its local proxy port listening? (default 40000 in proxy mode)
            bool port40000Up = false;
            try
            {
                using var c = new System.Net.Sockets.TcpClient();
                var ar = c.BeginConnect("127.0.0.1", 40000, null, null);
                port40000Up = ar.AsyncWaitHandle.WaitOne(1500) && c.Connected;
                c.Close();
            }
            catch { }

            if (port40000Up)
            {
                TextWarpStatus.Text = Strings.IsPersian
                    ? "✓ WARP فعال است (127.0.0.1:40000). کانفیگ v2ray خود را طوری تنظیم کنید که دامنههای گوگل/AI را از این پروکسی رد کند."
                    : "✓ WARP is active on 127.0.0.1:40000. Point your v2ray config's Google/AI domains through this local proxy.";
                TextWarpStatus.Foreground = (Brush)Application.Current.Resources["AccentBrush"];
            }
            else
            {
                TextWarpStatus.Text = Strings.IsPersian
                    ? "⚠ WARP نصب است ولی حالت Proxy روشن نیست. در اپ Cloudflare WARP: تنظیمات ← Advanced ← Connection ← Enable Proxy mode."
                    : "⚠ WARP installed but Proxy mode is off. In the Cloudflare WARP app: Settings → Advanced → Connection → Enable Proxy mode.";
                TextWarpStatus.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 251, 191, 36));
            }
        }
        catch (Exception ex) { TextWarpStatus.Text = "✗ " + ex.Message; }
        finally { BtnWarpDetect.IsEnabled = true; }
    }

    private void CardModeTun_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _controller.ActiveMode = "tun";
        UpdateModeCardsUi();
    }

    private void CardModeProxy_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _controller.ActiveMode = "proxy";
        UpdateModeCardsUi();
    }

    private void UpdateModeCardsUi()
    {
        var isTun = _controller.ActiveMode == "tun";
        var accentBrush = (Brush)Application.Current.Resources["AccentBrush"];
        var hairlineBrush = (Brush)Application.Current.Resources["NmBorderBrush"];
        var selectedBrush = (Brush)Application.Current.Resources["NmCardSelectedBrush"];
        var cardBrush = (Brush)Application.Current.Resources["NmCardBrush"];

        CardModeTun.BorderBrush = isTun ? accentBrush : hairlineBrush;
        CardModeTun.BorderThickness = new Thickness(isTun ? 1.5 : 1);
        CardModeTun.Background = isTun ? selectedBrush : cardBrush;

        CardModeProxy.BorderBrush = !isTun ? accentBrush : hairlineBrush;
        CardModeProxy.BorderThickness = new Thickness(!isTun ? 1.5 : 1);
        CardModeProxy.Background = !isTun ? selectedBrush : cardBrush;

        TextStatusMode.Text = isTun ? "⚡ TUN" : "🌐 Proxy";
    }

    private async void BtnConnect_Click(object sender, RoutedEventArgs e)
    {
        var pin = InputPin.Text.Trim();
        if (pin.Length != 4)
        {
            TextPinHint.Text = Strings.IsPersian ? "⚠️ لطفاً پین ۴ رقمی کامل را وارد کنید" : "⚠️ Please enter full 4 digits";
            return;
        }

        if (_selectedDevice is null)
        {
            TextPinHint.Text = Strings.IsPersian ? "⚠️ هیچ گوشی‌ای یافت نشد. ابتدا در گوشی دکمه شروع را بزنید." : "⚠️ No phone detected yet. Tap START on Android phone.";
            return;
        }

        var host = _selectedDevice.Host;
        var port = _selectedDevice.PortNumber;
        var deviceName = _selectedDevice.DeviceName;

        TextPinHint.Text = Strings.PinHint;
        await _controller.ConnectAsync(host, port, pin, deviceName);
    }

    private void BtnDisconnect_Click(object sender, RoutedEventArgs e)
    {
        _controller.Disconnect();
    }

    private void BtnErrorDismiss_Click(object sender, RoutedEventArgs e)
    {
        _controller.Disconnect();
    }

    private async void BtnErrorRetry_Click(object sender, RoutedEventArgs e)
    {
        var pin = InputPin.Text.Trim();
        if (_selectedDevice is not null)
        {
            await _controller.ConnectAsync(_selectedDevice.Host, _selectedDevice.PortNumber, pin, _selectedDevice.DeviceName);
        }
        else
        {
            _controller.Disconnect();
        }
    }

    private async void SwitchBypassDomestic_Toggled(object sender, RoutedEventArgs e)
    {
        await _controller.SetBypassDomesticAsync(SwitchBypassDomestic.IsOn);
    }

    private async void SwitchBypassLan_Toggled(object sender, RoutedEventArgs e)
    {
        await _controller.SetBypassLanAsync(SwitchBypassLan.IsOn);
    }

    private void SwitchStartWithWindows_Toggled(object sender, RoutedEventArgs e)
    {
        var isEnabled = SwitchStartWithWindows.IsOn;
        _controller.Settings.StartWithWindows = isEnabled;
        _controller.SaveCurrentSettings();
        StartupHelper.SetStartup(isEnabled);
        UpdateTrayIconState();
        LocalLog.Info($"Start with Windows set to: {isEnabled}");
    }

    private void SwitchCloseToTray_Toggled(object sender, RoutedEventArgs e)
    {
        _controller.Settings.CloseToTray = SwitchCloseToTray.IsOn;
        _controller.SaveCurrentSettings();
        UpdateTrayIconState();
        LocalLog.Info($"Close to Tray set to: {SwitchCloseToTray.IsOn}");
    }

    private void SwitchMinimizeToTray_Toggled(object sender, RoutedEventArgs e)
    {
        _controller.Settings.MinimizeToTray = SwitchMinimizeToTray.IsOn;
        _controller.SaveCurrentSettings();
        UpdateTrayIconState();
        LocalLog.Info($"Minimize to Tray set to: {SwitchMinimizeToTray.IsOn}");
    }

    private async void BtnAddRule_Click(object sender, RoutedEventArgs e)
    {
        var rawPattern = InputNewRulePattern.Text.Trim();
        if (string.IsNullOrWhiteSpace(rawPattern)) return;

        var clean = rawPattern.TrimStart('*', '.');
        if (clean.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || clean.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(clean, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            {
                clean = uri.Host.TrimStart('*', '.');
            }
        }

        var type = rawPattern.Contains('/') || (System.Net.IPAddress.TryParse(rawPattern, out _) && !rawPattern.Contains('*'))
            ? RuleType.IpCidr
            : RuleType.DomainSuffix;

        var rule = new RoutingRule(type, clean, RuleAction.Direct);
        await _controller.AddCustomRuleAsync(rule);

        InputNewRulePattern.Text = "";
        RefreshCustomRulesList();
    }

    private void BtnDeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RoutingRule rule })
        {
            _controller.RemoveCustomRule(rule);
            RefreshCustomRulesList();
        }
    }

    private async void BtnRefreshGeo_Click(object sender, RoutedEventArgs e)
    {
        await _controller.RefreshGeoLocationAsync();
    }

    private void BtnLangToggle_Click(object sender, RoutedEventArgs e)
    {
        Strings.IsPersian = !Strings.IsPersian;
        ApplyStrings();
        if (ViewTabDns.Visibility == Visibility.Visible) { RenderDnsGroups(); RefreshActiveCard(); }
    }

    private async void BtnCopyLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = LocalLog.GetFormattedLogText();
            if (!string.IsNullOrEmpty(text))
            {
                var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
                package.SetText(text);
                Clipboard.SetContent(package);
                try { Clipboard.Flush(); } catch { }
            }
            BtnCopyLogs.Content = Strings.CopyLogsFeedback;
            await Task.Delay(1500).ConfigureAwait(true);
            BtnCopyLogs.Content = Strings.CopyLogsAction;
        }
        catch (Exception ex)
        {
            LocalLog.Error($"Copy failed: {ex.Message}");
        }
    }

    private void BtnClearLogs_Click(object sender, RoutedEventArgs e)
    {
        LocalLog.Clear();
        TextLogsViewer.Text = "";
    }

    private void BtnOpenGithub_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/omid-io/AirTun",
                UseShellExecute = true,
            });
        }
        catch { }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}
