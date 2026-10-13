using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Newtonsoft.Json.Linq;

namespace ZemaxMCP.Launcher;

public partial class MainWindow : Window
{
    private static string ProductVersion => typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "unknown";
    private Process? _bridge;
    private int _bridgeRestartAttempts;
    private bool _exitRequested;
    private bool _handoffToUpdater;
    private readonly string _maintenanceTicket = Guid.NewGuid().ToString("N");
    private System.Threading.CancellationTokenSource? _updateCancellation;
    private bool _clientSetupPrompted;
    private bool _refreshingStatus;
    private bool _refreshingActivity;
    private bool _refreshingJobsDelta;
    private bool _jobsDeltaSupported = true;
    private readonly ScopedDeltaCursor _jobsDelta = new ScopedDeltaCursor();
    private DateTimeOffset _jobsDeltaRetryAfter;
    private bool _healthReachable;
    private bool _activityEndpointSupported = true;
    private bool _activityDeltaSupported = true;
    private readonly ScopedDeltaCursor _activityDelta = new ScopedDeltaCursor();
    private DateTimeOffset _activityEndpointRetryAfter;
    private bool _windowLoaded;
    private bool _settingsLoadFailed;
    private string _localAccessToken = "";
    private string _remoteEndpoint = "";
    private string _remoteAccessToken = "";
    private string _tlsPfxPath = "";
    private string _tlsPfxPassword = "";
    private string _runningEndpoint = "";
    private string _runningAccessToken = "";
    private ZemaxInstallation? _runningInstallation;
    private bool _checkingStop;
    private bool LocalBridgeRunning => _bridge != null && !_bridge.HasExited;
    private string LocalEndpoint => LocalBridgeRunning ? _runningEndpoint : Url;
    private string _fullDiagnostics = "Status has not been checked yet.";
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _activityTimer;
    public MainWindow()
    {
        InitializeComponent();
        // WPF ItemsSource observes a real view-model property rather than a
        // manually assigned ListBox collection on every Job status refresh.
        TasksPageJobs.DataContext=_taskCenter;
        SourceInitialized += (_, _) => ApplyMaterial();
        SystemEvents.UserPreferenceChanged += AppearancePreferenceChanged;
        SystemEvents.SessionSwitch += AppearanceSessionChanged;
        var applicationIcon = GetApplicationIcon();
        _trayIcon = new Forms.NotifyIcon { Icon = applicationIcon, Text = "Zemax MCP", Visible = true };
        _trayIcon.MouseClick += (_, eventArgs) =>
        {
            if (eventArgs.Button == Forms.MouseButtons.Left)
                Dispatcher.BeginInvoke(new Action(RestoreWindow));
        };
        var menu = new RoundedTrayMenu
        {
            Renderer = new TrayMenuRenderer(),
            ShowImageMargin = false,
            BackColor = Forms.SystemInformation.HighContrast ? System.Drawing.SystemColors.Menu : System.Drawing.Color.FromArgb(248, 250, 253),
            ForeColor = Forms.SystemInformation.HighContrast ? System.Drawing.SystemColors.MenuText : System.Drawing.Color.FromArgb(29, 29, 31),
            Font = new System.Drawing.Font("Segoe UI", 10),
            Padding = new Forms.Padding(5)
        };
        menu.Items.Add("Start", null, (_, _) => Dispatcher.BeginInvoke(new Action(StartServiceFromTray)));
        menu.Items.Add("Stop", null, (_, _) => Dispatcher.BeginInvoke(new Action(StopService)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.BeginInvoke(new Action(ExitApplication)));
        foreach (Forms.ToolStripItem item in menu.Items)
        {
            if (item is Forms.ToolStripMenuItem)
                item.Padding = new Forms.Padding(14, 8, 24, 8);
        }
        _trayIcon.ContextMenuStrip = menu;
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _statusTimer.Tick += async (_, _) => await RefreshStatusAsync();
        _activityTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _activityTimer.Tick += async (_, _) =>
        {
            await RefreshActivityAsync();
            await RefreshJobsDeltaAsync();
        };
    }

    private static System.Drawing.Icon GetApplicationIcon()
    {
        try
        {
            var executable = Process.GetCurrentProcess().MainModule?.FileName;
            return string.IsNullOrWhiteSpace(executable)
                ? System.Drawing.SystemIcons.Application
                : System.Drawing.Icon.ExtractAssociatedIcon(executable) ?? System.Drawing.SystemIcons.Application;
        }
        catch { return System.Drawing.SystemIcons.Application; }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LoadSettings();
        try
        {
            ZemaxMCP.DesktopShared.DesktopShortcut.MigrateLegacy(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Start-Zemax-MCP.exe"));
        }
        catch (Exception ex) { Report("Desktop shortcut rename skipped: " + ex.Message); }
        var installs = ZemaxInstallation.FindAll();
        var savedRoot = ReadSetting("zemaxRoot");
        if (!string.IsNullOrWhiteSpace(savedRoot) && installs.All(x => !x.Root.Equals(savedRoot, StringComparison.OrdinalIgnoreCase)))
        {
            var savedManual = ZemaxInstallation.FromFolder(savedRoot!);
            if (savedManual != null) installs.Add(savedManual);
        }
        ZemaxVersions.ItemsSource = installs;
        ZemaxVersions.SelectedItem = installs.FirstOrDefault(x => x.Root.Equals(savedRoot, StringComparison.OrdinalIgnoreCase));
        if (ZemaxVersions.SelectedItem == null) ZemaxVersions.SelectedIndex = installs.Count > 0 ? 0 : -1;
        var hasRemoteEndpoint = IsRemoteEndpointConfigured;
        Report(_settingsLoadFailed
            ? "Saved preferences could not be read. The file is preserved; restore launcher-settings.json from a valid backup. Automatic service startup is skipped."
            : hasRemoteEndpoint
            ? "Using the saved remote MCP endpoint. Local service startup is skipped."
            : installs.Count == 0
            ? "No local OpticStudio found. Paste secure setup from the OpticStudio computer, then select Check connection and Configure clients."
            : "Starting local MCP endpoint automatically…");
        RefreshEndpoint();
        _windowLoaded = true;
        ApplyMaterial();
        if (installs.Count > 0 && !hasRemoteEndpoint && !_settingsLoadFailed) StartBridge();
        SetIndicatorsChecking();
        _statusTimer.Start();
        _activityTimer.Start();
        RefreshClientDashboard(null);
        OfferFirstRunClientSetup();
    }
    private void ApplyMaterial()
    {
        // Fixed visual style; native composition retains its accessibility/OS fallback.
        WindowMaterial.Apply(this, "mica");
    }

    private void OpenTasks_Click(object sender, RoutedEventArgs e) => MainSections.SelectedIndex = 1;
    private void OpenSettings_Click(object sender, RoutedEventArgs e) => MainSections.SelectedItem = SettingsSection;

    private void AppearancePreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => QueueMaterialRefresh();
    private void AppearanceSessionChanged(object sender, SessionSwitchEventArgs e) => QueueMaterialRefresh();
    private void QueueMaterialRefresh()
    {
        if (!Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(new Action(() => { if (_windowLoaded && !_exitRequested) ApplyMaterial(); }));
    }




    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        var logs = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        Directory.CreateDirectory(logs);
        Process.Start(new ProcessStartInfo(logs) { UseShellExecute = true });
    }
    private void Report(string text) => Status.Text = DateTime.Now.ToString("HH:mm:ss") + "  " + text;
    private void RestoreWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exitRequested)
        {
            e.Cancel = true;
            Hide();
            _trayIcon.ShowBalloonTip(2500, DesktopIdentity.DisplayName, "Still running in the system tray. Click the icon to reopen; choose Exit to stop the service.", Forms.ToolTipIcon.Info);
        }
        base.OnClosing(e);
    }
    protected override void OnClosed(EventArgs e)
    {
        _updateCancellation?.Cancel();
        _statusTimer.Stop();
        SystemEvents.UserPreferenceChanged -= AppearancePreferenceChanged;
        SystemEvents.SessionSwitch -= AppearanceSessionChanged;
        _activityTimer.Stop();
        // The transactional updater owns the prepared Host until it terminates it.
        if (!_handoffToUpdater) StopBridge();
        _trayIcon.Visible = false;
        _trayIcon.ContextMenuStrip?.Dispose();
        _trayIcon.Dispose();
        base.OnClosed(e);
    }
}

internal static class FirewallRule
{
    public static bool TryEnsure(int port)
    {
        try
        {
            var rule = "Zemax MCP HTTPS " + port;
            var arguments = "advfirewall firewall add rule name=\"" + rule + "\" dir=in action=allow protocol=TCP localport=" + port + " profile=private";
            // Kestrel owns TLS directly; no HTTP.SYS URL ACL or certificate
            // binding is necessary. Only the private-network firewall needs UAC.
            using (var process = Process.Start(new ProcessStartInfo("netsh.exe", arguments) { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden }))
            {
                process.WaitForExit();
                return process.ExitCode == 0;
            }
        }
        catch { return false; }
    }
}
