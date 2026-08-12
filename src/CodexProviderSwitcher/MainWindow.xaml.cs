using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CodexProviderSwitcher.Core;
using Microsoft.Win32;

namespace CodexProviderSwitcher;

public partial class MainWindow : Window
{
    private enum AppPage
    {
        Home,
        Providers,
        Diagnostics,
        Backups,
        Settings
    }

    private sealed record BackupRow(
        DateTime Timestamp,
        string DisplayTime,
        long SizeBytes,
        string DisplaySize,
        string FolderName,
        string ConfigPath);

    private readonly ConfigService _configService = new();
    private readonly ProviderSwitchWorkflowService _switchWorkflow;
    private readonly BackupCatalogService _backupCatalogService = new();
    private readonly SettingsStore _settingsStore = new();
    private readonly SessionHealthService _sessionHealthService = new();
    private readonly ConnectionTestService _connectionTestService = new();
    private readonly ModelDiscoveryService _modelDiscoveryService = new();
    private readonly WslKimiRouterService _wslKimiRouterService = new();
    private readonly HostCapabilityDiagnosticsService _hostDiagnosticsService = new();
    private readonly CodexProcessService _processService = new();
    private readonly LunaWorkerAgentService _lunaWorkerAgentService = new();
    private readonly GitHubReleaseUpdateService _releaseUpdateService = new();
    private SwitcherSettings _settings = new();
    private SettingsLoadResult? _settingsLoadResult;
    private bool _isBusy;
    private bool _isInitialized;
    private bool _systemThemeEventsSubscribed;
    private AppPage _currentPage = AppPage.Home;
    private HostCapabilityDiagnostics? _lastHostDiagnostics;
    private ConfigStatus? _lastConfigStatus;
    private ManagedAgentStatus? _lunaWorkerAgentStatus;
    private ReleaseUpdateInfo? _releaseUpdateInfo;
    private bool _updateCheckCompleted;
    private bool _updateCheckFailed;
    private bool _isCheckingForUpdates;
    private bool _solUltraAvailable;
    private string? _configurationRestartWarning;
    private bool _isRefreshingProviderProfiles;
    // The picker is deliberately a draft selector.  A saved account is not
    // made active just because the user looked at it; the active profile is
    // updated only after a provider switch has completed successfully.
    private string? _selectedProviderProfileId;
    private bool _isNewProviderProfileDraft;

    public MainWindow()
    {
        _switchWorkflow = new ProviderSwitchWorkflowService(_configService);
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        SizeChanged += MainWindow_SizeChanged;
        Closed += MainWindow_Closed;
    }

    private string TokenBrokerPath =>
        Path.Combine(AppContext.BaseDirectory, "CodexProviderToken.exe");

    private string KimiWslLauncherPath =>
        Path.Combine(AppContext.BaseDirectory, AppPaths.KimiWslLauncherFileName);

    private ProviderProfile ActiveProviderProfile =>
        _settings.EnsureActiveProviderProfile();

    private string ActiveCredentialTarget => ActiveProviderProfile.CredentialTarget;

    private ProviderProfile? SelectedProviderProfile =>
        !string.IsNullOrWhiteSpace(_selectedProviderProfileId)
            ? _settings.ProviderProfiles.FirstOrDefault(profile =>
                string.Equals(
                    profile.Id,
                    _selectedProviderProfileId,
                    StringComparison.Ordinal))
            : null;

    private ProviderProfile? DraftProviderProfile =>
        _isNewProviderProfileDraft ? null : SelectedProviderProfile;

    private bool IsKimiProviderProfile()
    {
        return IsKimiProfile(ActiveProviderProfile);
    }

    private static bool IsKimiProfile(ProviderProfile profile) =>
        profile.Kind == ProviderKinds.Kimi &&
               SettingsStore.IsKimiBaseUrl(profile.BaseUrl) &&
               string.Equals(
                   profile.Model,
                   AppPaths.DefaultKimiModel,
                   StringComparison.Ordinal);

    private static bool IsSuiXiangProfile(ProviderProfile profile) =>
        !IsKimiProfile(profile) &&
        (profile.Kind == ProviderKinds.SuiXiang ||
         IsSuiXiangBaseUrl(profile.BaseUrl));

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var status = _configService.ReadStatus();
            _settingsLoadResult = _settingsStore.LoadWithStatus(status);
            _settings = _settingsLoadResult.Settings;
            _selectedProviderProfileId = _settings.ActiveProviderProfileId;
            Localizer.Use(_settings.UiLanguage);
            ThemeManager.Apply(_settings.UiTheme);
            ApplyLanguage();
            UpdateVersionText();
            RefreshLunaWorkerAgentStatus(status);
            RefreshSolUltraSetting();
            BaseUrlTextBox.Text = _settings.ThirdPartyBaseUrl;
            ModelComboBox.Text = _settings.ThirdPartyModel;
            RefreshProviderProfilePicker();
            // Every provider change is a stop -> write/verify -> start
            // transaction.  Keep the legacy persisted preference for
            // compatibility, but it no longer changes that safety boundary.
            _settings.RestartAfterSwitch = true;
            RestartCheckBox.IsChecked = true;
            OpenGeneratedImageButton.IsEnabled = HasGeneratedImage();
            UpdatePersistedProviderCapabilityStatuses();
            RefreshBackups();
            await RefreshStatusAsync();
            await RefreshCapabilitiesAsync();
            _isInitialized = true;
            ChineseLanguageButton.IsEnabled = true;
            EnglishLanguageButton.IsEnabled = true;
            LightThemeButton.IsEnabled = true;
            DarkThemeButton.IsEnabled = true;
            SystemThemeButton.IsEnabled = true;
            UpdateThemeButtons();
            HomeNavigationButton.IsChecked = true;
            NavigateTo(AppPage.Home);
            SubscribeToSystemThemeEvents();
            UpdateResponsiveLayout();
            OperationStatusText.Text = _settingsLoadResult.RecoveryNotice ??
                                       T("就绪", "Ready");
            MainScrollViewer.ScrollToTop();
            Keyboard.ClearFocus();
            Focus();

            if (!_settings.OnboardingCompleted)
            {
                await RunSetupWizardAsync();
            }

            _ = CheckForUpdatesAsync(isManual: false);
        }
        catch (Exception exception)
        {
            ShowFailure(T("初始化失败", "Initialization failed"), exception);
        }
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateResponsiveLayout();

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        if (!_systemThemeEventsSubscribed)
        {
            return;
        }

        SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
        _systemThemeEventsSubscribed = false;
    }

    private void SubscribeToSystemThemeEvents()
    {
        if (_systemThemeEventsSubscribed)
        {
            return;
        }

        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
        _systemThemeEventsSubscribed = true;
    }

    private void SystemEvents_UserPreferenceChanged(
        object sender,
        UserPreferenceChangedEventArgs e)
    {
        if (ThemePreference.Parse(_settings.UiTheme) != UiThemePreference.System)
        {
            return;
        }

        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (!IsLoaded ||
                Dispatcher.HasShutdownStarted ||
                Dispatcher.HasShutdownFinished)
            {
                return;
            }

            try
            {
                ThemeManager.Apply(_settings.UiTheme);
                if (_lastConfigStatus is { } status)
                {
                    UpdateModeBadge(status);
                }
                UpdatePersistedProviderCapabilityStatuses();
                UpdateHostStatusDots();
            }
            catch (Exception exception)
            {
                OperationStatusText.Text = F(
                    "无法应用 Windows 外观变化：{0}",
                    "Could not apply the Windows appearance change: {0}",
                    exception.Message);
            }
        });
    }

    private void NavigationButton_Checked(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized || sender is not RadioButton button)
        {
            return;
        }

        var page = ReferenceEquals(button, HomeNavigationButton)
            ? AppPage.Home
            : ReferenceEquals(button, ProvidersNavigationButton)
                ? AppPage.Providers
                : ReferenceEquals(button, DiagnosticsNavigationButton)
                    ? AppPage.Diagnostics
                    : ReferenceEquals(button, BackupsNavigationButton)
                        ? AppPage.Backups
                        : AppPage.Settings;
        NavigateTo(page);
    }

    private void NavigateTo(AppPage page)
    {
        _currentPage = page;
        MainScrollViewer.Visibility =
            page == AppPage.Home ? Visibility.Visible : Visibility.Collapsed;
        ProvidersPageScrollViewer.Visibility =
            page == AppPage.Providers ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsPageScrollViewer.Visibility =
            page == AppPage.Diagnostics ? Visibility.Visible : Visibility.Collapsed;
        BackupsPage.Visibility =
            page == AppPage.Backups ? Visibility.Visible : Visibility.Collapsed;
        SettingsPageScrollViewer.Visibility =
            page == AppPage.Settings ? Visibility.Visible : Visibility.Collapsed;

        switch (page)
        {
            case AppPage.Home:
                MainScrollViewer.ScrollToTop();
                break;
            case AppPage.Providers:
                ProvidersPageScrollViewer.ScrollToTop();
                break;
            case AppPage.Diagnostics:
                DiagnosticsPageScrollViewer.ScrollToTop();
                break;
            case AppPage.Backups:
                RefreshBackups();
                break;
            case AppPage.Settings:
                SettingsPageScrollViewer.ScrollToTop();
                break;
        }

        UpdatePageTitle();
    }

    private void UpdatePageTitle()
    {
        PageTitleText.Text = _currentPage switch
        {
            AppPage.Home => T("首页", "Home"),
            AppPage.Providers => T("供应商", "Providers"),
            AppPage.Diagnostics => T("能力诊断", "Diagnostics"),
            AppPage.Backups => T("备份记录", "Backups"),
            _ => T("设置", "Settings")
        };
    }

    private void UpdateResponsiveLayout()
    {
        if (!IsLoaded)
        {
            return;
        }

        var compact = ActualWidth < 820;
        NavigationColumn.Width = new GridLength(compact ? 68 : 210);
        NavigationBrandText.Visibility =
            compact ? Visibility.Collapsed : Visibility.Visible;
        NavStatusText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        VersionText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;

        foreach (var label in new[]
                 {
                     HomeNavigationText,
                     ProvidersNavigationText,
                     DiagnosticsNavigationText,
                     BackupsNavigationText,
                     SettingsNavigationText
                 })
        {
            label.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private async void HeaderRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            await RefreshStatusAsync();
            await RefreshCapabilitiesAsync();
            RefreshBackups();
            OperationStatusText.Text = T("状态已刷新。", "Status refreshed.");
        });
    }

    private async void RunSetupAgainButton_Click(object sender, RoutedEventArgs e)
    {
        await RunSetupWizardAsync();
    }

    private async void InstallLunaWorkerButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        var routeStatus = _configService.ReadStatus();
        if (LunaWorkerAgentService.IsSuiXiangRoute(routeStatus) ||
            routeStatus.Mode == ProviderMode.Unknown)
        {
            OperationStatusText.Text = T(
                LunaWorkerAgentService.IsSuiXiangRoute(routeStatus)
                    ? "随想目前不支持 Luna；请先切回官方线路再安装。"
                    : "请先连接官方或已确认支持 Luna 的第三方线路。",
                LunaWorkerAgentService.IsSuiXiangRoute(routeStatus)
                    ? "SuiXiang does not currently support Luna. Switch to the official route before installing it."
                    : "Connect to official or a third-party route confirmed to support Luna first.");
            return;
        }

        await RunBusyAsync(() =>
        {
            var status = _lunaWorkerAgentService.Install();
            _lunaWorkerAgentStatus = status;
            UpdateLunaWorkerAgentStatus();
            OperationStatusText.Text = status.State switch
            {
                ManagedAgentState.Installed => T(
                    "Luna 任务 Agent 已安装；如 Codex 尚未识别，请重启 Codex。",
                    "Luna task agent is installed. Restart Codex if it is not recognized yet."),
                ManagedAgentState.Conflict => T(
                    "检测到同名自定义 Luna Agent，未覆盖。",
                    "A custom Luna agent already exists; it was not overwritten."),
                _ => T(
                    "Luna 任务 Agent 尚未安装。",
                    "The Luna task agent is not installed.")
            };
            return Task.CompletedTask;
        });
    }

    private void OpenLunaAgentsFolderButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        OpenFolder(AppPaths.AgentsDirectory);
        OperationStatusText.Text = T(
            "已打开 Codex agents 文件夹。",
            "Opened the Codex agents folder.");
    }

    private void RefreshLunaWorkerAgentStatus(ConfigStatus? configStatus = null)
    {
        configStatus ??= _configService.ReadStatus();
        _lunaWorkerAgentStatus = _lunaWorkerAgentService.Reconcile(configStatus);
        UpdateLunaWorkerAgentStatus(configStatus);
    }

    private void UpdateLunaWorkerAgentStatus(ConfigStatus? routeStatus = null)
    {
        var status = _lunaWorkerAgentStatus;
        routeStatus ??= _configService.ReadStatus();
        var officialRoute = routeStatus.Mode == ProviderMode.Official;
        var suiXiangRoute = LunaWorkerAgentService.IsSuiXiangRoute(routeStatus);
        var routeAllowsLuna = routeStatus.Mode != ProviderMode.Unknown &&
                              !suiXiangRoute;
        if (status is null)
        {
            LunaWorkerStatusText.Text = T("正在检测…", "Checking…");
            InstallLunaWorkerButton.Content = T(
                "安装 Luna Agent",
                "Install Luna agent");
            InstallLunaWorkerButton.IsEnabled = false;
            OpenLunaAgentsFolderButton.IsEnabled = !_isBusy;
            return;
        }

        LunaWorkerStatusText.Text = status.State switch
        {
            ManagedAgentState.Installed when officialRoute => T(
                "已安装（gpt-5.6-luna / max）",
                "Installed (gpt-5.6-luna / max)"),
            ManagedAgentState.Installed => T(
                "已安装（请确认当前供应商支持 Luna）",
                "Installed (confirm Luna support with this provider)"),
            ManagedAgentState.Disabled => T(
                "已停用（随想不支持；官方线路会自动恢复）",
                "Disabled (SuiXiang unsupported; restored on official)"),
            ManagedAgentState.Conflict when suiXiangRoute => T(
                "停用失败（文件冲突）",
                "Disable failed (file conflict)"),
            ManagedAgentState.Conflict => T(
                "已有自定义文件或无法访问",
                "Custom or inaccessible file found"),
            _ => T(
                "未安装（可选）",
                "Not installed (optional)")
        };
        InstallLunaWorkerButton.Content = status.State switch
        {
            ManagedAgentState.Installed => T("已安装", "Installed"),
            ManagedAgentState.Disabled => T("当前已停用", "Currently disabled"),
            ManagedAgentState.Conflict when suiXiangRoute => T(
                "需要处理冲突",
                "Resolve conflict"),
            ManagedAgentState.Conflict => T(
                "文件冲突",
                "File conflict"),
            _ => T("安装 Luna Agent", "Install Luna agent")
        };
        InstallLunaWorkerButton.ToolTip = status.State switch
        {
            ManagedAgentState.Installed => T(
                officialRoute
                    ? "Luna 任务 Agent 已存在，无需重复安装。"
                    : "该 Agent 已安装；当前第三方是否支持 Luna 取决于供应商。",
                officialRoute
                    ? "The Luna task agent is already installed."
                    : "The agent is installed; Luna support on this route depends on the provider."),
            ManagedAgentState.Disabled => T(
                suiXiangRoute
                    ? "随想目前不支持 Luna；切回官方后会自动恢复。"
                    : "Luna 文件曾因随想线路而停用；切回官方后会自动恢复。",
                suiXiangRoute
                    ? "SuiXiang does not currently support Luna; it is restored after switching to official."
                    : "The Luna file was disabled for SuiXiang and will be restored after switching to official."),
            ManagedAgentState.Conflict => T(
                suiXiangRoute
                    ? "未能停用 Luna：目标文件已存在或无法访问。活动 Agent 可能仍可见；请打开 agents 文件夹处理冲突。任何现有文件都未被覆盖。"
                    : "检测到自定义文件或文件无法访问。为保护现有配置，不会移动或覆盖它。",
                suiXiangRoute
                    ? "Luna could not be disabled because the target exists or is inaccessible. The active agent may remain visible; open the agents folder to resolve it. No file was overwritten."
                    : "A custom file exists or a file is inaccessible. It will not be moved or overwritten."),
            _ => T(
                officialRoute
                    ? "安装 gpt-5.6-luna、max 的 Luna 任务 Agent。"
                    : suiXiangRoute
                        ? "随想目前不支持 Luna；请先切回官方。"
                        : "安装前请确认当前第三方供应商支持 gpt-5.6-luna。",
                officialRoute
                    ? "Install the gpt-5.6-luna, max Luna task agent."
                    : suiXiangRoute
                        ? "SuiXiang does not currently support Luna. Switch back to official first."
                        : "Confirm that this provider supports gpt-5.6-luna before installing.")
        };
        OpenLunaAgentsFolderButton.ToolTip = T(
            "打开 Codex agents 文件夹查看配置。",
            "Open the Codex agents folder to inspect the configuration.");
        InstallLunaWorkerButton.IsEnabled =
            !_isBusy && routeAllowsLuna && status.State == ManagedAgentState.Missing;
        OpenLunaAgentsFolderButton.IsEnabled = !_isBusy;
    }

    private void RefreshSolUltraSetting()
    {
        _solUltraAvailable = _configService.ReadSolUltraAvailability();
        UpdateSolUltraStatus();
    }

    private void UpdateSolUltraStatus()
    {
        SolUltraStatusText.Text = _solUltraAvailable
            ? T("Ultra 已可用", "Ultra available")
            : T("尚未启用", "Not enabled yet");
        EnableSolUltraButton.Content = _solUltraAvailable
            ? T("Ultra 已可用", "Ultra available")
            : T("启用并重启 Codex", "Enable and restart Codex");
        EnableSolUltraButton.ToolTip = _solUltraAvailable
            ? T(
                "简体中文 Codex 中，Ultra 是菜单最底部带“更快消耗使用额度”的“极高”。",
                "In Simplified Chinese Codex, Ultra is the bottom 'Extremely high' item with the faster usage warning.")
            : T(
                "启用 Sol Ultra 后重启 Codex。",
                "Enable Sol Ultra and restart Codex.");
        EnableSolUltraButton.IsEnabled = !_isBusy && !_solUltraAvailable;
    }

    private async void EnableSolUltraButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!_isInitialized || _solUltraAvailable)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            OperationStatusText.Text = T(
                "正在关闭 Codex，然后安全写入 Ultra 启用请求…",
                "Closing Codex before safely writing the Ultra enablement request...");
            await _processService.StopAsync();

            string? backupFolder;
            try
            {
                backupFolder = _configService.RequestSolUltraEnablement();
            }
            catch
            {
                await _processService.StartAsync();
                throw;
            }

            OperationStatusText.Text = T(
                "Ultra 启用请求已写入，正在启动 Codex…",
                "Ultra enablement was requested. Starting Codex...");
            await _processService.StartAsync();
            _solUltraAvailable = await WaitForSolUltraAvailabilityAsync();
            OperationStatusText.Text = _solUltraAvailable
                ? F(
                    "Sol Ultra 已可用。简体中文菜单中是最底部带“更快消耗使用额度”的“极高”。备份：{0}",
                    "Sol Ultra is available. In Simplified Chinese it is the bottom item with the faster usage warning. Backup: {0}",
                    backupFolder ?? T("无需写入", "No write needed"))
                : F(
                    "已请求启用 Ultra；Codex 仍在完成启动。备份：{0}",
                    "Ultra enablement was requested; Codex is still finishing startup. Backup: {0}",
                    backupFolder ?? T("无需写入", "No write needed"));
        });

        RefreshSolUltraSetting();
        RefreshBackups();
    }

    private async Task<bool> WaitForSolUltraAvailabilityAsync()
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (_configService.ReadSolUltraAvailability())
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(400));
        }

        return false;
    }

    private async void UpdateActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_releaseUpdateInfo is { IsUpdateAvailable: true })
        {
            OpenLatestRelease();
            return;
        }

        await CheckForUpdatesAsync(isManual: true);
    }

    private void OpenUpdateReleaseButton_Click(object sender, RoutedEventArgs e) =>
        OpenLatestRelease();

    private async Task CheckForUpdatesAsync(bool isManual)
    {
        if (_isCheckingForUpdates)
        {
            return;
        }

        _isCheckingForUpdates = true;
        _updateCheckFailed = false;
        UpdateUpdateCheckUi();
        try
        {
            _releaseUpdateInfo = await _releaseUpdateService.CheckAsync(
                CurrentApplicationVersion());
            _updateCheckCompleted = true;
            if (_releaseUpdateInfo.IsUpdateAvailable)
            {
                OperationStatusText.Text = F(
                    "发现新版本 {0}，可从 GitHub 下载。",
                    "Version {0} is available on GitHub.",
                    _releaseUpdateInfo.LatestTag);
            }
            else if (isManual)
            {
                OperationStatusText.Text = T(
                    "当前已经是最新版。",
                    "You already have the latest version.");
            }
        }
        catch (Exception)
        {
            _releaseUpdateInfo = null;
            _updateCheckCompleted = true;
            _updateCheckFailed = true;
            if (isManual)
            {
                OperationStatusText.Text = T(
                    "暂时无法检查 GitHub 更新，请稍后重试。",
                    "Could not check GitHub for updates. Try again later.");
            }
        }
        finally
        {
            _isCheckingForUpdates = false;
            UpdateUpdateCheckUi();
        }
    }

    private void OpenLatestRelease()
    {
        if (_releaseUpdateInfo is not { IsUpdateAvailable: true } update)
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = update.ReleaseUri.AbsoluteUri,
            UseShellExecute = true
        });
    }

    private void UpdateUpdateCheckUi()
    {
        var update = _releaseUpdateInfo is { IsUpdateAvailable: true } candidate
            ? candidate
            : null;
        var updateAvailable = update is not null;
        UpdateAvailableBanner.Visibility =
            updateAvailable ? Visibility.Visible : Visibility.Collapsed;

        if (update is not null)
        {
            UpdateBannerTitleText.Text = F(
                "发现新版本 {0}",
                "Version {0} is available",
                update.LatestTag);
            UpdateBannerDescriptionText.Text = T(
                "GitHub 已发布新的正式版本，打开下载页即可升级。",
                "A new stable release is available on GitHub. Open the download page to upgrade.");
        }

        UpdateCheckStatusText.Text = _isCheckingForUpdates
            ? T("正在检查 GitHub...", "Checking GitHub...")
            : updateAvailable
                ? F(
                    "可更新到 {0}",
                    "Update available: {0}",
                    update!.LatestTag)
                : _updateCheckFailed
                    ? T("检查失败，可重试", "Check failed; retry available")
                    : _updateCheckCompleted
                        ? F(
                            "已是最新版（v{0}）",
                            "Up to date (v{0})",
                            CurrentApplicationVersion().ToString(3))
                        : T("等待自动检查", "Waiting for automatic check");
        UpdateActionButton.Content = _isCheckingForUpdates
            ? T("正在检查", "Checking")
            : updateAvailable
                ? T("打开下载页", "Open download page")
                : T("立即检查", "Check now");
        UpdateActionButton.Tag = updateAvailable ? "\uE895" : "\uE72C";
        UpdateActionButton.ToolTip = updateAvailable
            ? T(
                "在浏览器中打开 GitHub Release 下载页",
                "Open the GitHub Release download page in your browser")
            : T(
                "立即检查 GitHub 上的最新正式版本",
                "Check GitHub for the latest stable release now");
        OpenUpdateReleaseButton.Content = T("打开下载页", "Open download page");
        OpenUpdateReleaseButton.ToolTip = T(
            "在浏览器中打开 GitHub Release 下载页",
            "Open the GitHub Release download page in your browser");
        UpdateActionButton.IsEnabled = !_isBusy && !_isCheckingForUpdates;
        OpenUpdateReleaseButton.IsEnabled = !_isBusy;
    }

    private async Task RunSetupWizardAsync()
    {
        SetupWizardResult? draft = null;
        while (true)
        {
            var wizard = new SetupWizardWindow(_settings, draft)
            {
                Owner = this
            };
            if (wizard.ShowDialog() != true || wizard.Result is null)
            {
                // A language selection made inside the wizard is useful even if
                // the user defers provider setup. No provider data is changed.
                _settingsStore.Save(_settings);
                OperationStatusText.Text = T(
                    "设置已保留，当前线路未改动。",
                    "Setup was deferred; the current route was not changed.");
                return;
            }

            var applied = false;
            await RunBusyAsync(async () =>
            {
                await ApplySetupResultAsync(wizard.Result);
                applied = true;
            });
            if (applied)
            {
                return;
            }

            // Keep the entered key only in process memory while the user fixes
            // a validation error. It is never written until validation passes.
            draft = wizard.Result;
        }
    }

    private async Task ApplySetupResultAsync(SetupWizardResult setup)
    {
        if (setup.UseOfficial)
        {
            var current = _configService.ReadStatus();
            if (current.Mode != ProviderMode.Official)
            {
                if (!File.Exists(AppPaths.ConfigPath))
                {
                    throw new FileNotFoundException(T(
                        "未找到 Codex config.toml。请先启动一次官方 Codex 后再设置。",
                        "Codex config.toml was not found. Start official Codex once before setup."),
                        AppPaths.ConfigPath);
                }

                OperationStatusText.Text = T(
                    "正在切换到官方 Codex…",
                    "Switching to official Codex...");
                var switchRequest = new OfficialSwitchRequest(
                    _settings.OfficialModel,
                    _settings.OfficialReviewModel);
                var switchResult = await SwitchToOfficialFromCurrentConfigAsync(
                    switchRequest);
                RefreshLunaWorkerAgentStatus(switchResult.VerifiedStatus);
                var setupConfigurationRestartWarning = _configurationRestartWarning;
                OperationStatusText.Text = string.IsNullOrWhiteSpace(
                    setupConfigurationRestartWarning)
                    ? F(
                        "已切换到官方 Codex；已完成配置写入并重新启动 Codex。备份：{0}",
                        "Switched to official Codex; the configuration was written and Codex was restarted. Backup: {0}",
                        switchResult.BackupFolder)
                    : F(
                        "官方配置已写入并验证，但 Codex 启动未确认。备份：{0}。{1}",
                        "The official configuration was written and verified, but Codex startup was not confirmed. Backup: {0}. {1}",
                        switchResult.BackupFolder,
                        setupConfigurationRestartWarning);
            }

            CompleteOnboarding();
            await RefreshStatusAsync();
            RefreshBackups();
            return;
        }

        var baseUrl = ConfigService.NormalizeBaseUrl(setup.BaseUrl);
        var model = setup.Model.Trim();
        if (setup.ProviderKind == ProviderKinds.Kimi)
        {
            ProviderAvailabilityPolicy.RequireKimiRouteEnabled();
        }
        ProviderAvailabilityPolicy.RequireAvailableThirdPartyRoute(baseUrl, model);
        var apiKey = setup.ApiKey?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException(T(
                "请填写模型。",
                "Enter a model."));
        }

        if (setup.ProviderKind == ProviderKinds.Kimi)
        {
            ValidateKimiModel(model);
            var reuseExistingProfile = apiKey.Length == 0;
            if (apiKey.Length == 0)
            {
                apiKey = ResolveSavedKeyForExactRoute(
                    baseUrl,
                    model,
                    ProviderKinds.Kimi);
            }
            else if (apiKey.Length < 16)
            {
                throw new InvalidOperationException(T(
                    "请粘贴完整的新随想 API Key。",
                    "Paste a complete new SuiXiang API key."));
            }
            await ApplyKimiSetupResultAsync(
                setup,
                baseUrl,
                model,
                apiKey,
                reuseExistingProfile);
            return;
        }

        if (apiKey.Length < 16)
        {
            throw new InvalidOperationException(T(
                "请填写完整的新 API Key。",
                "Enter a complete new API key."));
        }

        OperationStatusText.Text = T(
            "正在验证第三方 Responses API…",
            "Validating the third-party Responses API...");
        var compatibility = await _connectionTestService.TestResponsesApiAsync(
            baseUrl,
            model,
            apiKey);
        if (!compatibility.Success)
        {
            throw new InvalidOperationException(compatibility.Summary);
        }

        var previousActiveProfileId = _settings.ActiveProviderProfileId;
        var previousThirdPartyBaseUrl = _settings.ThirdPartyBaseUrl;
        var previousThirdPartyModel = _settings.ThirdPartyModel;
        var profile = SelectOrCreateProfileForSetup(
            baseUrl,
            model,
            setup.ProviderKind,
            reuseExistingProfile: false,
            out var createdProfile);
        var before = CloneProviderProfile(profile);
        var target = CredentialTargetFactory.RequireValid(profile.CredentialTarget);
        var previousKey = CredentialVault.Read(target);
        var switched = false;
        try
        {
            profile.Kind = setup.ProviderKind;
            profile.DisplayName = ResolveSetupDisplayName(setup, baseUrl);
            profile.BaseUrl = baseUrl;
            profile.Model = model;
            _settings.ThirdPartyBaseUrl = baseUrl;
            _settings.ThirdPartyModel = model;

            CredentialVault.Write(target, apiKey);
            var switchResult = await SwitchToThirdPartyFromCurrentConfigAsync(
                new ThirdPartySwitchRequest(
                    model,
                    baseUrl,
                    TokenBrokerPath,
                    target));
            switched = true;
            RefreshLunaWorkerAgentStatus(switchResult.VerifiedStatus);

            _settings.LastSuccessfulCompatibilityTestUtc = DateTimeOffset.UtcNow;
            _settings.LastTestedEndpointFingerprint =
                ConnectionTestService.EndpointFingerprint(baseUrl, model);
            CompleteOnboarding();
            BaseUrlTextBox.Text = baseUrl;
            ModelComboBox.Text = model;
            var configurationRestartWarning = _configurationRestartWarning;
            OperationStatusText.Text = string.IsNullOrWhiteSpace(
                configurationRestartWarning)
                ? F(
                    "已连接 {0}；已完成配置写入并重新启动 Codex，历史分区保持 {1}。备份：{2}",
                    "Connected to {0}; the configuration was written and Codex was restarted. The history partition remains {1}. Backup: {2}",
                    profile.DisplayName,
                    AppPaths.StableProviderId,
                    switchResult.BackupFolder)
                : F(
                    "{0} 配置已写入并验证，但 Codex 启动未确认；历史分区仍为 {1}。备份：{2}。{3}",
                    "The {0} configuration was written and verified, but Codex startup was not confirmed; the history partition remains {1}. Backup: {2}. {3}",
                    profile.DisplayName,
                    AppPaths.StableProviderId,
                    switchResult.BackupFolder,
                    configurationRestartWarning);
        }
        catch when (!switched)
        {
            RestoreProviderProfile(
                profile,
                before,
                createdProfile,
                previousActiveProfileId,
                save: false);
            _settings.ThirdPartyBaseUrl = previousThirdPartyBaseUrl;
            _settings.ThirdPartyModel = previousThirdPartyModel;
            if (previousKey is null)
            {
                CredentialVault.Delete(target);
            }
            else
            {
                CredentialVault.Write(target, previousKey);
            }

            throw;
        }

        await RefreshStatusAsync();
        RefreshBackups();
    }

    private async Task ApplyKimiSetupResultAsync(
        SetupWizardResult setup,
        string upstreamBaseUrl,
        string model,
        string apiKey,
        bool reuseExistingProfile)
    {
        if (!SettingsStore.IsKimiBaseUrl(upstreamBaseUrl))
        {
            throw new InvalidOperationException(T(
                "随想 K3 实验线路必须使用 https://sui-xiang.com/v1；本机路由器不会把凭据转发到其他上游。",
                "The SuiXiang K3 experimental route requires https://sui-xiang.com/v1; the local router will not forward credentials to another upstream."));
        }

        var previousActiveProfileId = _settings.ActiveProviderProfileId;
        var previousThirdPartyBaseUrl = _settings.ThirdPartyBaseUrl;
        var previousThirdPartyModel = _settings.ThirdPartyModel;
        var previousOfficialModel = _settings.OfficialModel;
        var previousOfficialReviewModel = _settings.OfficialReviewModel;
        var profile = SelectOrCreateProfileForSetup(
            upstreamBaseUrl,
            model,
            ProviderKinds.Kimi,
            reuseExistingProfile,
            out var createdProfile);
        var before = CloneProviderProfile(profile);
        var target = CredentialTargetFactory.RequireValid(profile.CredentialTarget);
        var previousKey = CredentialVault.Read(target);
        var current = _configService.ReadStatus();
        if (current.Mode == ProviderMode.Official)
        {
            if (!string.IsNullOrWhiteSpace(current.Model))
            {
                _settings.OfficialModel = current.Model;
            }

            _settings.OfficialReviewModel = current.ReviewModel;
        }

        var switched = false;
        try
        {
            profile.Kind = ProviderKinds.Kimi;
            profile.DisplayName = ResolveSetupDisplayName(setup, upstreamBaseUrl);
            profile.BaseUrl = upstreamBaseUrl;
            profile.Model = model;
            _settings.ThirdPartyBaseUrl = upstreamBaseUrl;
            _settings.ThirdPartyModel = model;

            CredentialVault.Write(target, apiKey);
            var switchResult = await EnsureAndSwitchToKimiAsync(
                model,
                apiKey,
                target);
            switched = true;
            RefreshLunaWorkerAgentStatus(switchResult.VerifiedStatus);

            _settings.LastSuccessfulCompatibilityTestUtc = DateTimeOffset.UtcNow;
            _settings.LastTestedEndpointFingerprint =
                ConnectionTestService.EndpointFingerprint(
                    AppPaths.KimiRouterBaseUrl,
                    model);
            CompleteOnboarding();
            BaseUrlTextBox.Text = upstreamBaseUrl;
            ModelComboBox.Text = model;
            var configurationRestartWarning = _configurationRestartWarning;
            OperationStatusText.Text = string.IsNullOrWhiteSpace(
                configurationRestartWarning)
                ? F(
                    "已连接随想 K3（实验）；已完成配置写入并重新启动 Codex，历史分区保持 {0}。备份：{1}",
                    "Connected to SuiXiang K3 (experimental); the configuration was written and Codex was restarted. The history partition remains {0}. Backup: {1}",
                    AppPaths.StableProviderId,
                    switchResult.BackupFolder)
                : F(
                    "随想 K3 配置已写入并验证，但 Codex 启动未确认；历史分区仍为 {0}。备份：{1}。{2}",
                    "The SuiXiang K3 configuration was written and verified, but Codex startup was not confirmed; the history partition remains {0}. Backup: {1}. {2}",
                    AppPaths.StableProviderId,
                    switchResult.BackupFolder,
                    configurationRestartWarning);
        }
        catch when (!switched)
        {
            _settings.ThirdPartyBaseUrl = previousThirdPartyBaseUrl;
            _settings.ThirdPartyModel = previousThirdPartyModel;
            _settings.OfficialModel = previousOfficialModel;
            _settings.OfficialReviewModel = previousOfficialReviewModel;
            RestoreProviderProfile(profile, before, createdProfile, previousActiveProfileId);
            if (previousKey is null)
            {
                CredentialVault.Delete(target);
            }
            else
            {
                CredentialVault.Write(target, previousKey);
            }

            throw;
        }

        await RefreshStatusAsync();
        RefreshBackups();
        // Every route is switched as the same config transaction.
    }

    private ProviderProfile SelectOrCreateProfileForSetup(
        string normalizedBaseUrl,
        string model,
        string requiredKind,
        bool reuseExistingProfile,
        out bool created)
    {
        var exactMatches = ProviderProfileRouteMatcher.FindExact(
            _settings.ProviderProfiles,
            normalizedBaseUrl,
            model,
            requiredKind);
        // A single exact route is safe to reuse only when the wizard is
        // explicitly reusing its saved credential. Pasted credentials always
        // receive a fresh isolated profile, so setup cannot overwrite an
        // existing account by accident.
        if (reuseExistingProfile && exactMatches.Count != 1)
        {
            throw new InvalidOperationException(T(
                exactMatches.Count > 1
                    ? "这个线路有多个已保存账号，无法猜测要使用哪一个。请在供应商页明确选择账号，或粘贴新的 API Key。"
                    : "这个线路没有唯一的已保存账号，请粘贴完整的 API Key。",
                exactMatches.Count > 1
                    ? "Multiple saved accounts match this route. Select one explicitly on Providers, or paste a new API key."
                    : "No unique saved account matches this route. Paste a complete API key."));
        }

        var profile = reuseExistingProfile && exactMatches.Count == 1
            ? exactMatches[0]
            : null;
        created = profile is null;
        if (profile is null)
        {
            var profileId = Guid.NewGuid().ToString("N");
            profile = new ProviderProfile
            {
                Id = profileId,
                Kind = requiredKind,
                BaseUrl = normalizedBaseUrl,
                Model = model,
                CredentialTarget = CredentialTargetFactory.CreateForProfileId(profileId)
            };
            _settings.ProviderProfiles.Add(profile);
        }
        else
        {
            if (!Guid.TryParse(profile.Id, out _))
            {
                profile.Id = Guid.NewGuid().ToString("N");
            }

            if (!CredentialTargetFactory.IsValid(profile.CredentialTarget))
            {
                profile.CredentialTarget =
                    CredentialTargetFactory.CreateForProfileId(profile.Id);
            }
        }

        _settings.ActiveProviderProfileId = profile.Id;
        return profile;
    }

    private string ResolveSavedKeyForExactRoute(
        string normalizedBaseUrl,
        string model,
        string requiredKind)
    {
        var exactMatches = ProviderProfileRouteMatcher.FindExact(
            _settings.ProviderProfiles,
            normalizedBaseUrl,
            model,
            requiredKind);
        if (exactMatches.Count != 1)
        {
            throw new InvalidOperationException(T(
                exactMatches.Count > 1
                    ? "该随想 K3 路由有多个已保存账号，请先选择账号或粘贴新密钥。"
                    : "该随想 K3 路由尚无已保存的 API Key，请粘贴新密钥。",
                exactMatches.Count > 1
                    ? "Multiple saved accounts match this SuiXiang K3 route; select an account or paste a new key."
                    : "No saved API key exists for this SuiXiang K3 route; paste a new key."));
        }

        var profile = exactMatches[0];
        if (!CredentialTargetFactory.IsValid(profile.CredentialTarget))
        {
            throw new InvalidOperationException(T(
                "该随想 K3 账号没有有效的凭据引用，请粘贴新密钥。",
                "This SuiXiang K3 account has no valid credential reference; paste a new key."));
        }

        return CredentialVault.Read(profile.CredentialTarget)
            ?? throw new InvalidOperationException(T(
                "该随想 K3 账号尚无已保存的 API Key，请粘贴新密钥。",
                "No saved API key exists for this SuiXiang K3 account; paste a new key."));
    }

    private static ProviderProfile CloneProviderProfile(ProviderProfile profile) =>
        new()
        {
            Id = profile.Id,
            Kind = profile.Kind,
            DisplayName = profile.DisplayName,
            BaseUrl = profile.BaseUrl,
            Model = profile.Model,
            CredentialTarget = profile.CredentialTarget
        };

    private void RestoreProviderProfile(
        ProviderProfile profile,
        ProviderProfile before,
        bool created,
        string? previousActiveProfileId,
        bool save = true)
    {
        if (created)
        {
            _settings.ProviderProfiles.Remove(profile);
        }
        else
        {
            profile.Id = before.Id;
            profile.Kind = before.Kind;
            profile.DisplayName = before.DisplayName;
            profile.BaseUrl = before.BaseUrl;
            profile.Model = before.Model;
            profile.CredentialTarget = before.CredentialTarget;
        }

        _settings.ActiveProviderProfileId = previousActiveProfileId;
        if (save)
        {
            _settingsStore.Save(_settings);
        }
    }

    private static void RestoreCredential(
        string credentialTarget,
        string? previousKey)
    {
        if (!CredentialTargetFactory.IsValid(credentialTarget))
        {
            return;
        }

        if (previousKey is null)
        {
            CredentialVault.Delete(credentialTarget);
        }
        else
        {
            CredentialVault.Write(credentialTarget, previousKey);
        }
    }

    private void CompleteOnboarding()
    {
        _settings.OnboardingCompleted = true;
        _settings.OnboardingVersion = Math.Max(_settings.OnboardingVersion, 1);
        _settingsStore.Save(_settings);
    }

    private static string ResolveSetupDisplayName(
        SetupWizardResult setup,
        string baseUrl)
    {
        if (!string.IsNullOrWhiteSpace(setup.DisplayName))
        {
            return setup.DisplayName.Trim();
        }

        if (setup.ProviderKind == ProviderKinds.SuiXiang)
        {
            return "SuiXiang";
        }

        if (setup.ProviderKind == ProviderKinds.Kimi)
        {
            return T("随想 K3（实验）", "SuiXiang K3 (experimental)");
        }

        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            ? uri.Host
            : baseUrl;
    }

    private void HomeOpenProvidersButton_Click(object sender, RoutedEventArgs e) =>
        ProvidersNavigationButton.IsChecked = true;

    private void HomeOpenDiagnosticsButton_Click(object sender, RoutedEventArgs e) =>
        DiagnosticsNavigationButton.IsChecked = true;

    private void HomeSwitchOfficialButton_Click(object sender, RoutedEventArgs e) =>
        SwitchOfficialButton_Click(sender, e);

    private void HomeSwitchThirdPartyButton_Click(object sender, RoutedEventArgs e) =>
        SwitchThirdPartyButton_Click(sender, e);

    private async void DailyPrimaryActionButton_Click(object sender, RoutedEventArgs e)
    {
        var status = _configService.ReadStatus();
        if (status.Mode == ProviderMode.Official &&
            ProviderAvailabilityPolicy.IsRetiredKimiProfile(ActiveProviderProfile))
        {
            ProvidersNavigationButton.IsChecked = true;
            OperationStatusText.Text = T(
                "K3 线路已停用，请添加或选择其他第三方线路。",
                "The K3 route has been retired. Add or select another third-party route.");
            return;
        }
        if (status.Mode == ProviderMode.Official &&
            CredentialVault.Exists(ActiveCredentialTarget))
        {
            SwitchThirdPartyButton_Click(sender, e);
            return;
        }

        if (status.Mode == ProviderMode.ThirdParty)
        {
            SwitchOfficialButton_Click(sender, e);
            return;
        }

        await RunSetupWizardAsync();
    }

    private async Task RefreshStatusAsync()
    {
        var status = _configService.ReadStatus();
        UpdateModeBadge(status);
        RefreshLunaWorkerAgentStatus(status);

        CurrentRouteText.Text = status.Mode switch
        {
            ProviderMode.Official =>
                T("你正在使用：官方 Codex", "You are using: Official Codex"),
            ProviderMode.ThirdParty =>
                F(
                    "你正在使用：{0}",
                    "You are using: {0}",
                    ResolveProviderDisplayName(status)),
            _ =>
                T("需要完成设置", "Setup required")
        };
        UpdateDailyPrimaryAction(status);
        RefreshProviderProfilePicker();

        UpdateKeyStatusText();

        HistoryStatusText.Text = T(
            "正在核对聊天历史文件…",
            "Checking chat history files...");
        var health = await _sessionHealthService.InspectAsync();
        HistoryStatusText.Text = F(
            "聊天历史：{0} 个会话文件；{1} 个在固定分区，{2} 个在其他分区，{3} 个不可读，{4} 个是 0 字节旧占位文件。",
            "Chat history: {0} session files; {1} in the stable partition, {2} in other partitions, {3} unreadable, and {4} legacy zero-byte placeholders.",
            health.TotalFiles,
            health.StableProviderFiles,
            health.OtherProviderFiles,
            health.UnreadableFiles,
            health.EmptyPlaceholderFiles);
    }

    private void UpdateDailyPrimaryAction(ConfigStatus status)
    {
        var retiredActiveProfile =
            ProviderAvailabilityPolicy.IsRetiredKimiProfile(ActiveProviderProfile);
        switch (status.Mode)
        {
            case ProviderMode.Official when retiredActiveProfile:
                DailyPrimaryActionButton.Content = T(
                    "选择可用线路",
                    "Choose an available route");
                DailyPrimaryActionButton.Tag = "\uE8AB";
                DailyPrimaryActionButton.Style = (Style)FindResource("PrimaryButton");
                break;
            case ProviderMode.Official when CredentialVault.Exists(ActiveCredentialTarget):
                DailyPrimaryActionButton.Content = F(
                    "切换到 {0}",
                    "Switch to {0}",
                    ResolveProviderDisplayName(status));
                DailyPrimaryActionButton.Tag = "\uE8AB";
                DailyPrimaryActionButton.Style = (Style)FindResource("PrimaryButton");
                break;
            case ProviderMode.Official:
                DailyPrimaryActionButton.Content = ActiveProviderProfile.Kind switch
                {
                    ProviderKinds.SuiXiang => T("连接随想", "Connect SuiXiang"),
                    ProviderKinds.Kimi => T("连接随想 K3（实验）", "Connect SuiXiang K3 (experimental)"),
                    _ => T("连接服务", "Connect a service")
                };
                DailyPrimaryActionButton.Tag = "\uE8AB";
                DailyPrimaryActionButton.Style = (Style)FindResource("PrimaryButton");
                break;
            case ProviderMode.ThirdParty:
                DailyPrimaryActionButton.Content = T(
                    "切换到官方 Codex",
                    "Switch to official Codex");
                DailyPrimaryActionButton.Tag = "\uE72E";
                DailyPrimaryActionButton.Style = (Style)FindResource("OfficialButton");
                break;
            default:
                DailyPrimaryActionButton.Content = T("开始设置", "Start setup");
                DailyPrimaryActionButton.Tag = "\uE72A";
                DailyPrimaryActionButton.Style = (Style)FindResource("PrimaryButton");
                break;
        }
    }

    private string ResolveProviderDisplayName(ConfigStatus status)
    {
        var profile = _settings.ProviderProfiles.FirstOrDefault(candidate =>
            string.Equals(
                candidate.CredentialTarget,
                status.CredentialTarget,
                StringComparison.Ordinal)) ?? ActiveProviderProfile;
        if (profile.Kind == ProviderKinds.SuiXiang)
        {
            return T("随想", "SuiXiang");
        }

        if ((profile.Kind == ProviderKinds.Kimi &&
             SettingsStore.IsKimiBaseUrl(profile.BaseUrl) &&
             string.Equals(
                 profile.Model,
                 AppPaths.DefaultKimiModel,
                 StringComparison.Ordinal)) ||
            SettingsStore.IsKimiLoopbackBaseUrl(status.BaseUrl))
        {
            return T("随想 K3（实验）", "SuiXiang K3 (experimental)");
        }

        if (!string.IsNullOrWhiteSpace(profile.DisplayName))
        {
            return profile.DisplayName;
        }

        return Uri.TryCreate(status.BaseUrl, UriKind.Absolute, out var uri)
            ? uri.Host
            : status.BaseUrl ?? T("第三方服务", "Third-party service");
    }

    private async void ChineseLanguageButton_Checked(object sender, RoutedEventArgs e)
    {
        await ChangeLanguageAsync(AppLanguage.Chinese);
    }

    private async void EnglishLanguageButton_Checked(object sender, RoutedEventArgs e)
    {
        await ChangeLanguageAsync(AppLanguage.English);
    }

    private void LanguageButton_Unchecked(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized || _isBusy)
        {
            return;
        }

        var uncheckedCurrentLanguage =
            ReferenceEquals(sender, ChineseLanguageButton) &&
            Localizer.Current == AppLanguage.Chinese ||
            ReferenceEquals(sender, EnglishLanguageButton) &&
            Localizer.Current == AppLanguage.English;
        if (uncheckedCurrentLanguage)
        {
            UpdateLanguageButtons();
        }
    }

    private async Task ChangeLanguageAsync(AppLanguage language)
    {
        if (!_isInitialized || _isBusy || Localizer.Current == language)
        {
            UpdateLanguageButtons();
            return;
        }

        await RunBusyAsync(async () =>
        {
            var previousLanguageCode = _settings.UiLanguage;
            _settings.UiLanguage = Localizer.ToCode(language);
            try
            {
                _settingsStore.Save(_settings);
            }
            catch
            {
                _settings.UiLanguage = previousLanguageCode;
                UpdateLanguageButtons();
                throw;
            }

            Localizer.Use(language);
            ApplyLanguage();
            RefreshLunaWorkerAgentStatus();
            UpdatePersistedProviderCapabilityStatuses();
            RefreshBackups();
            await RefreshStatusAsync();
            await RefreshCapabilitiesAsync();
            OperationStatusText.Text = T(
                "界面语言已切换为中文。",
                "Interface language switched to English.");
        });
    }

    private async void LightThemeButton_Checked(object sender, RoutedEventArgs e)
    {
        await ChangeThemeAsync(UiThemePreference.Light);
    }

    private async void DarkThemeButton_Checked(object sender, RoutedEventArgs e)
    {
        await ChangeThemeAsync(UiThemePreference.Dark);
    }

    private async void SystemThemeButton_Checked(object sender, RoutedEventArgs e)
    {
        await ChangeThemeAsync(UiThemePreference.System);
    }

    private async Task ChangeThemeAsync(UiThemePreference preference)
    {
        if (!_isInitialized ||
            _isBusy ||
            ThemePreference.Parse(_settings.UiTheme) == preference)
        {
            UpdateThemeButtons();
            return;
        }

        await RunBusyAsync(() =>
        {
            var previousThemeCode = _settings.UiTheme;
            _settings.UiTheme = ThemePreference.ToCode(preference);
            try
            {
                _settingsStore.Save(_settings);
                ThemeManager.Apply(_settings.UiTheme);
            }
            catch
            {
                _settings.UiTheme = previousThemeCode;
                ThemeManager.Apply(previousThemeCode);
                UpdateThemeButtons();
                throw;
            }

            UpdateThemeButtons();
            if (_lastConfigStatus is { } status)
            {
                UpdateModeBadge(status);
            }
            UpdatePersistedProviderCapabilityStatuses();
            UpdateHostStatusDots();
            OperationStatusText.Text = preference switch
            {
                UiThemePreference.Dark =>
                    T("已切换到深色外观。", "Dark appearance selected."),
                UiThemePreference.System =>
                    T("外观现在跟随 Windows。", "Appearance now follows Windows."),
                _ =>
                    T("已切换到浅色外观。", "Light appearance selected.")
            };
            return Task.CompletedTask;
        });
    }

    private void UpdateThemeButtons()
    {
        var preference = ThemePreference.Parse(_settings.UiTheme);
        LightThemeButton.IsChecked = preference == UiThemePreference.Light;
        DarkThemeButton.IsChecked = preference == UiThemePreference.Dark;
        SystemThemeButton.IsChecked = preference == UiThemePreference.System;
    }

    private async void SaveKeyButton_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            var profile = SaveNonSecretSettings(persist: false);
            var key = ApiKeyPasswordBox.Password.Trim();
            if (key.Length < 16)
            {
                throw new InvalidOperationException(T(
                    "请输入新生成的完整 API Key。",
                    "Enter the complete newly generated API key."));
            }

            CredentialVault.Write(
                CredentialTargetFactory.RequireValid(profile.CredentialTarget),
                key);
            _settingsStore.Save(_settings);
            ApiKeyPasswordBox.Clear();
            RefreshProviderProfilePicker();
            UpdateKeyStatusText();
            OperationStatusText.Text = T(
                "新密钥已保存到 Windows 凭据管理器。",
                "The new key was saved to Windows Credential Manager.");
            await RefreshStatusAsync();
        });
    }

    private async void DeleteKeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                this,
                T(
                    "确认从 Windows 凭据管理器删除第三方密钥？",
                    "Delete the third-party key from Windows Credential Manager?"),
                T("删除密钥", "Delete key"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            if (_isNewProviderProfileDraft || DraftProviderProfile is null)
            {
                throw new InvalidOperationException(T(
                    "新账号尚未保存密钥。",
                    "The new account has no saved key."));
            }

            CredentialVault.Delete(
                CredentialTargetFactory.RequireValid(
                    DraftProviderProfile.CredentialTarget));
            ApiKeyPasswordBox.Clear();
            RefreshProviderProfilePicker();
            UpdateKeyStatusText();
            OperationStatusText.Text = T(
                "第三方密钥已删除。",
                "The third-party key was deleted.");
            await RefreshStatusAsync();
        });
    }

    private async void TestConnectionButton_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            var profile = SaveNonSecretSettings(persist: false);
            var key = ResolveAndOptionallySaveKey();
            ConnectionTestResult result;
            try
            {
                result = IsKimiProfile(profile)
                    ? await TestKimiConnectionAsync(profile.Model, key)
                    : await TestConnectionAsync(profile.BaseUrl, profile.Model, key);
            }
            catch
            {
                ClearCurrentCompatibilityTestResult();
                _settingsStore.Save(_settings);
                throw;
            }
            OperationStatusText.Text = result.Summary;

            if (result.Success)
            {
                _settings.LastSuccessfulCompatibilityTestUtc = DateTimeOffset.UtcNow;
                _settings.LastTestedEndpointFingerprint =
                    CompatibilityFingerprint(profile);
            }
            else
            {
                ClearCurrentCompatibilityTestResult();
            }
            _settingsStore.Save(_settings);

            MessageBox.Show(
                this,
                result.Summary,
                result.Success
                    ? T("兼容性测试通过", "Compatibility test passed")
                    : T("兼容性测试未通过", "Compatibility test failed"),
                MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        });
    }

    private async Task<ConnectionTestResult> TestKimiConnectionAsync(
        string model,
        string apiKey)
    {
        OperationStatusText.Text = T(
            "正在确保 WSL 内的随想 K3 路由器可用…",
            "Ensuring the SuiXiang K3 router inside WSL is ready...");
        var router = await _wslKimiRouterService.EnsureRunningAsync(
            KimiWslLauncherPath,
            CancellationToken.None);
        if (!router.Success)
        {
            return new ConnectionTestResult(false, router.Summary);
        }

        OperationStatusText.Text = T(
            "正在测试随想 K3 上游 Chat Completions…",
            "Testing the SuiXiang K3 upstream Chat Completions...");
        var upstream = await _connectionTestService.TestChatCompletionsApiAsync(
            AppPaths.KimiUpstreamBaseUrl,
            model,
            apiKey,
            CancellationToken.None);
        return upstream.Success
            ? new ConnectionTestResult(
                true,
                T(
                    "连接成功：WSL 路由器健康检查、随想 K3 上游 Chat Completions 与认证均可用。",
                    "Connection succeeded: the WSL router health check, SuiXiang K3 upstream Chat Completions, and authentication are available."),
                upstream.StatusCode)
            : new ConnectionTestResult(
                false,
                DescribeKimiCompatibilityFailure(upstream),
                upstream.StatusCode);
    }

    private async void RefreshCapabilitiesButton_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(RefreshCapabilitiesAsync);
    }

    private async void TestToolCallingButton_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            var profile = SaveNonSecretSettings(persist: false);
            var key = ResolveAndOptionallySaveKey();
            ToolCapabilityStatusText.Text =
                T(
                    "第三方插件工具调用：正在测试 function_call 与工具结果回传…",
                    "Third-party plugin tools: testing function_call and tool-result replay...");
            ToolStatusDot.Background = ResourceBrush("WarningBrush");

            ConnectionTestResult result;
            try
            {
                result = await _connectionTestService.TestFunctionCallingAsync(
                    profile.BaseUrl,
                    profile.Model,
                    key);
            }
            catch
            {
                ClearCurrentToolTestResult();
                _settingsStore.Save(_settings);
                UpdatePersistedProviderCapabilityStatuses();
                throw;
            }
            ToolCapabilityStatusText.Text =
                F(
                    "第三方插件工具调用：{0}",
                    "Third-party plugin tools: {0}",
                    result.Summary);
            OperationStatusText.Text = result.Summary;
            if (result.Success)
            {
                _settings.LastSuccessfulToolTestUtc = DateTimeOffset.UtcNow;
                _settings.LastToolTestedEndpointFingerprint =
                    ConnectionTestService.EndpointFingerprint(
                        profile.BaseUrl,
                        profile.Model);
            }
            else
            {
                ClearCurrentToolTestResult();
            }
            _settingsStore.Save(_settings);
            UpdatePersistedProviderCapabilityStatuses();

            MessageBox.Show(
                this,
                result.Summary,
                result.Success
                    ? T("插件协议测试通过", "Plugin protocol test passed")
                    : T("插件协议测试未通过", "Plugin protocol test failed"),
                MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        });
    }

    private async void TestImageGenerationButton_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                this,
                T(
                    "这会向第三方服务发送一次真实的 /images/generations 请求，" +
                    "用于验证 Codex 当前图片后端，可能消耗额度。继续吗？",
                    "This sends a real /images/generations request to the third-party service " +
                    "to verify Codex's current image backend and may consume credits. Continue?"),
                T("生成测试图片", "Generate test image"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Information) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            var profile = SaveNonSecretSettings(persist: false);
            var key = ResolveAndOptionallySaveKey();
            ImageCapabilityStatusText.Text =
                T(
                    "第三方图片生成：正在实测 Codex 当前使用的 Images API…",
                    "Third-party image generation: testing the Images API currently used by Codex...");
            ImageStatusDot.Background = ResourceBrush("WarningBrush");

            ImageGenerationTestResult result;
            try
            {
                result = await _connectionTestService.TestImageGenerationAsync(
                    profile.BaseUrl,
                    AppPaths.DefaultThirdPartyImageModel,
                    key);
            }
            catch
            {
                ClearCurrentImageTestResult();
                _settingsStore.Save(_settings);
                UpdatePersistedProviderCapabilityStatuses();
                throw;
            }
            ImageCapabilityStatusText.Text =
                F(
                    "第三方图片生成：{0}",
                    "Third-party image generation: {0}",
                    result.Summary);
            OperationStatusText.Text = result.Summary;

            if (result.Success && !string.IsNullOrWhiteSpace(result.ArtifactPath))
            {
                _settings.LastSuccessfulImageTestUtc = DateTimeOffset.UtcNow;
                _settings.LastImageTestedEndpointFingerprint =
                    ConnectionTestService.EndpointFingerprint(
                        profile.BaseUrl,
                        AppPaths.DefaultThirdPartyImageModel);
                _settings.LastGeneratedImagePath = result.ArtifactPath;
            }
            else
            {
                ClearCurrentImageTestResult();
            }
            _settingsStore.Save(_settings);
            UpdatePersistedProviderCapabilityStatuses();

            MessageBox.Show(
                this,
                result.Summary,
                result.Success
                    ? T("图片生成测试通过", "Image generation test passed")
                    : T("图片生成测试未通过", "Image generation test failed"),
                MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        });
    }

    private void OpenGeneratedImageButton_Click(object sender, RoutedEventArgs e)
    {
        var path = _settings.LastGeneratedImagePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _settings.LastGeneratedImagePath = null;
            _settingsStore.Save(_settings);
            OpenGeneratedImageButton.IsEnabled = false;
            UpdatePersistedProviderCapabilityStatuses();
            MessageBox.Show(
                this,
                T(
                    "尚未找到已生成的测试图片，请先运行图片生成测试。",
                    "No generated test image was found. Run the image generation test first."),
                T("测试图片不存在", "Test image not found"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private void OpenRemoteSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!HostCapabilityDiagnosticsService.OpenOfficialCodexApp())
        {
            MessageBox.Show(
                this,
                T(
                    "无法打开官方 Codex Windows 应用，请先确认它已安装。",
                    "Could not open the official Codex Windows app. Confirm that it is installed."),
                T("无法打开 Codex", "Could not open Codex"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        OperationStatusText.Text =
            T(
                "已请求打开官方 Codex。首次手机配对需从侧栏的“Set up Remote”开始（若账号已开放该入口）。",
                "Requested official Codex to open. Initial phone pairing starts from “Set up Remote” in the sidebar when the account exposes it.");
    }

    private async Task<ProviderSwitchResult> EnsureAndSwitchToKimiAsync(
        string model,
        string apiKey,
        string credentialTarget)
    {
        ValidateKimiModel(model);
        if (!File.Exists(AppPaths.ConfigPath))
        {
            throw new FileNotFoundException(T(
                "未找到 Codex config.toml。请先启动一次官方 Codex 后再连接随想 K3。",
                "Codex config.toml was not found. Start official Codex once before connecting SuiXiang K3."),
                AppPaths.ConfigPath);
        }

        OperationStatusText.Text = T(
            "正在确保 WSL 内的随想 K3 路由器可用…",
            "Ensuring the SuiXiang K3 router inside WSL is ready...");
        var router = await _wslKimiRouterService.EnsureRunningAsync(
            KimiWslLauncherPath,
            CancellationToken.None);
        if (!router.Success)
        {
            throw new InvalidOperationException(router.Summary);
        }

        OperationStatusText.Text = T(
            "正在实时测试随想 K3 上游 Chat Completions…",
            "Running a live SuiXiang K3 upstream Chat Completions test...");
        var compatibility = await _connectionTestService.TestChatCompletionsApiAsync(
            AppPaths.KimiUpstreamBaseUrl,
            model,
            apiKey,
            CancellationToken.None);
        if (!compatibility.Success)
        {
            throw new InvalidOperationException(
                DescribeKimiCompatibilityFailure(compatibility));
        }

        _configurationRestartWarning = null;
        var codexStopped = false;
        try
        {
            OperationStatusText.Text = T(
                "正在停止 Codex，写入并验证新线路配置…",
                "Stopping Codex to write and verify the new route configuration...");
            await _processService.StopAsync();
            codexStopped = true;
            return _switchWorkflow.SwitchToKimi(
                new KimiSwitchRequest(
                    model,
                    TokenBrokerPath,
                    credentialTarget));
        }
        finally
        {
            if (codexStopped)
            {
                try
                {
                    OperationStatusText.Text = T(
                        "新线路配置已验证，正在等待 Codex 完成启动…",
                        "The new route configuration was verified; waiting for Codex to finish starting...");
                    await _processService.StartAsync();
                }
                catch (Exception exception)
                {
                    // Keep the config/profile coherent after a successful write;
                    // report startup failure without reverting only UI metadata.
                    _configurationRestartWarning = F(
                        "新线路配置已写入，但 Codex 未能在规定时间内启动：{0}。请手动启动 Codex。",
                        "The new route configuration was written, but Codex did not start in time: {0}. Start Codex manually.",
                        exception.Message);
                    OperationStatusText.Text = _configurationRestartWarning;
                }
            }
        }
    }

    private static string DescribeKimiCompatibilityFailure(
        ConnectionTestResult result) =>
        result.StatusCode switch
        {
            401 or 403 => T(
                "随想 K3 上游测试被拒绝（API Key 或权限无效）。未切换线路。",
                "The SuiXiang K3 upstream test was rejected (invalid API key or permission). The route was not switched."),
            404 => T(
                "随想 K3 上游返回 HTTP 404：模型可能不受支持。未切换线路。",
                "The SuiXiang K3 upstream returned HTTP 404: the model may be unsupported. The route was not switched."),
            502 or 503 => F(
                "随想 K3 上游暂时不可用（HTTP {0}）。未切换线路，请稍后重试。",
                "The SuiXiang K3 upstream is temporarily unavailable (HTTP {0}). The route was not switched; retry later.",
                result.StatusCode),
            _ => F(
                "随想 K3 上游 Chat Completions 测试未通过：{0}。未切换线路。",
                "The SuiXiang K3 upstream Chat Completions test failed: {0}. The route was not switched.",
                result.Summary)
        };

    private async void SwitchThirdPartyButton_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            var previousActiveProfileId = _settings.ActiveProviderProfileId;
            var previousSelectedProfileId = _selectedProviderProfileId;
            var previousNewProfileDraft = _isNewProviderProfileDraft;
            var previousRestartAfterSwitch = _settings.RestartAfterSwitch;
            var previousStoredOfficialModel = _settings.OfficialModel;
            var previousStoredOfficialReviewModel =
                _settings.OfficialReviewModel;
            var previousCompatibilityTestUtc =
                _settings.LastSuccessfulCompatibilityTestUtc;
            var previousCompatibilityFingerprint =
                _settings.LastTestedEndpointFingerprint;
            var selectedProfileBeforeSave = DraftProviderProfile;
            var previousDisplayedProfileSnapshot = selectedProfileBeforeSave is null
                ? null
                : CloneProviderProfile(selectedProfileBeforeSave);
            var previousDisplayedKey = selectedProfileBeforeSave is null ||
                                  !CredentialTargetFactory.IsValid(
                                      selectedProfileBeforeSave.CredentialTarget)
                ? null
                : CredentialVault.Read(selectedProfileBeforeSave.CredentialTarget);
            ProviderProfile? attemptedProfile = null;
            string? selectedCredentialTarget = null;
            var credentialMayHaveChanged = false;

            void RestoreAttemptedProviderState()
            {
                if (attemptedProfile is not null)
                {
                    RestoreProviderProfile(
                        attemptedProfile,
                        previousDisplayedProfileSnapshot ??
                        CloneProviderProfile(attemptedProfile),
                        previousDisplayedProfileSnapshot is null,
                        previousActiveProfileId,
                        save: false);
                }
                else
                {
                    _settings.ActiveProviderProfileId = previousActiveProfileId;
                }

                _settings.RestartAfterSwitch = previousRestartAfterSwitch;
                _settings.OfficialModel = previousStoredOfficialModel;
                _settings.OfficialReviewModel =
                    previousStoredOfficialReviewModel;
                _settings.LastSuccessfulCompatibilityTestUtc =
                    previousCompatibilityTestUtc;
                _settings.LastTestedEndpointFingerprint =
                    previousCompatibilityFingerprint;

                if (credentialMayHaveChanged &&
                    CredentialTargetFactory.IsValid(selectedCredentialTarget))
                {
                    RestoreCredential(
                        selectedCredentialTarget!,
                        previousDisplayedKey);
                }

                _settingsStore.Save(_settings);
                _selectedProviderProfileId = previousSelectedProfileId;
                _isNewProviderProfileDraft = previousNewProfileDraft;
                var restoredProfile = DraftProviderProfile ?? ActiveProviderProfile;
                BaseUrlTextBox.Text = restoredProfile.BaseUrl;
                ModelComboBox.Text = restoredProfile.Model;
                ApiKeyPasswordBox.Clear();
                RefreshProviderProfilePicker();
                UpdateKeyStatusText();
                UpdatePersistedProviderCapabilityStatuses();
            }

            string key;
            try
            {
                attemptedProfile = SaveNonSecretSettings(persist: false);
                selectedCredentialTarget = CredentialTargetFactory.RequireValid(
                    attemptedProfile.CredentialTarget);
                credentialMayHaveChanged = true;
                key = ResolveAndOptionallySaveKey();
            }
            catch
            {
                RestoreAttemptedProviderState();
                throw;
            }
            if (IsKimiProfile(attemptedProfile))
            {
                var currentKimi = _configService.ReadStatus();
                var previousOfficialModel = _settings.OfficialModel;
                var previousOfficialReviewModel = _settings.OfficialReviewModel;
                if (currentKimi.Mode == ProviderMode.Official)
                {
                    if (!string.IsNullOrWhiteSpace(currentKimi.Model))
                    {
                        _settings.OfficialModel = currentKimi.Model;
                    }

                    _settings.OfficialReviewModel = currentKimi.ReviewModel;
                }

                ProviderSwitchResult kimiSwitch;
                try
                {
                    kimiSwitch = await EnsureAndSwitchToKimiAsync(
                        attemptedProfile.Model,
                        key,
                        selectedCredentialTarget!);
                }
                catch
                {
                    _settings.OfficialModel = previousOfficialModel;
                    _settings.OfficialReviewModel = previousOfficialReviewModel;
                    RestoreAttemptedProviderState();
                    throw;
                }
                _settings.RestartAfterSwitch = true;
                _settings.ActiveProviderProfileId = attemptedProfile.Id;
                _selectedProviderProfileId = attemptedProfile.Id;
                _isNewProviderProfileDraft = false;
                _settings.LastTestedEndpointFingerprint =
                    ConnectionTestService.EndpointFingerprint(
                        AppPaths.KimiRouterBaseUrl,
                        attemptedProfile.Model);
                _settings.LastSuccessfulCompatibilityTestUtc = DateTimeOffset.UtcNow;
                _settingsStore.Save(_settings);
                RefreshLunaWorkerAgentStatus(kimiSwitch.VerifiedStatus);
                var kimiConfigurationRestartWarning = _configurationRestartWarning;
                OperationStatusText.Text = string.IsNullOrWhiteSpace(
                    kimiConfigurationRestartWarning)
                    ? F(
                        "已切换到随想 K3（实验）；已完成配置写入并重新启动 Codex，历史分区保持 {0}。备份：{1}",
                        "Switched to SuiXiang K3 (experimental); the configuration was written and Codex was restarted. The history partition remains {0}. Backup: {1}",
                        AppPaths.StableProviderId,
                        kimiSwitch.BackupFolder)
                    : F(
                        "随想 K3 配置已写入并验证，但 Codex 启动未确认；历史分区仍为 {0}。备份：{1}。{2}",
                        "The SuiXiang K3 configuration was written and verified, but Codex startup was not confirmed; the history partition remains {0}. Backup: {1}. {2}",
                        AppPaths.StableProviderId,
                        kimiSwitch.BackupFolder,
                        kimiConfigurationRestartWarning);
                await RefreshStatusAsync();
                RefreshBackups();
                // Every route is switched as the same config transaction.
                return;
            }
            var suiXiangRoute = IsSuiXiangProfile(attemptedProfile);

            var fingerprint = ConnectionTestService.EndpointFingerprint(
                attemptedProfile.BaseUrl,
                attemptedProfile.Model);
            var recentlyTested =
                !suiXiangRoute &&
                _settings.LastTestedEndpointFingerprint == fingerprint &&
                _settings.LastSuccessfulCompatibilityTestUtc is not null &&
                DateTimeOffset.UtcNow - _settings.LastSuccessfulCompatibilityTestUtc <
                TimeSpan.FromDays(7);

            if (!recentlyTested)
            {
                OperationStatusText.Text = T(
                    "正在验证第三方 Responses API…",
                    "Validating the third-party Responses API...");
                ConnectionTestResult result;
                try
                {
                    result = await TestConnectionAsync(
                        attemptedProfile.BaseUrl,
                        attemptedProfile.Model,
                        key);
                }
                catch
                {
                    RestoreAttemptedProviderState();
                    throw;
                }
                if (!result.Success)
                {
                    if (suiXiangRoute)
                    {
                        var suiXiangFailure =
                            DescribeSuiXiangCompatibilityFailure(result);
                        RestoreAttemptedProviderState();
                        OperationStatusText.Text = suiXiangFailure;
                        MessageBox.Show(
                            this,
                            suiXiangFailure,
                            T(
                                "随想兼容性未确认",
                                "SuiXiang compatibility not confirmed"),
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        return;
                    }

                    var answer = MessageBox.Show(
                        this,
                        F(
                            "{0}\n\n仍然写入第三方配置吗？",
                            "{0}\n\nWrite the third-party configuration anyway?",
                            result.Summary),
                        T(
                            "第三方兼容性未确认",
                            "Third-party compatibility not confirmed"),
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                    if (answer != MessageBoxResult.Yes)
                    {
                        RestoreAttemptedProviderState();
                        OperationStatusText.Text = T(
                            "已取消切换；当前 Codex 配置未改动。",
                            "Switch cancelled; the current Codex configuration was not changed.");
                        return;
                    }

                    ClearCurrentCompatibilityTestResult();
                    _settingsStore.Save(_settings);
                }
                else
                {
                    _settings.LastSuccessfulCompatibilityTestUtc = DateTimeOffset.UtcNow;
                    _settings.LastTestedEndpointFingerprint = fingerprint;
                    _settingsStore.Save(_settings);
                }
            }

            var current = _configService.ReadStatus();
            if (current.Mode == ProviderMode.Official)
            {
                if (!string.IsNullOrWhiteSpace(current.Model))
                {
                    _settings.OfficialModel = current.Model;
                }

                _settings.OfficialReviewModel = current.ReviewModel;
                _settingsStore.Save(_settings);
            }

            ProviderSwitchResult switchResult;
            try
            {
                switchResult = await SwitchToThirdPartyFromCurrentConfigAsync(
                    new ThirdPartySwitchRequest(
                        attemptedProfile.Model,
                        attemptedProfile.BaseUrl,
                        TokenBrokerPath,
                        selectedCredentialTarget!));
            }
            catch
            {
                RestoreAttemptedProviderState();
                throw;
            }
            RefreshLunaWorkerAgentStatus(switchResult.VerifiedStatus);

            // The draft only becomes the active account after config.toml was
            // written and verified.  A failed request above leaves the
            // previous active profile untouched.
            _settings.ActiveProviderProfileId = attemptedProfile.Id;
            _selectedProviderProfileId = attemptedProfile.Id;
            _isNewProviderProfileDraft = false;
            _settingsStore.Save(_settings);
            RefreshProviderProfilePicker();
            UpdateKeyStatusText();

            var configurationRestartWarning = _configurationRestartWarning;
            OperationStatusText.Text = string.IsNullOrWhiteSpace(
                configurationRestartWarning)
                ? F(
                    "已切换到第三方；已完成配置写入并重新启动 Codex，历史分区保持 {0}。备份：{1}",
                    "Switched to the third-party route; the configuration was written and Codex was restarted. The history partition remains {0}. Backup: {1}",
                    AppPaths.StableProviderId,
                    switchResult.BackupFolder)
                : F(
                    "第三方配置已写入并验证，但 Codex 启动未确认；历史分区仍为 {0}。备份：{1}。{2}",
                    "The third-party configuration was written and verified, but Codex startup was not confirmed; the history partition remains {0}. Backup: {1}. {2}",
                    AppPaths.StableProviderId,
                    switchResult.BackupFolder,
                    configurationRestartWarning);
            await RefreshStatusAsync();
            RefreshBackups();
        });
    }

    private static string DescribeSuiXiangCompatibilityFailure(
        ConnectionTestResult result)
    {
        return result.StatusCode switch
        {
            404 => T(
                "随想返回 HTTP 404：当前模型或模型组可能不受支持。未写入配置；请刷新模型列表或改用随想当前提供的模型后重试。",
                "SuiXiang returned HTTP 404: the model or model group may be unsupported. Nothing was written; refresh the model list or choose a model currently offered by SuiXiang, then retry."),
            503 => T(
                "随想返回 HTTP 503：当前模型或模型组可能暂不可用。未写入配置；请稍后重试或刷新模型列表。",
                "SuiXiang returned HTTP 503: the model or model group may be temporarily unavailable. Nothing was written; retry later or refresh the model list."),
            502 => T(
                "随想返回 HTTP 502：上游或网络暂时不可用。未写入配置；请稍后重试。",
                "SuiXiang returned HTTP 502: its upstream or network is temporarily unavailable. Nothing was written; retry later."),
            _ => F(
                "随想 Responses 兼容性测试未通过：{0}。未写入配置。",
                "SuiXiang Responses compatibility test failed: {0}. Nothing was written.",
                result.Summary)
        };
    }

    private Task<ProviderSwitchResult> SwitchToThirdPartyFromCurrentConfigAsync(
        ThirdPartySwitchRequest request) =>
        SwitchConfigWhileCodexStoppedAsync(
            () => _switchWorkflow.SwitchToThirdParty(request));

    private Task<ProviderSwitchResult> SwitchToOfficialFromCurrentConfigAsync(
        OfficialSwitchRequest request) =>
        SwitchConfigWhileCodexStoppedAsync(
            () => _switchWorkflow.SwitchToOfficial(request));

    private async Task<ProviderSwitchResult> SwitchConfigWhileCodexStoppedAsync(
        Func<ProviderSwitchResult> writeConfig)
    {
        _configurationRestartWarning = null;
        var codexStopped = false;
        try
        {
            OperationStatusText.Text = T(
                "正在停止 Codex，写入并验证新线路配置…",
                "Stopping Codex to write and verify the new route configuration...");
            await _processService.StopAsync();
            codexStopped = true;
            return writeConfig();
        }
        finally
        {
            if (codexStopped)
            {
                try
                {
                    OperationStatusText.Text = T(
                        "新线路配置已验证，正在等待 Codex 完成启动…",
                        "The new route configuration was verified; waiting for Codex to finish starting...");
                    await _processService.StartAsync();
                }
                catch (Exception exception)
                {
                    _configurationRestartWarning = F(
                        "新线路配置已写入，但 Codex 未能在规定时间内启动：{0}。请手动启动 Codex。",
                        "The new route configuration was written, but Codex did not start in time: {0}. Start Codex manually.",
                        exception.Message);
                    OperationStatusText.Text = _configurationRestartWarning;
                }
            }
        }
    }

    private async void SwitchOfficialButton_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            _settings.RestartAfterSwitch = true;
            _settingsStore.Save(_settings);
            var switchResult = await SwitchToOfficialFromCurrentConfigAsync(
                new OfficialSwitchRequest(
                    _settings.OfficialModel,
                    _settings.OfficialReviewModel));
            RefreshLunaWorkerAgentStatus(switchResult.VerifiedStatus);

            var configurationRestartWarning = _configurationRestartWarning;
            OperationStatusText.Text = string.IsNullOrWhiteSpace(
                configurationRestartWarning)
                ? F(
                    "已切换到官方 OpenAI；已完成配置写入并重新启动 Codex。官方登录凭据保持不变。备份：{0}",
                    "Switched to official OpenAI; the configuration was written and Codex was restarted. Official sign-in credentials were preserved. Backup: {0}",
                    switchResult.BackupFolder)
                : F(
                    "官方 OpenAI 配置已写入并验证，但 Codex 启动未确认；官方登录凭据保持不变。备份：{0}。{1}",
                    "The official OpenAI configuration was written and verified, but Codex startup was not confirmed; official sign-in credentials were preserved. Backup: {0}. {1}",
                    switchResult.BackupFolder,
                    configurationRestartWarning);
            await RefreshStatusAsync();
            RefreshBackups();
        });
    }

    private void OpenBackupsButton_Click(object sender, RoutedEventArgs e)
    {
        BackupsNavigationButton.IsChecked = true;
    }

    private void RefreshBackupsButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshBackups();
        OperationStatusText.Text = T("备份列表已刷新。", "Backup list refreshed.");
    }

    private void OpenBackupFolderButton_Click(object sender, RoutedEventArgs e)
    {
        OpenFolder(AppPaths.BackupsRoot);
    }

    private void OpenSelectedBackupButton_Click(object sender, RoutedEventArgs e)
    {
        OpenSelectedBackup();
    }

    private void BackupsDataGrid_MouseDoubleClick(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(BackupsDataGrid, source) is DataGridRow)
        {
            OpenSelectedBackup();
        }
    }

    private void BackupsDataGrid_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        OpenSelectedBackupButton.IsEnabled =
            !_isBusy && BackupsDataGrid.SelectedItem is BackupRow;
    }

    private void OpenSelectedBackup()
    {
        if (BackupsDataGrid.SelectedItem is not BackupRow backup)
        {
            return;
        }

        var folder = Path.GetDirectoryName(backup.ConfigPath);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            OpenFolder(folder);
        }
    }

    private void RefreshBackups()
    {
        Directory.CreateDirectory(AppPaths.BackupsRoot);
        var rows = _backupCatalogService
            .List()
            .Select(entry => new BackupRow(
                entry.Timestamp,
                entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                entry.SizeBytes,
                FormatFileSize(entry.SizeBytes),
                entry.FolderName,
                entry.ConfigPath))
            .ToList();

        BackupsDataGrid.ItemsSource = rows;
        BackupsEmptyPanel.Visibility =
            rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BackupCountText.Text = F(
            "{0} 个备份",
            "{0} backups",
            rows.Count);
        OpenSelectedBackupButton.IsEnabled = false;
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024d:0.0} KB";
        }

        return $"{bytes / (1024d * 1024d):0.0} MB";
    }

    private void OpenDataFolderButton_Click(object sender, RoutedEventArgs e)
    {
        OpenFolder(AppPaths.LocalDataRoot);
    }

    private void OpenGitHubButton_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://github.com/tuolaji996/codex-provider-switcher",
            UseShellExecute = true
        });
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private ProviderProfile SaveNonSecretSettings(bool persist = true)
    {
        var normalizedBaseUrl = ConfigService.NormalizeBaseUrl(BaseUrlTextBox.Text);
        var model = ModelComboBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException(T(
                "第三方模型不能为空。",
                "The third-party model cannot be empty."));
        }

        ProviderAvailabilityPolicy.RequireAvailableThirdPartyRoute(
            normalizedBaseUrl,
            model);

        var kimiBridge = SettingsStore.IsKimiBaseUrl(normalizedBaseUrl) &&
                          string.Equals(
                              model,
                              AppPaths.DefaultKimiModel,
                              StringComparison.Ordinal);
        if (kimiBridge)
        {
            ValidateKimiModel(model);
        }

        var profile = SelectOrCreateProfileForProviderFields(normalizedBaseUrl, model);
        profile.Kind = kimiBridge
            ? ProviderKinds.Kimi
            : IsSuiXiangBaseUrl(normalizedBaseUrl)
                ? ProviderKinds.SuiXiang
                : ProviderKinds.Custom;
        profile.BaseUrl = normalizedBaseUrl;
        profile.Model = model;
        if (profile.Kind == ProviderKinds.Custom &&
            string.IsNullOrWhiteSpace(profile.DisplayName) &&
            Uri.TryCreate(normalizedBaseUrl, UriKind.Absolute, out var uri))
        {
            profile.DisplayName = uri.Host;
        }

        _settings.RestartAfterSwitch = true;
        if (persist)
        {
            _settingsStore.Save(_settings);
        }

        _selectedProviderProfileId = profile.Id;
        _isNewProviderProfileDraft = false;
        UpdatePersistedProviderCapabilityStatuses();
        return profile;
    }

    private ProviderProfile SelectOrCreateProfileForProviderFields(
        string normalizedBaseUrl,
        string model)
    {
        // A draft selection owns its own credential slot. Never mutate that
        // saved account into another model/route when the user edits the
        // fields; that used to make the K3 account disappear when switching
        // back to SuiXiang OpenAI. A changed endpoint or model starts a new
        // profile and therefore requires an explicitly entered key.
        var profile = _isNewProviderProfileDraft
            ? null
            : DraftProviderProfile;
        var exactMatches = new List<ProviderProfile>();
        if (profile is not null &&
            (!ProfileMatchesBaseUrl(profile, normalizedBaseUrl) ||
             !string.Equals(profile.Model.Trim(), model.Trim(), StringComparison.Ordinal)))
        {
            var editedProfileId = profile.Id;
            // If the user edited the fields instead of picking the saved
            // account explicitly, reuse an exact saved endpoint/model match
            // only when it is unambiguous. Multiple keys may intentionally
            // share the same endpoint/model, so selecting the first one would
            // silently send the wrong credential.
            exactMatches = _settings.ProviderProfiles.Where(candidate =>
                !string.Equals(candidate.Id, editedProfileId, StringComparison.Ordinal) &&
                ProfileMatchesBaseUrl(candidate, normalizedBaseUrl) &&
                string.Equals(candidate.Model.Trim(), model.Trim(), StringComparison.Ordinal))
                .ToList();
            profile = exactMatches.Count == 1 ? exactMatches[0] : null;
        }
        if (profile is null)
        {
            var enteredKey = ApiKeyPasswordBox.Password.Trim();
            if (string.IsNullOrWhiteSpace(enteredKey))
            {
                throw new InvalidOperationException(T(
                    exactMatches.Count > 1
                        ? "此 Base URL 和模型有多个已保存账号。请先从“已保存账号”中明确选择，或点击“添加账号”并粘贴新 API Key。"
                        : "更换已保存账号的 Base URL 或模型时，必须粘贴新的 API Key。已保存的密钥绝不会发送到不同线路。",
                    exactMatches.Count > 1
                        ? "Multiple saved accounts use this Base URL and model. Select one explicitly under Saved accounts, or choose Add account and paste a new API key."
                        : "Changing a saved account's Base URL or model requires a new API key. A saved key is never sent to a different route."));
            }

            if (enteredKey.Length < 16)
            {
                throw new InvalidOperationException(T(
                    "请输入新生成的完整 API Key。",
                    "Enter the complete newly generated API key."));
            }

            var profileId = Guid.NewGuid().ToString("N");
            profile = new ProviderProfile
            {
                Id = profileId,
                BaseUrl = normalizedBaseUrl,
                Model = model,
                CredentialTarget = CredentialTargetFactory.CreateForProfileId(profileId)
            };
            _settings.ProviderProfiles.Add(profile);
        }
        else
        {
            if (!Guid.TryParse(profile.Id, out _))
            {
                profile.Id = Guid.NewGuid().ToString("N");
            }

            if (!CredentialTargetFactory.IsValid(profile.CredentialTarget))
            {
                profile.CredentialTarget =
                    CredentialTargetFactory.CreateForProfileId(profile.Id);
            }
        }

        _selectedProviderProfileId = profile.Id;
        _isNewProviderProfileDraft = false;
        return profile;
    }

    private void UpdateKeyStatusText()
    {
        if (_isNewProviderProfileDraft)
        {
            KeyStatusText.Text = T(
                "密钥状态：新账号尚未保存",
                "Key status: new account not saved yet");
            return;
        }

        var profile = DraftProviderProfile ?? ActiveProviderProfile;
        KeyStatusText.Text = CredentialTargetFactory.IsValid(profile.CredentialTarget) &&
                             CredentialVault.Exists(profile.CredentialTarget)
            ? T("密钥状态：已安全保存", "Key status: securely saved")
            : T(
                "密钥状态：尚未保存（请使用撤销旧密钥后生成的新密钥）",
                "Key status: not saved (revoke the exposed key and use a newly generated key)");
    }

    private static bool ProfileMatchesBaseUrl(
        ProviderProfile profile,
        string normalizedBaseUrl)
    {
        try
        {
            return string.Equals(
                ConfigService.NormalizeBaseUrl(profile.BaseUrl),
                normalizedBaseUrl,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private void RefreshProviderProfilePicker()
    {
        if (ProviderProfileComboBox is null)
        {
            return;
        }

        var profiles = _settings.ProviderProfiles
            .Where(profile =>
                !ProviderAvailabilityPolicy.IsRetiredKimiProfile(profile))
            .ToList();
        _isRefreshingProviderProfiles = true;
        try
        {
            ProviderProfileComboBox.Items.Clear();
            ComboBoxItem? selected = null;
            foreach (var profile in profiles)
            {
                var item = new ComboBoxItem
                {
                    Content = CreateProviderProfilePickerContent(profile),
                    Tag = profile.Id
                };
                ProviderProfileComboBox.Items.Add(item);
                if (!_isNewProviderProfileDraft &&
                    string.Equals(
                        profile.Id,
                        _selectedProviderProfileId ?? _settings.ActiveProviderProfileId,
                        StringComparison.Ordinal))
                {
                    selected = item;
                }
            }

            ProviderProfileComboBox.SelectedItem = selected;
        }
        finally
        {
            _isRefreshingProviderProfiles = false;
        }
    }

    private StackPanel CreateProviderProfilePickerContent(ProviderProfile profile)
    {
        var routeName = ResolveProfileRouteName(profile);
        var sameRouteProfiles = _settings.ProviderProfiles
            .Where(candidate =>
                !ProviderAvailabilityPolicy.IsRetiredKimiProfile(candidate) &&
                ProfileMatchesBaseUrl(candidate, NormalizeProfileBaseUrl(profile)) &&
                string.Equals(
                    candidate.Model.Trim(),
                    profile.Model.Trim(),
                    StringComparison.Ordinal))
            .ToList();
        if (sameRouteProfiles.Count > 1)
        {
            var ordinal = sameRouteProfiles.FindIndex(candidate =>
                string.Equals(candidate.Id, profile.Id, StringComparison.Ordinal)) + 1;
            routeName = $"{routeName} · {T("账号", "Account")} {ordinal}";
        }
        var isActive = _lastConfigStatus is { Mode: ProviderMode.ThirdParty } status &&
                       CredentialTargetFactory.IsValid(status.CredentialTarget) &&
                       string.Equals(
                           profile.CredentialTarget,
                           status.CredentialTarget,
                           StringComparison.Ordinal);
        var model = string.IsNullOrWhiteSpace(profile.Model)
            ? T("未设置模型", "No model")
            : profile.Model.Trim();
        var keyState = CredentialTargetFactory.IsValid(profile.CredentialTarget) &&
                       CredentialVault.Exists(profile.CredentialTarget)
            ? T("密钥已保存", "key saved")
            : T("需要密钥", "key needed");
        var endpoint = Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var uri)
            ? uri.Host
            : profile.BaseUrl;

        var panel = new StackPanel { Margin = new Thickness(0, 1, 0, 1) };
        panel.Children.Add(new TextBlock
        {
            Text = isActive
                ? $"{routeName} · {T("当前线路", "Current route")}"
                : routeName,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{model} · {keyState} · {endpoint}",
            FontSize = 11,
            Foreground = ResourceBrush("SubtleTextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        return panel;
    }

    private string ResolveProfileRouteName(ProviderProfile profile)
    {
        if (profile.Kind == ProviderKinds.Kimi)
        {
            return T("随想 K3（实验）", "SuiXiang K3 (experimental)");
        }

        if (profile.Kind == ProviderKinds.SuiXiang)
        {
            return T("随想 OpenAI", "SuiXiang OpenAI");
        }

        if (!string.IsNullOrWhiteSpace(profile.DisplayName))
        {
            return profile.DisplayName;
        }

        return Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var uri)
            ? uri.Host
            : profile.BaseUrl;
    }

    private static string NormalizeProfileBaseUrl(ProviderProfile profile)
    {
        try
        {
            return ConfigService.NormalizeBaseUrl(profile.BaseUrl);
        }
        catch (ArgumentException)
        {
            return profile.BaseUrl.Trim();
        }
    }

    private void ProviderProfileComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!_isInitialized ||
            _isBusy ||
            _isRefreshingProviderProfiles ||
            ProviderProfileComboBox.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string profileId)
        {
            return;
        }

        var profile = _settings.ProviderProfiles.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, profileId, StringComparison.Ordinal));
        if (profile is null)
        {
            return;
        }

        try
        {
            _selectedProviderProfileId = profile.Id;
            _isNewProviderProfileDraft = false;
            BaseUrlTextBox.Text = profile.BaseUrl;
            ModelComboBox.Text = profile.Model;
            ApiKeyPasswordBox.Clear();
            UpdateKeyStatusText();
            UpdatePersistedProviderCapabilityStatuses();
            OperationStatusText.Text = F(
                "已载入 {0}。这只是待切换草稿；点击“切换到第三方”成功后才会成为当前线路。",
                "Loaded {0}. This is only a switch draft; it becomes the active route after Switch to third-party succeeds.",
                ResolveProfileRouteName(profile));
        }
        catch (Exception exception)
        {
            _selectedProviderProfileId = _settings.ActiveProviderProfileId;
            _isNewProviderProfileDraft = false;
            RefreshProviderProfilePicker();
            ShowFailure(T("选择线路失败", "Could not select route"), exception);
        }
    }

    private async void NewProviderProfileButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        await RunBusyAsync(() =>
        {
            var baseUrl = ConfigService.NormalizeBaseUrl(BaseUrlTextBox.Text);
            var model = ModelComboBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(model) ||
                ProviderAvailabilityPolicy.IsRetiredKimiRoute(baseUrl, model))
            {
                model = AppPaths.DefaultThirdPartyModel;
            }

            // Do not create an empty profile.  The new account is only added
            // after a valid key is saved or a successful switch writes it.
            // A retired K3 selection becomes a fresh direct-provider draft.
            // The legacy K3 profile and credential remain untouched.
            _selectedProviderProfileId = null;
            _isNewProviderProfileDraft = true;
            BaseUrlTextBox.Text = baseUrl;
            ModelComboBox.Text = model;
            ApiKeyPasswordBox.Clear();
            RefreshProviderProfilePicker();
            UpdateKeyStatusText();
            UpdatePersistedProviderCapabilityStatuses();
            OperationStatusText.Text = T(
                "请输入新账号的 API Key；保存密钥或成功切换后，才会建立独立账号。",
                "Enter the new account API key. A separate account is created only after you save the key or switch successfully.");
            return Task.CompletedTask;
        });
    }

    private void ProviderSettingsTextBox_TextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        if (_isInitialized)
        {
            UpdatePersistedProviderCapabilityStatuses();
            UpdateKimiUi();
        }
    }

    private void ProviderModelComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_isInitialized)
        {
            var selected = ModelComboBox.SelectedItem?.ToString();
            if (!string.IsNullOrWhiteSpace(selected))
            {
                ModelComboBox.Text = selected;
            }
            UpdatePersistedProviderCapabilityStatuses();
            UpdateKimiUi();
        }
    }

    private void ProviderModelComboBox_LostFocus(
        object sender,
        RoutedEventArgs e)
    {
        if (_isInitialized)
        {
            UpdatePersistedProviderCapabilityStatuses();
            UpdateKimiUi();
        }
    }

    private async void RefreshModelsButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            var normalizedBaseUrl = ConfigService.NormalizeBaseUrl(BaseUrlTextBox.Text);
            var currentModel = ModelComboBox.Text.Trim();
            var key = ResolveKeyForModelDiscovery(normalizedBaseUrl);
            ModelDiscoveryStatusText.Text = T(
                "正在读取服务提供的模型列表…",
                "Reading the model list from the service...");
            OperationStatusText.Text = ModelDiscoveryStatusText.Text;

            var result = await _modelDiscoveryService.DiscoverAsync(
                normalizedBaseUrl,
                key,
                CancellationToken.None);
            if (!result.Success)
            {
                ModelDiscoveryStatusText.Text = result.Summary;
                OperationStatusText.Text = result.Summary;
                return;
            }

            var models = result.Models
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Select(model => model.Trim())
                .Where(model =>
                    !ProviderAvailabilityPolicy.IsRetiredKimiRoute(
                        normalizedBaseUrl,
                        model))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var suiXiangDiscovery = SettingsStore.IsKimiBaseUrl(normalizedBaseUrl);
            ModelComboBox.Items.Clear();
            foreach (var model in models)
            {
                ModelComboBox.Items.Add(model);
            }

            // Keep the user's current text even when the provider did not
            // advertise it. Compatibility is still confirmed by a live test.
            ModelComboBox.Text = currentModel;
            var currentIsListed = currentModel.Length > 0 &&
                                   models.Any(model => string.Equals(
                                       model,
                                       currentModel,
                                       StringComparison.OrdinalIgnoreCase));
            ModelDiscoveryStatusText.Text = suiXiangDiscovery
                ? F(
                    "已发现 {0} 个可用的随想 OpenAI 模型。",
                    "Discovered {0} available SuiXiang OpenAI models.",
                    models.Count)
                : currentIsListed || currentModel.Length == 0
                ? F(
                    "已发现 {0} 个模型；仍可手动输入自定义模型 ID。",
                    "Discovered {0} models; you can still enter a custom model ID.",
                    models.Count)
                : F(
                    "已发现 {0} 个模型；已保留当前模型“{1}”，它仍需通过实时兼容性测试。",
                    "Discovered {0} models; kept the current model \"{1}\". It still needs a live compatibility test.",
                    models.Count,
                    currentModel);
            OperationStatusText.Text = ModelDiscoveryStatusText.Text;
        });
    }

    private string ResolveKeyForModelDiscovery(string normalizedBaseUrl)
    {
        var model = ModelComboBox.Text.Trim();
        var kimiRoute = SettingsStore.IsKimiBaseUrl(normalizedBaseUrl) &&
                        string.Equals(
                            model,
                            AppPaths.DefaultKimiModel,
                            StringComparison.Ordinal);
        var selected = DraftProviderProfile;
        var selectedMatchesRoute = !kimiRoute &&
                                   selected is not null &&
                                   ProviderProfileRouteMatcher.FindExact(
                                       [selected],
                                       normalizedBaseUrl,
                                       model,
                                       selected.Kind).Count == 1;
        var requiredKind = kimiRoute
            ? ProviderKinds.Kimi
            : selectedMatchesRoute
                ? selected!.Kind
                : null;
        var routeMatches = ProviderProfileRouteMatcher.FindExact(
            _settings.ProviderProfiles,
            normalizedBaseUrl,
            model,
            requiredKind);

        var entered = ApiKeyPasswordBox.Password.Trim();
        if (!string.IsNullOrWhiteSpace(entered))
        {
            if (entered.Length < 16)
            {
                throw new InvalidOperationException(T(
                    "请输入新生成的完整 API Key。",
                    "Enter the complete newly generated API key."));
            }

            return entered;
        }

        if (routeMatches.Count != 1)
        {
            throw new InvalidOperationException(T(
                routeMatches.Count > 1
                    ? "这个线路有多个已保存账号，请先选择账号或粘贴新 API Key。"
                    : "这个线路尚无已保存的密钥。请先输入属于该服务的新 API Key。",
                routeMatches.Count > 1
                    ? "Multiple saved accounts match this route. Select an account or paste a new API key."
                    : "This route has no saved key. Enter a new API key for this service."));
        }

        var profile = routeMatches[0];
        var target = CredentialTargetFactory.RequireValid(profile.CredentialTarget);
        return CredentialVault.Read(target)
            ?? throw new InvalidOperationException(T(
                "当前线路尚未保存 API Key。请先输入属于该服务的新密钥。",
                "No API key is saved for the current route. Enter a new key for this service."));
    }

    private string ResolveAndOptionallySaveKey()
    {
        var entered = ApiKeyPasswordBox.Password.Trim();
        if (!string.IsNullOrWhiteSpace(entered))
        {
            if (entered.Length < 16)
            {
                throw new InvalidOperationException(T(
                    "请输入新生成的完整 API Key。",
                    "Enter the complete newly generated API key."));
            }

            if (_isNewProviderProfileDraft || DraftProviderProfile is null)
            {
                throw new InvalidOperationException(T(
                    "请先保存账号设置，再保存密钥。",
                    "Save the account settings before saving its key."));
            }

            CredentialVault.Write(
                CredentialTargetFactory.RequireValid(
                    DraftProviderProfile.CredentialTarget),
                entered);
            ApiKeyPasswordBox.Clear();
            return entered;
        }

        if (_isNewProviderProfileDraft || DraftProviderProfile is null)
        {
            throw new InvalidOperationException(T(
                "新账号尚无已保存密钥。请粘贴 API Key。",
                "The new account has no saved key. Paste its API key."));
        }

        return CredentialVault.Read(
                   CredentialTargetFactory.RequireValid(
                       DraftProviderProfile.CredentialTarget))
            ?? throw new InvalidOperationException(
                T(
                    "尚未保存第三方密钥。请先撤销已暴露的旧密钥，再粘贴新密钥。",
                    "No third-party key is saved. Revoke the exposed old key, then paste a newly generated key."));
    }

    private Task<ConnectionTestResult> TestConnectionAsync(
        string baseUrl,
        string model,
        string key) =>
        _connectionTestService.TestResponsesApiAsync(
            baseUrl,
            model,
            key);

    private static void ValidateKimiModel(string model)
    {
        ProviderAvailabilityPolicy.RequireKimiRouteEnabled();
        if (!string.Equals(
                model.Trim(),
                AppPaths.DefaultKimiModel,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(T(
                "随想 K3 实验线路当前只支持 k3；请从模型列表选择 k3。",
                "The SuiXiang K3 experimental route currently supports only k3; choose k3 from the model list."));
        }
    }

    private async Task RefreshCapabilitiesAsync()
    {
        HostStatusDot.Background = ResourceBrush("NeutralStatusBrush");
        RemoteStatusDot.Background = ResourceBrush("NeutralStatusBrush");
        HostCapabilityStatusText.Text = T(
            "官方登录与功能开关：正在检测…",
            "Official sign-in and feature flags: checking...");
        var diagnostics = await _hostDiagnosticsService.InspectAsync();
        _lastHostDiagnostics = diagnostics;
        HostCapabilityStatusText.Text = F(
            "官方宿主：{0}",
            "Official host: {0}",
            diagnostics.Summary);

        RemoteCapabilityStatusText.Text = diagnostics.ChatGptLoggedIn switch
        {
            false =>
                T(
                    "手机 Remote：官方 ChatGPT 登录未就绪，需先在 Codex 中登录。",
                    "Mobile Remote: official ChatGPT sign-in is not ready. Sign in through Codex first."),
            true when diagnostics.Features.RemotePluginEnabled == false =>
                T(
                    "手机 Remote：本机 remote_plugin 开关未启用。",
                    "Mobile Remote: the local remote_plugin feature flag is disabled."),
            true when diagnostics.Features.RemotePluginEnabled == true =>
                T(
                    "手机 Remote：本机登录与功能开关已就绪；仍需从官方 Codex 的“Set up Remote”入口完成手机实机配对。",
                    "Mobile Remote: local sign-in and feature flags are ready; physical phone pairing must still be completed from “Set up Remote” in official Codex."),
            _ =>
                T(
                    "手机 Remote：本机状态未完整识别；可打开官方 Codex 检查是否显示“Set up Remote”。",
                    "Mobile Remote: local state was not fully detected; open official Codex and check for “Set up Remote”.")
        };
        UpdateHostStatusDots();
    }

    private void UpdateHostStatusDots()
    {
        if (_lastHostDiagnostics is not { } diagnostics)
        {
            HostStatusDot.Background = ResourceBrush("NeutralStatusBrush");
            RemoteStatusDot.Background = ResourceBrush("NeutralStatusBrush");
            return;
        }

        var coreHostReady =
            diagnostics.ChatGptLoggedIn == true &&
            diagnostics.Features.AppsEnabled == true &&
            diagnostics.Features.PluginsEnabled == true &&
            diagnostics.Features.ImageGenerationEnabled == true;
        HostStatusDot.Background = coreHostReady
            ? ResourceBrush("ThirdPartyStatusBrush")
            : !diagnostics.CliAvailable || diagnostics.ChatGptLoggedIn == false
                ? ResourceBrush("DangerBrush")
                : ResourceBrush("WarningBrush");
        RemoteStatusDot.Background =
            diagnostics.ChatGptLoggedIn == true &&
            diagnostics.Features.RemotePluginEnabled == true
                ? ResourceBrush("WarningBrush")
                : diagnostics.ChatGptLoggedIn == false ||
                  diagnostics.Features.RemotePluginEnabled == false
                    ? ResourceBrush("DangerBrush")
                    : ResourceBrush("NeutralStatusBrush");
    }

    private bool HasGeneratedImage() =>
        !string.IsNullOrWhiteSpace(_settings.LastGeneratedImagePath) &&
        File.Exists(_settings.LastGeneratedImagePath);

    private string CurrentToolEndpointFingerprint() =>
        ConnectionTestService.EndpointFingerprint(
            _settings.ThirdPartyBaseUrl,
            _settings.ThirdPartyModel);

    private string CurrentCompatibilityEndpointFingerprint() =>
        CompatibilityFingerprint(
            DraftProviderProfile ?? ActiveProviderProfile,
            BaseUrlTextBox.Text,
            ModelComboBox.Text);

    private static string CompatibilityFingerprint(
        ProviderProfile profile,
        string? displayedBaseUrl = null,
        string? displayedModel = null)
    {
        var baseUrl = displayedBaseUrl?.Trim();
        var model = displayedModel?.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = profile.BaseUrl;
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            model = profile.Model;
        }

        // K3 is tested against its upstream Chat Completions contract, but
        // the persisted capability belongs to the local Responses route that
        // Codex actually consumes. Keep this identity stable across tests and
        // switches so a direct SuiXiang model can never reuse a K3 result.
        if (SettingsStore.IsKimiBaseUrl(baseUrl) &&
            string.Equals(model, AppPaths.DefaultKimiModel, StringComparison.Ordinal))
        {
            baseUrl = AppPaths.KimiRouterBaseUrl;
        }

        return ConnectionTestService.EndpointFingerprint(baseUrl, model!);
    }

    private string CurrentImageEndpointFingerprint() =>
        ConnectionTestService.EndpointFingerprint(
            _settings.ThirdPartyBaseUrl,
            AppPaths.DefaultThirdPartyImageModel);

    private void UpdatePersistedProviderCapabilityStatuses()
    {
        string? displayedToolFingerprint = null;
        string? displayedImageFingerprint = null;
        try
        {
            var displayedBaseUrl =
                ConfigService.NormalizeBaseUrl(BaseUrlTextBox.Text);
            var displayedModel = ModelComboBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(displayedModel))
            {
                displayedToolFingerprint =
                    ConnectionTestService.EndpointFingerprint(
                        displayedBaseUrl,
                        displayedModel);
            }
            displayedImageFingerprint =
                ConnectionTestService.EndpointFingerprint(
                    displayedBaseUrl,
                    AppPaths.DefaultThirdPartyImageModel);
        }
        catch (ArgumentException)
        {
            // An incomplete edit is untested until it becomes a valid URL.
        }

        ToolCapabilityStatusText.Text =
            _settings.LastSuccessfulToolTestUtc is { } toolTestUtc &&
            displayedToolFingerprint is not null &&
            _settings.LastToolTestedEndpointFingerprint ==
            displayedToolFingerprint
                ? F(
                    "第三方插件工具调用：已实测通过（{0}）",
                    "Third-party plugin tools: verified ({0})",
                    FormatLocalTime(toolTestUtc))
                : T(
                    "第三方插件工具调用：尚未实测",
                    "Third-party plugin tools: not tested");
        ToolStatusDot.Background =
            _settings.LastSuccessfulToolTestUtc is not null &&
            displayedToolFingerprint is not null &&
            _settings.LastToolTestedEndpointFingerprint == displayedToolFingerprint
                ? ResourceBrush("ThirdPartyStatusBrush")
                : ResourceBrush("NeutralStatusBrush");

        ImageCapabilityStatusText.Text =
            _settings.LastSuccessfulImageTestUtc is { } imageTestUtc &&
            displayedImageFingerprint is not null &&
            _settings.LastImageTestedEndpointFingerprint ==
            displayedImageFingerprint &&
            HasGeneratedImage()
                ? F(
                    "第三方图片生成：已实测通过（{0}），测试图片已保存",
                    "Third-party image generation: verified ({0}); test image saved",
                    FormatLocalTime(imageTestUtc))
                : T(
                    "第三方图片生成：尚未实测",
                    "Third-party image generation: not tested");
        ImageStatusDot.Background =
            _settings.LastSuccessfulImageTestUtc is not null &&
            displayedImageFingerprint is not null &&
            _settings.LastImageTestedEndpointFingerprint == displayedImageFingerprint &&
            HasGeneratedImage()
                ? ResourceBrush("ThirdPartyStatusBrush")
                : ResourceBrush("NeutralStatusBrush");
        UpdateKimiUi();
    }

    private void UpdateKimiUi()
    {
        var retiredSelection = IsKimiModelSelection();
        var retiredCurrentRoute = _lastConfigStatus is { } status &&
                                  (SettingsStore.IsKimiLoopbackBaseUrl(status.BaseUrl) ||
                                   string.Equals(
                                       status.ModelCatalogJson,
                                       AppPaths.KimiModelCatalogFileName,
                                       StringComparison.Ordinal));
        var showRetiredNotice = retiredSelection || retiredCurrentRoute;
        KimiExperimentalNoticeText.Visibility = showRetiredNotice
            ? Visibility.Visible
            : Visibility.Collapsed;
        // Keep the model editable so a legacy K3 user can select an available
        // SuiXiang/OpenAI model and leave the retired route.
        ModelComboBox.IsEditable = true;
        KimiExperimentalNoticeText.Text = retiredCurrentRoute
            ? T(
                "当前仍是旧 K3 配置，该线路已停用。请切换到官方 Codex，或改用随想当前支持的 OpenAI 模型；旧配置、聊天记录和密钥不会被删除。",
                "The current configuration still uses the retired K3 route. Switch to Official Codex or a currently supported SuiXiang OpenAI model; existing configuration, chat history, and credentials are not deleted.")
            : T(
                "K3 线路已停用，不再支持新建、测试或切换。请改用官方 Codex 或其他可用模型。",
                "The K3 route has been retired and can no longer be created, tested, or selected. Use Official Codex or another available model.");
        SwitchThirdPartyButton.Content = T("切换到第三方", "Switch to third-party");
        TestConnectionButton.Content = T(
            "测试 Responses 兼容性",
            "Test Responses compatibility");
        SaveKeyButton.IsEnabled = !_isBusy && !retiredSelection;
        DeleteKeyButton.IsEnabled = !_isBusy && !retiredSelection;
        TestConnectionButton.IsEnabled = !_isBusy && !retiredSelection;
        SwitchThirdPartyButton.IsEnabled = !_isBusy && !retiredSelection;
        RefreshModelsButton.IsEnabled = !_isBusy;
    }

    private bool IsKimiModelSelection()
    {
        return ProviderAvailabilityPolicy.IsRetiredKimiRoute(
            BaseUrlTextBox.Text,
            ModelComboBox.Text);
    }

    private void ClearCurrentToolTestResult()
    {
        if (_settings.LastToolTestedEndpointFingerprint ==
            CurrentToolEndpointFingerprint())
        {
            _settings.LastSuccessfulToolTestUtc = null;
            _settings.LastToolTestedEndpointFingerprint = null;
        }
    }

    private void ClearCurrentCompatibilityTestResult()
    {
        if (_settings.LastTestedEndpointFingerprint ==
            CurrentCompatibilityEndpointFingerprint())
        {
            _settings.LastSuccessfulCompatibilityTestUtc = null;
            _settings.LastTestedEndpointFingerprint = null;
        }
    }

    private void ClearCurrentImageTestResult()
    {
        if (_settings.LastImageTestedEndpointFingerprint ==
            CurrentImageEndpointFingerprint())
        {
            _settings.LastSuccessfulImageTestUtc = null;
            _settings.LastImageTestedEndpointFingerprint = null;
        }
    }

    private static string FormatLocalTime(DateTimeOffset timestamp) =>
        timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    private static bool IsSuiXiangBaseUrl(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) &&
        uri.Host.Equals("sui-xiang.com", StringComparison.OrdinalIgnoreCase);

    private void UpdateModeBadge(ConfigStatus status)
    {
        _lastConfigStatus = status;
        switch (status.Mode)
        {
            case ProviderMode.Official:
                ModeBadge.Background = ResourceBrush("OfficialStatusBrush");
                ProviderStatusDot.Background = ResourceBrush("OfficialStatusBrush");
                ModeBadgeText.Text = T("官方 OpenAI", "Official OpenAI");
                NavStatusText.Text = T("官方线路", "Official");
                break;
            case ProviderMode.ThirdParty:
                ModeBadge.Background = ResourceBrush("ThirdPartyStatusBrush");
                ProviderStatusDot.Background = ResourceBrush("ThirdPartyStatusBrush");
                ModeBadgeText.Text = T("第三方线路", "Third-party");
                NavStatusText.Text = T("第三方线路", "Third-party");
                break;
            default:
                ModeBadge.Background = ResourceBrush("WarningStatusBrush");
                ProviderStatusDot.Background = ResourceBrush("WarningBrush");
                ModeBadgeText.Text = T("需要初始化", "Setup required");
                NavStatusText.Text = T("需要初始化", "Setup required");
                break;
        }
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        if (_isBusy)
        {
            return;
        }

        SetBusy(true);
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            ShowFailure(T("操作失败", "Operation failed"), exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        var lunaRouteStatus = _configService.ReadStatus();
        var retiredKimiSelection = IsKimiModelSelection();
        SaveKeyButton.IsEnabled = !busy && !retiredKimiSelection;
        DeleteKeyButton.IsEnabled = !busy && !retiredKimiSelection;
        TestConnectionButton.IsEnabled = !busy && !retiredKimiSelection;
        RefreshCapabilitiesButton.IsEnabled = !busy;
        TestToolCallingButton.IsEnabled = !busy;
        TestImageGenerationButton.IsEnabled = !busy;
        OpenGeneratedImageButton.IsEnabled = !busy && HasGeneratedImage();
        OpenRemoteSettingsButton.IsEnabled = !busy;
        SwitchThirdPartyButton.IsEnabled = !busy && !retiredKimiSelection;
        SwitchOfficialButton.IsEnabled = !busy;
        DailyPrimaryActionButton.IsEnabled = !busy;
        HeaderRefreshButton.IsEnabled = !busy;
        ChineseLanguageButton.IsEnabled = !busy;
        EnglishLanguageButton.IsEnabled = !busy;
        LightThemeButton.IsEnabled = !busy;
        DarkThemeButton.IsEnabled = !busy;
        SystemThemeButton.IsEnabled = !busy;
        BaseUrlTextBox.IsEnabled = !busy;
        ModelComboBox.IsEnabled = !busy;
        ProviderProfileComboBox.IsEnabled = !busy;
        NewProviderProfileButton.IsEnabled = !busy;
        RefreshModelsButton.IsEnabled = !busy;
        ApiKeyPasswordBox.IsEnabled = !busy;
        RestartCheckBox.IsEnabled = false;
        RefreshBackupsButton.IsEnabled = !busy;
        OpenBackupFolderButton.IsEnabled = !busy;
        OpenSelectedBackupButton.IsEnabled =
            !busy && BackupsDataGrid.SelectedItem is BackupRow;
        OpenDataFolderButton.IsEnabled = !busy;
        OpenGitHubButton.IsEnabled = !busy;
        RunSetupAgainButton.IsEnabled = !busy;
        EnableSolUltraButton.IsEnabled = !busy && !_solUltraAvailable;
        InstallLunaWorkerButton.IsEnabled =
            !busy &&
            lunaRouteStatus.Mode != ProviderMode.Unknown &&
            !LunaWorkerAgentService.IsSuiXiangRoute(lunaRouteStatus) &&
            _lunaWorkerAgentStatus?.State == ManagedAgentState.Missing;
        OpenLunaAgentsFolderButton.IsEnabled = !busy;
        UpdateActionButton.IsEnabled = !busy && !_isCheckingForUpdates;
        OpenUpdateReleaseButton.IsEnabled = !busy;
        BusyProgressBar.Visibility =
            busy ? Visibility.Visible : Visibility.Collapsed;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    private void ShowFailure(string title, Exception exception)
    {
        OperationStatusText.Text = exception.Message;
        MessageBox.Show(
            this,
            exception.Message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void ApplyLanguage()
    {
        NavigationSubtitleText.Text = T("供应商控制台", "Provider console");
        HomeNavigationText.Text = T("首页", "Home");
        ProvidersNavigationText.Text = T("供应商", "Providers");
        DiagnosticsNavigationText.Text = T("能力诊断", "Diagnostics");
        BackupsNavigationText.Text = T("备份记录", "Backups");
        SettingsNavigationText.Text = T("设置", "Settings");
        HomeNavigationButton.ToolTip = HomeNavigationText.Text;
        ProvidersNavigationButton.ToolTip = ProvidersNavigationText.Text;
        DiagnosticsNavigationButton.ToolTip = DiagnosticsNavigationText.Text;
        BackupsNavigationButton.ToolTip = BackupsNavigationText.Text;
        SettingsNavigationButton.ToolTip = SettingsNavigationText.Text;

        HeaderRefreshButton.ToolTip = T("刷新状态", "Refresh status");
        AutomationProperties.SetName(
            HeaderRefreshButton,
            T("刷新状态", "Refresh status"));
        TaglineText.Text = T(
            "官方登录与第三方线路，共用同一份聊天历史。",
            "Official sign-in and third-party routes share one chat history.");
        ModeBadgeText.Text = T("正在检测", "Checking");
        CurrentStatusTitleText.Text = T("当前线路", "Current route");
        StableHistoryText.Text = T(
            "固定历史分区：OpenAI（切换时永远不改）",
            "Stable history partition: OpenAI (never changed when switching)");
        if (_lastConfigStatus is { } currentStatus)
        {
            UpdateDailyPrimaryAction(currentStatus);
        }
        HomeSafetyTitleText.Text = T("本机保护", "Local safeguards");
        OfficialAuthSafetyTitleText.Text = T(
            "官方登录保持不变",
            "Official sign-in stays intact");
        OfficialAuthSafetyDescriptionText.Text = T(
            "切换第三方时不会删除或覆盖 ChatGPT 登录。",
            "Switching to a third party does not delete or overwrite your ChatGPT sign-in.");
        HistorySafetyTitleText.Text = T(
            "聊天记录不搬家",
            "Chat history stays put");
        HistorySafetyDescriptionText.Text = T(
            "两条线路都使用稳定的 OpenAI 历史分区。",
            "Both routes use the stable OpenAI history partition.");
        BackupSafetyTitleText.Text = T(
            "每次写入先备份",
            "Backup before every write");
        BackupSafetyDescriptionText.Text = T(
            "config.toml 会在切换前创建带时间戳的副本。",
            "A timestamped config.toml copy is created before switching.");
        HomeOpenProvidersButton.Content = T("管理供应商", "Manage providers");
        HomeOpenDiagnosticsButton.Content = T(
            "查看能力诊断",
            "View diagnostics");
        OpenBackupsButton.Content = T("查看备份", "View backups");

        ProvidersLeadText.Text = T(
            "选择线路并管理第三方 Responses API。切换不会改变聊天历史分区。",
            "Choose a route and manage the third-party Responses API. Switching never changes the chat-history partition.");
        ThirdPartyTitleText.Text = T("第三方线路", "Third-party route");
        SavedProfileLabelText.Text = T("已保存账号", "Saved accounts");
        SavedProfileDescriptionText.Text = T(
            "选择只会载入草稿；点击切换成功后才会成为当前线路。",
            "Selection only loads a draft. It becomes the active route after a successful switch.");
        NewProviderProfileButton.Content = T("添加账号", "Add account");
        NewProviderProfileButton.ToolTip = T(
            "添加一个独立的 API Key；输入并保存密钥或成功切换后才会建立账号。",
            "Add an independent API key. The account is created only after the key is saved or a switch succeeds.");
        KimiExperimentalNoticeText.Text = T(
            "K3 线路已停用。旧配置、聊天记录和密钥会保留，请切换到官方 Codex 或其他可用线路。",
            "The K3 route has been retired. Existing configuration, chat history, and credentials are preserved; switch to Official Codex or another available route.");
        KeyStorageDescriptionText.Text = T(
            "密钥保存在 Windows 凭据管理器；不会写进 config.toml、源码或日志。",
            "The key is stored in Windows Credential Manager and is never written to config.toml, source code, or logs.");
        BaseUrlLabelText.Text = T("Base URL", "Base URL");
        ModelLabelText.Text = T("模型", "Model");
        RefreshModelsButton.Content = T("刷新模型列表", "Refresh model list");
        RefreshModelsButton.ToolTip = T(
            "使用当前 Base URL 对应的密钥读取模型列表；不会跨线路复用密钥。",
            "Read models with the key for the current Base URL; keys are never reused across routes.");
        AutomationProperties.SetName(
            RefreshModelsButton,
            T("刷新模型列表", "Refresh model list"));
        if (string.IsNullOrWhiteSpace(ModelDiscoveryStatusText.Text))
        {
            ModelDiscoveryStatusText.Text = T(
                "可刷新模型列表，也可以直接输入自定义模型 ID。",
                "Refresh the model list, or enter a custom model ID directly.");
        }
        ApiKeyLabelText.Text = T("新 API Key", "New API key");
        SaveKeyButton.Content = T("保存密钥", "Save key");
        DeleteKeyButton.Content = T("删除密钥", "Delete key");
        ThirdPartyPrivacyWarningText.Text = T(
            "第三方服务会接收你发送给 Codex 的提示词、代码片段和工具上下文。",
            "The third-party service receives prompts, code snippets, and tool context sent through Codex.");
        TestConnectionButton.Content = T(
            "测试 Responses 兼容性",
            "Test Responses compatibility");
        SwitchThirdPartyButton.Content = T("切换到第三方", "Switch to third-party");

        CapabilityTitleText.Text = T("能力诊断", "Capability diagnostics");
        CapabilityDescriptionText.Text = T(
            "官方账号能力保留在本机；第三方模型仍需通过 Responses 工具协议与图片接口实测。",
            "Official account capabilities remain local; third-party models still require real Responses tool-protocol and image API tests.");
        HostDiagnosticTitleText.Text = T("官方宿主", "Official host");
        ToolDiagnosticTitleText.Text = T("插件工具协议", "Plugin tool protocol");
        ImageDiagnosticTitleText.Text = T("图片生成", "Image generation");
        RemoteDiagnosticTitleText.Text = T(
            "手机 Remote（本机前置条件）",
            "Mobile Remote (local prerequisites)");
        HostCapabilityStatusText.Text = T(
            "官方登录与功能开关：正在检测…",
            "Official sign-in and feature flags: checking...");
        RemoteCapabilityStatusText.Text = T(
            "首次手机配对需从官方 Codex 侧栏的“Set up Remote”入口开始。",
            "Initial phone pairing starts from “Set up Remote” in the official Codex sidebar.");
        RefreshCapabilitiesButton.Content = T("刷新官方能力", "Refresh official capabilities");
        TestToolCallingButton.Content = T("测试插件工具调用", "Test plugin tool calling");
        TestImageGenerationButton.Content = T("生成测试图片", "Generate test image");
        OpenGeneratedImageButton.Content = T("打开测试图片", "Open test image");
        OpenRemoteSettingsButton.Content = T("打开官方 Codex", "Open official Codex");

        OfficialTitleText.Text = T("官方 OpenAI Codex", "Official OpenAI Codex");
        OfficialDescriptionText.Text = T(
            "使用现有 ChatGPT 登录与订阅额度；官方登录凭据不会被删除或覆盖。",
            "Uses your existing ChatGPT sign-in and subscription allowance; official credentials are never deleted or overwritten.");
        SwitchOfficialButton.Content = T("切换到官方", "Switch to official");

        RestartTitleText.Text = T(
            "切换时重启 Codex",
            "Codex restarts during a switch");
        RestartDescriptionText.Text = T(
            "为让新线路和模型目录立即生效，切换会固定停止、写入并校验配置后再启动 Codex。这会中断正在运行的任务；写入前会生成备份。",
            "To apply the new route and model catalog immediately, every switch stops Codex, writes and verifies the configuration, then starts Codex. This interrupts active tasks; a backup is created before the write.");

        BackupsLeadText.Text = T(
            "每次切换前创建的 config.toml 备份副本。",
            "Backup copies of config.toml created before each switch.");
        RefreshBackupsButton.Content = T("刷新", "Refresh");
        OpenSelectedBackupButton.Content = T(
            "打开所选位置",
            "Open selected location");
        OpenBackupFolderButton.Content = T(
            "打开备份文件夹",
            "Open backup folder");
        BackupTimeColumn.Header = T("时间", "Time");
        BackupSizeColumn.Header = T("大小", "Size");
        BackupFolderColumn.Header = T("备份 ID", "Backup ID");
        BackupPathColumn.Header = T("文件", "File");
        BackupsEmptyText.Text = T("还没有备份", "No backups yet");

        SettingsLeadText.Text = T(
            "界面与切换行为会保存在本机，不会改动聊天记录。",
            "Appearance and switching preferences are stored locally and do not modify chat history.");
        RunSetupAgainButton.Content = T("重新设置", "Run setup again");
        RunSetupAgainButton.ToolTip = T(
            "重新运行首次设置向导",
            "Run the first-time setup wizard again");
        SolUltraTitleText.Text = "Sol Ultra";
        SolUltraDescriptionText.Text = T(
            "简体中文 Codex 会把 xhigh 和 Ultra 都显示为“极高”。Ultra 是菜单最底部带“更快消耗使用额度”的一项；Luna Agent 仍使用 Max。",
            "Simplified Chinese Codex labels both xhigh and Ultra as 'Extremely high'. Ultra is the bottom item with the faster usage warning; the Luna task agent remains on Max.");
        LunaWorkerTitleText.Text = T(
            "Luna 任务 Agent",
            "Luna task agent");
        LunaWorkerDescriptionText.Text = T(
            "可选安装 Luna 任务 Agent（gpt-5.6-luna / max）。官方 OpenAI 支持；随想不支持并会自动停用；其他第三方由供应商决定。切回官方后自动恢复。",
            "Optionally install the Luna task agent (gpt-5.6-luna / max). Official OpenAI supports it; the agent is automatically disabled on SuiXiang. Support on other third-party routes depends on the provider, and the agent is restored when leaving SuiXiang.");
        UpdateCheckTitleText.Text = T("应用更新", "Application updates");
        UpdateCheckDescriptionText.Text = T(
            "启动后自动检查 GitHub 最新正式版本；也可以随时手动检查。",
            "Automatically check GitHub for the latest stable release at startup, or check manually at any time.");
        LanguageSettingTitleText.Text = T("界面语言", "Interface language");
        LanguageSettingDescriptionText.Text = T(
            "选择中文或英文。",
            "Choose Chinese or English.");
        ThemeSettingTitleText.Text = T("外观", "Appearance");
        ThemeSettingDescriptionText.Text = T(
            "选择浅色、深色或跟随 Windows。",
            "Choose light, dark, or follow Windows.");
        LightThemeButton.Content = T("浅色", "Light");
        DarkThemeButton.Content = T("深色", "Dark");
        SystemThemeButton.Content = T("系统", "System");
        AboutTitleText.Text = T("关于", "About");
        SettingsStorageDescriptionText.Text = T(
            "设置、诊断和备份保存在本机 LocalAppData。",
            "Settings, diagnostics, and backups are stored in local AppData.");
        OpenDataFolderButton.Content = T("打开数据目录", "Open data folder");
        OpenLunaAgentsFolderButton.Content = T(
            "打开 agents 文件夹",
            "Open agents folder");

        ChineseLanguageButton.ToolTip = T("切换到中文", "Switch to Chinese");
        EnglishLanguageButton.ToolTip = T("切换到英文", "Switch to English");
        LightThemeButton.ToolTip = T("使用浅色外观", "Use light appearance");
        DarkThemeButton.ToolTip = T("使用深色外观", "Use dark appearance");
        SystemThemeButton.ToolTip = T("跟随 Windows 外观", "Follow Windows appearance");
        UpdateSolUltraStatus();
        UpdateLunaWorkerAgentStatus();
        RefreshProviderProfilePicker();
        UpdateKimiUi();
        UpdateUpdateCheckUi();
        UpdateLanguageButtons();
        UpdateThemeButtons();
        UpdatePageTitle();
    }

    private void UpdateLanguageButtons()
    {
        var chineseSelected = Localizer.Current == AppLanguage.Chinese;
        ChineseLanguageButton.IsChecked = chineseSelected;
        EnglishLanguageButton.IsChecked = !chineseSelected;
    }

    private void UpdateVersionText()
    {
        var displayVersion = CurrentApplicationVersion().ToString(3);
        VersionText.Text = $"v{displayVersion}";
        SettingsVersionText.Text = $"Codex Provider Switcher v{displayVersion}";
    }

    private static Version CurrentApplicationVersion() =>
        GitHubReleaseUpdateService.NormalizeVersion(
            typeof(MainWindow).Assembly.GetName().Version ?? new Version(1, 4, 3));

    private Brush ResourceBrush(string key) =>
        (Brush)FindResource(key);

    private static string T(string chinese, string english) =>
        Localizer.Text(chinese, english);

    private static string F(
        string chineseFormat,
        string englishFormat,
        params object?[] arguments) =>
        Localizer.Format(chineseFormat, englishFormat, arguments);
}
