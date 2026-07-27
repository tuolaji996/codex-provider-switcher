using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using CodexProviderSwitcher.Core;

namespace CodexProviderSwitcher;

public partial class SetupWizardWindow : Window
{
    private const string SuiXiangHomeUrl = "https://sui-xiang.com/";
    private static readonly TimeSpan WebViewSoftTimeout =
        TimeSpan.FromSeconds(20);

    private enum WizardPage
    {
        Welcome,
        EnvironmentCheck,
        Choice,
        SuiXiangLogin,
        ProviderDetails,
        Completion
    }

    private sealed record EnvironmentCheckSnapshot(
        bool ConfigExists,
        ConfigStatus ConfigStatus,
        string? ConfigError,
        bool WebViewAvailable,
        string? WebViewVersion,
        string? WebViewError,
        bool ActiveProfileTargetValid);

    private enum CheckVisual
    {
        Checking,
        Ready,
        Attention,
        Informational
    }

    private readonly SwitcherSettings _settings;
    private readonly CancellationTokenSource _windowLifetime = new();
    private SetupWizardResult? _draft;
    private SetupWizardResult? _pendingResult;
    private Task? _webViewInitializationTask;
    private CoreWebView2? _configuredBrowser;
    private CancellationTokenSource? _webViewStatusTimeout;
    private WizardPage _currentPage;
    private WizardPage _completionBackPage = WizardPage.Choice;
    private bool _isSuiXiangChoice;
    private bool _webViewInitialized;
    private bool _webViewInitializationInProgress;
    private bool _webViewNavigationInProgress;
    private bool _webViewNavigationTimedOut;
    private bool _clearWebViewDataInProgress;
    private bool _browserProcessUnavailable;
    private bool _isClosed;
    private ulong? _activeNavigationId;
    private int _webViewStatusRevision;
    private bool _environmentCheckInProgress;
    private bool? _detailsPreparedForSuiXiang;
    private int _environmentCheckRevision;
    private EnvironmentCheckSnapshot? _environmentSnapshot;
    private string? _statusChinese;
    private string? _statusEnglish;
    private string? _webViewStatusChinese;
    private string? _webViewStatusEnglish;

    public SetupWizardResult? Result { get; private set; }

    public SetupWizardWindow(SwitcherSettings settings, SetupWizardResult? draft = null)
    {
        _settings = settings;
        _draft = draft;
        InitializeComponent();
        Loaded += SetupWizardWindow_Loaded;
        Closed += SetupWizardWindow_Closed;
    }

    private void SetupWizardWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyLanguage();
        ShowPage(WizardPage.Welcome);
    }

    private void SetupWizardWindow_Closed(object? sender, EventArgs e)
    {
        _isClosed = true;
        _environmentCheckRevision++;
        _webViewStatusRevision++;
        _windowLifetime.Cancel();
        StopWebViewSoftTimeout();
        DetachSuiXiangBrowser();
        _pendingResult = null;
        _draft = null;
        WizardApiKeyPasswordBox.Clear();
        SuiXiangWebView.Dispose();
        _windowLifetime.Dispose();
    }

    private async void WelcomeContinueButton_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(WizardPage.EnvironmentCheck);
        await RunEnvironmentChecksAsync();
    }

    private void EnvironmentCheckBackButton_Click(object sender, RoutedEventArgs e) =>
        ShowPage(WizardPage.Welcome);

    private async void EnvironmentRecheckButton_Click(object sender, RoutedEventArgs e) =>
        await RunEnvironmentChecksAsync();

    private void EnvironmentContinueButton_Click(object sender, RoutedEventArgs e) =>
        ShowPage(WizardPage.Choice);

    private async void SuiXiangChoiceButton_Click(object sender, RoutedEventArgs e)
    {
        _isSuiXiangChoice = true;
        ShowPage(WizardPage.SuiXiangLogin);
        await EnsureSuiXiangBrowserAsync();
    }

    private void CustomChoiceButton_Click(object sender, RoutedEventArgs e)
    {
        _isSuiXiangChoice = false;
        PrepareProviderDetails();
        ShowPage(WizardPage.ProviderDetails);
    }

    private void OfficialChoiceButton_Click(object sender, RoutedEventArgs e)
    {
        _pendingResult = new SetupWizardResult(
            true,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            null);
        _completionBackPage = WizardPage.Choice;
        SetWizardStatus(
            "确认后才会应用官方线路设置。",
            "The official route setting is applied only after confirmation.");
        ShowPage(WizardPage.Completion);
    }

    private void SuiXiangBackButton_Click(object sender, RoutedEventArgs e) =>
        ShowPage(WizardPage.Choice);

    private void SuiXiangContinueButton_Click(object sender, RoutedEventArgs e)
    {
        PrepareProviderDetails();
        ShowPage(WizardPage.ProviderDetails);
    }

    private async void SuiXiangRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosed ||
            _webViewInitializationInProgress ||
            _clearWebViewDataInProgress)
        {
            return;
        }

        if (_configuredBrowser is null)
        {
            _webViewInitializationTask = null;
            await EnsureSuiXiangBrowserAsync();
            return;
        }

        if (!_webViewNavigationInProgress || _webViewNavigationTimedOut)
        {
            _webViewNavigationTimedOut = false;
            _configuredBrowser.Reload();
        }
    }

    private async void ClearSuiXiangLoginButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosed || _clearWebViewDataInProgress)
        {
            return;
        }

        if (MessageBox.Show(
                this,
                T(
                    "这会清除本应用内保存的随想登录 Cookie 和网站数据，是否继续？",
                    "This clears SuiXiang sign-in cookies and site data stored by this app. Continue?"),
                T("清除随想登录数据", "Clear SuiXiang sign-in data"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            if (_configuredBrowser is not { } browser)
            {
                SetWebViewStatus(
                    "登录窗口尚未准备好。请先点击“刷新”重试。",
                    "The sign-in window is not ready. Select Refresh to retry.");
                return;
            }

            _clearWebViewDataInProgress = true;
            UpdateWebViewControls();
            await browser.Profile.ClearBrowsingDataAsync();
            if (_isClosed)
            {
                return;
            }

            browser.Navigate(SuiXiangHomeUrl);
            SetWizardStatus(
                "已清除本应用内的随想登录数据。",
                "SuiXiang sign-in data stored by this app was cleared.");
        }
        catch (Exception exception)
        {
            if (_isClosed)
            {
                return;
            }

            SetWizardStatus(
                $"无法清除随想登录数据：{exception.Message}",
                $"Could not clear SuiXiang sign-in data: {exception.Message}");
        }
        finally
        {
            _clearWebViewDataInProgress = false;
            if (!_isClosed)
            {
                UpdateWebViewControls();
            }
        }
    }

    private void ProviderDetailsBackButton_Click(object sender, RoutedEventArgs e) =>
        ShowPage(_isSuiXiangChoice ? WizardPage.SuiXiangLogin : WizardPage.Choice);

    private void ConnectProviderButton_Click(object sender, RoutedEventArgs e)
    {
        var baseUrl = WizardBaseUrlTextBox.Text.Trim();
        var model = WizardModelTextBox.Text.Trim();
        var apiKey = WizardApiKeyPasswordBox.Password.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl) ||
            string.IsNullOrWhiteSpace(model) ||
            apiKey.Length < 16)
        {
            SetWizardStatus(
                "请填写 Base URL、模型和完整的新 API Key。",
                "Enter a Base URL, model, and complete new API key.");
            return;
        }

        _pendingResult = new SetupWizardResult(
            false,
            _isSuiXiangChoice ? ProviderKinds.SuiXiang : ProviderKinds.Custom,
            ProviderNameTextBox.Text.Trim(),
            baseUrl,
            model,
            apiKey);
        _completionBackPage = WizardPage.ProviderDetails;
        SetWizardStatus(
            "确认后会先验证连接，成功后才切换线路。",
            "After confirmation, the connection is validated before the route is switched.");
        ShowPage(WizardPage.Completion);
    }

    private void CompletionBackButton_Click(object sender, RoutedEventArgs e) =>
        ShowPage(_completionBackPage);

    private void CompletionFinishButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingResult is null)
        {
            ShowPage(WizardPage.Choice);
            return;
        }

        Result = _pendingResult;
        DialogResult = true;
    }

    private void CancelWizardButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void ChineseLanguageButton_Click(object sender, RoutedEventArgs e) =>
        ChangeLanguage(AppLanguage.Chinese);

    private void EnglishLanguageButton_Click(object sender, RoutedEventArgs e) =>
        ChangeLanguage(AppLanguage.English);

    private void ChangeLanguage(AppLanguage language)
    {
        if (Localizer.Current == language)
        {
            return;
        }

        _settings.UiLanguage = Localizer.ToCode(language);
        Localizer.Use(language);
        ApplyLanguage();
    }

    private void ShowPage(WizardPage page)
    {
        _currentPage = page;
        WelcomePanel.Visibility = page == WizardPage.Welcome
            ? Visibility.Visible
            : Visibility.Collapsed;
        ChoicePanel.Visibility = page == WizardPage.Choice
            ? Visibility.Visible
            : Visibility.Collapsed;
        SuiXiangLoginPanel.Visibility = page == WizardPage.SuiXiangLogin
            ? Visibility.Visible
            : Visibility.Collapsed;
        ProviderDetailsPanel.Visibility = page == WizardPage.ProviderDetails
            ? Visibility.Visible
            : Visibility.Collapsed;
        EnvironmentCheckPanel.Visibility = page == WizardPage.EnvironmentCheck
            ? Visibility.Visible
            : Visibility.Collapsed;
        CompletionPanel.Visibility = page == WizardPage.Completion
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApplyLanguage();
    }

    private void PrepareProviderDetails()
    {
        if (_detailsPreparedForSuiXiang == _isSuiXiangChoice)
        {
            return;
        }

        var profile = _settings.ActiveProviderProfile;
        var draft = _draft;
        var draftMatchesChoice =
            draft is { UseOfficial: false } &&
            string.Equals(
                draft.ProviderKind,
                _isSuiXiangChoice ? ProviderKinds.SuiXiang : ProviderKinds.Custom,
                StringComparison.Ordinal);
        if (_isSuiXiangChoice)
        {
            ProviderNameTextBox.Text = draftMatchesChoice
                ? draft!.DisplayName
                : T("随想", "SuiXiang");
            WizardBaseUrlTextBox.Text = draftMatchesChoice
                ? draft!.BaseUrl
                : AppPaths.DefaultBaseUrl;
            WizardModelTextBox.Text = draftMatchesChoice
                ? draft!.Model
                : AppPaths.DefaultThirdPartyModel;
        }
        else
        {
            ProviderNameTextBox.Text = draftMatchesChoice
                ? draft!.DisplayName
                : profile?.DisplayName ?? string.Empty;
            WizardBaseUrlTextBox.Text = draftMatchesChoice
                ? draft!.BaseUrl
                : profile?.BaseUrl ?? string.Empty;
            WizardModelTextBox.Text = draftMatchesChoice
                ? draft!.Model
                : profile?.Model ?? string.Empty;
        }

        if (draftMatchesChoice && draft?.ApiKey is { Length: > 0 })
        {
            WizardApiKeyPasswordBox.Password = draft.ApiKey;
        }
        else
        {
            WizardApiKeyPasswordBox.Clear();
        }

        _detailsPreparedForSuiXiang = _isSuiXiangChoice;
        if (draftMatchesChoice)
        {
            _draft = null;
        }
    }

    private async Task RunEnvironmentChecksAsync()
    {
        var revision = ++_environmentCheckRevision;
        _environmentCheckInProgress = true;
        _environmentSnapshot = null;
        RenderEnvironmentCheck();
        EnvironmentRecheckButton.IsEnabled = false;
        EnvironmentContinueButton.IsEnabled = false;

        var activeCredentialTarget =
            _settings.ActiveProviderProfile?.CredentialTarget;
        var snapshot = await Task.Run(
            () => InspectEnvironment(activeCredentialTarget));
        if (revision != _environmentCheckRevision || !IsLoaded)
        {
            return;
        }

        _environmentSnapshot = snapshot;
        _environmentCheckInProgress = false;
        EnvironmentRecheckButton.IsEnabled = true;
        EnvironmentContinueButton.IsEnabled = true;
        RenderEnvironmentCheck();
        SetWizardStatus(
            "环境检查完成。你可以继续选择线路。",
            "Environment check complete. You can continue to choose a route.");
    }

    private static EnvironmentCheckSnapshot InspectEnvironment(
        string? activeCredentialTarget)
    {
        var configExists = File.Exists(AppPaths.ConfigPath);
        var configStatus = new ConfigStatus(
            ProviderMode.Unknown,
            string.Empty,
            null,
            null,
            null,
            false);
        string? configError = null;
        try
        {
            configStatus = new ConfigService().ReadStatus();
        }
        catch (Exception exception)
        {
            configError = exception.Message;
        }

        var webViewAvailable = false;
        string? webViewVersion = null;
        string? webViewError = null;
        try
        {
            webViewVersion =
                CoreWebView2Environment.GetAvailableBrowserVersionString();
            webViewAvailable = !string.IsNullOrWhiteSpace(webViewVersion);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            webViewError = "runtime-not-found";
        }
        catch (Exception exception)
        {
            webViewError = exception.Message;
        }

        return new EnvironmentCheckSnapshot(
            configExists,
            configStatus,
            configError,
            webViewAvailable,
            webViewVersion,
            webViewError,
            CredentialTargetFactory.IsValid(activeCredentialTarget));
    }

    private void RenderEnvironmentCheck()
    {
        ConfigCheckLabelText.Text = T("Codex 配置", "Codex configuration");
        WebViewCheckLabelText.Text = T("登录窗口", "Sign-in window");
        RouteCheckLabelText.Text = T("当前线路", "Current route");
        CredentialCheckLabelText.Text = T("凭据引用", "Credential reference");

        if (_environmentCheckInProgress || _environmentSnapshot is null)
        {
            SetCheckRow(
                ConfigCheckIconText,
                ConfigCheckStatusText,
                ConfigCheckDescriptionText,
                CheckVisual.Checking,
                T("检查中…", "Checking..."),
                string.Empty);
            SetCheckRow(
                WebViewCheckIconText,
                WebViewCheckStatusText,
                WebViewCheckDescriptionText,
                CheckVisual.Checking,
                T("检查中…", "Checking..."),
                string.Empty);
            SetCheckRow(
                RouteCheckIconText,
                RouteCheckStatusText,
                RouteCheckDescriptionText,
                CheckVisual.Checking,
                T("检查中…", "Checking..."),
                string.Empty);
            SetCheckRow(
                CredentialCheckIconText,
                CredentialCheckStatusText,
                CredentialCheckDescriptionText,
                CheckVisual.Checking,
                T("检查中…", "Checking..."),
                string.Empty);
            return;
        }

        var snapshot = _environmentSnapshot;
        if (!string.IsNullOrWhiteSpace(snapshot.ConfigError))
        {
            SetCheckRow(
                ConfigCheckIconText,
                ConfigCheckStatusText,
                ConfigCheckDescriptionText,
                CheckVisual.Attention,
                T("无法读取", "Could not read"),
                snapshot.ConfigError);
        }
        else if (snapshot.ConfigExists)
        {
            SetCheckRow(
                ConfigCheckIconText,
                ConfigCheckStatusText,
                ConfigCheckDescriptionText,
                CheckVisual.Ready,
                T("已找到", "Found"),
                T(
                    "config.toml 已就绪。",
                    "config.toml is ready."));
        }
        else
        {
            SetCheckRow(
                ConfigCheckIconText,
                ConfigCheckStatusText,
                ConfigCheckDescriptionText,
                CheckVisual.Attention,
                T("尚未创建", "Not created yet"),
                T(
                    "请先启动一次官方 Codex，让它创建 config.toml。",
                    "Start official Codex once so it can create config.toml."));
        }

        if (snapshot.WebViewAvailable)
        {
            SetCheckRow(
                WebViewCheckIconText,
                WebViewCheckStatusText,
                WebViewCheckDescriptionText,
                CheckVisual.Ready,
                T("可用", "Available"),
                F(
                    "Edge WebView2 Runtime {0}。仅随想登录需要它。",
                    "Edge WebView2 Runtime {0}. Only SuiXiang sign-in needs it.",
                    snapshot.WebViewVersion ?? string.Empty));
        }
        else
        {
            var webViewDescription =
                snapshot.WebViewError == "runtime-not-found"
                    ? T(
                        "未检测到 WebView2 Runtime；其他服务和官方线路仍可设置。",
                        "WebView2 Runtime was not found; other services and the official route can still be set up.")
                    : F(
                        "无法确认 WebView2 状态：{0}",
                        "Could not confirm WebView2 status: {0}",
                        snapshot.WebViewError ?? T("未知错误", "Unknown error"));
            SetCheckRow(
                WebViewCheckIconText,
                WebViewCheckStatusText,
                WebViewCheckDescriptionText,
                CheckVisual.Attention,
                T("需要注意", "Needs attention"),
                webViewDescription);
        }

        RenderRouteCheck(snapshot);
        RenderCredentialCheck(snapshot);
    }

    private void RenderRouteCheck(EnvironmentCheckSnapshot snapshot)
    {
        switch (snapshot.ConfigStatus.Mode)
        {
            case ProviderMode.Official:
                SetCheckRow(
                    RouteCheckIconText,
                    RouteCheckStatusText,
                    RouteCheckDescriptionText,
                    CheckVisual.Ready,
                    T("官方 Codex", "Official Codex"),
                    F(
                        "模型：{0}。历史分区保持 OpenAI。",
                        "Model: {0}. The history partition remains OpenAI.",
                        snapshot.ConfigStatus.Model ?? T("未指定", "Not specified")));
                break;

            case ProviderMode.ThirdParty:
                var endpoint = DisplayEndpoint(snapshot.ConfigStatus.BaseUrl);
                SetCheckRow(
                    RouteCheckIconText,
                    RouteCheckStatusText,
                    RouteCheckDescriptionText,
                    CheckVisual.Ready,
                    T("第三方服务", "Third-party service"),
                    F(
                        "{0}；模型：{1}。历史分区保持 OpenAI。",
                        "{0}; model: {1}. The history partition remains OpenAI.",
                        endpoint,
                        snapshot.ConfigStatus.Model ?? T("未指定", "Not specified")));
                break;

            default:
                SetCheckRow(
                    RouteCheckIconText,
                    RouteCheckStatusText,
                    RouteCheckDescriptionText,
                    CheckVisual.Informational,
                    T("尚未识别", "Not recognized yet"),
                    T(
                        "继续设置不会删除官方登录或聊天记录。",
                        "Continuing setup will not delete official sign-in or chat history."));
                break;
        }
    }

    private void RenderCredentialCheck(EnvironmentCheckSnapshot snapshot)
    {
        if (snapshot.ConfigStatus.Mode == ProviderMode.Official)
        {
            SetCheckRow(
                CredentialCheckIconText,
                CredentialCheckStatusText,
                CredentialCheckDescriptionText,
                CheckVisual.Informational,
                T("当前无需第三方凭据", "No third-party credential needed"),
                T(
                    "官方线路使用官方登录；本检查没有读取任何密钥。",
                    "The official route uses official sign-in; this check did not read any key."));
            return;
        }

        if (snapshot.ConfigStatus.Mode == ProviderMode.ThirdParty)
        {
            var configuredTargetValid =
                CredentialTargetFactory.IsValid(
                    snapshot.ConfigStatus.CredentialTarget);
            SetCheckRow(
                CredentialCheckIconText,
                CredentialCheckStatusText,
                CredentialCheckDescriptionText,
                configuredTargetValid
                    ? CheckVisual.Ready
                    : CheckVisual.Attention,
                configuredTargetValid
                    ? T("引用已配置", "Reference configured")
                    : T("引用需要修复", "Reference needs repair"),
                configuredTargetValid
                    ? T(
                        "已确认受管理的凭据名称；没有读取 API Key 内容。",
                        "The managed credential name was confirmed; API key contents were not read.")
                    : T(
                        "当前第三方线路没有有效的受管理凭据引用。",
                        "The current third-party route has no valid managed credential reference."));
            return;
        }

        SetCheckRow(
            CredentialCheckIconText,
            CredentialCheckStatusText,
            CredentialCheckDescriptionText,
            snapshot.ActiveProfileTargetValid
                ? CheckVisual.Informational
                : CheckVisual.Attention,
            snapshot.ActiveProfileTargetValid
                ? T("配置槽已准备", "Credential slot ready")
                : T("将在设置时创建", "Will be created during setup"),
            T(
                "这里只检查凭据名称格式，不会读取 API Key 内容。",
                "Only the credential name format is checked; API key contents are not read."));
    }

    private static void SetCheckRow(
        TextBlock icon,
        TextBlock status,
        TextBlock description,
        CheckVisual visual,
        string statusText,
        string descriptionText)
    {
        icon.Text = visual switch
        {
            CheckVisual.Ready => "\uE73E",
            CheckVisual.Attention => "\uE7BA",
            CheckVisual.Informational => "\uE946",
            _ => "\uE895"
        };
        var brushKey = visual switch
        {
            CheckVisual.Ready => "ThirdPartyStatusBrush",
            CheckVisual.Attention => "WarningStatusBrush",
            CheckVisual.Informational => "OfficialStatusBrush",
            _ => "MutedTextBrush"
        };
        icon.SetResourceReference(ForegroundProperty, brushKey);
        status.SetResourceReference(ForegroundProperty, brushKey);
        status.Text = statusText;
        description.Text = descriptionText;
    }

    private static string DisplayEndpoint(string? baseUrl)
    {
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }

        return string.IsNullOrWhiteSpace(baseUrl)
            ? T("未指定端点", "Endpoint not specified")
            : baseUrl;
    }

    private Task EnsureSuiXiangBrowserAsync()
    {
        if (_isClosed || _webViewInitialized)
        {
            return Task.CompletedTask;
        }

        return _webViewInitializationTask ??=
            InitializeSuiXiangBrowserAsync();
    }

    private async Task InitializeSuiXiangBrowserAsync()
    {
        _webViewInitializationInProgress = true;
        SetWebViewStatus(
            "正在准备登录窗口…",
            "Preparing the sign-in window...");
        UpdateWebViewControls();
        StartWebViewSoftTimeout(
            "登录窗口准备时间较长。你可以继续等待，或直接继续填写 API Key。",
            "The sign-in window is taking longer than expected. You can keep waiting or continue to the API key.");
        try
        {
            Directory.CreateDirectory(AppPaths.SuiXiangWebView2UserDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: AppPaths.SuiXiangWebView2UserDataFolder);
            if (_isClosed)
            {
                return;
            }

            await SuiXiangWebView.EnsureCoreWebView2Async(environment);
            if (_isClosed)
            {
                return;
            }

            ConfigureSuiXiangBrowser(SuiXiangWebView.CoreWebView2);
            _webViewInitialized = true;
            StopWebViewSoftTimeout();
            SuiXiangWebView.CoreWebView2.Navigate(SuiXiangHomeUrl);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            if (!_isClosed)
            {
                SetWebViewStatus(
                    "未检测到 Microsoft Edge WebView2 Runtime。安装或更新 Edge 后点击“刷新”重试。",
                    "Microsoft Edge WebView2 Runtime was not found. Install or update Edge, then select Refresh.");
            }
        }
        catch (Exception exception)
        {
            if (!_isClosed)
            {
                SetWebViewStatus(
                    $"无法打开随想登录窗口：{exception.Message}。请点击“刷新”重试。",
                    $"Could not open the SuiXiang sign-in window: {exception.Message}. Select Refresh to retry.");
            }
        }
        finally
        {
            StopWebViewSoftTimeout();
            _webViewInitializationInProgress = false;
            if (!_webViewInitialized)
            {
                _webViewInitializationTask = null;
            }

            if (!_isClosed)
            {
                UpdateWebViewControls();
            }
        }
    }

    private void ConfigureSuiXiangBrowser(CoreWebView2 browser)
    {
        if (ReferenceEquals(_configuredBrowser, browser))
        {
            return;
        }

        DetachSuiXiangBrowser();
        browser.Settings.AreDevToolsEnabled = false;
        browser.Settings.AreDefaultContextMenusEnabled = false;
        browser.Settings.IsStatusBarEnabled = false;
        browser.NavigationStarting += SuiXiangBrowser_NavigationStarting;
        browser.NavigationCompleted += SuiXiangBrowser_NavigationCompleted;
        browser.NewWindowRequested += SuiXiangBrowser_NewWindowRequested;
        browser.ProcessFailed += SuiXiangBrowser_ProcessFailed;
        _configuredBrowser = browser;
        _browserProcessUnavailable = false;
    }

    private void DetachSuiXiangBrowser()
    {
        if (_configuredBrowser is not { } browser)
        {
            return;
        }

        browser.NavigationStarting -= SuiXiangBrowser_NavigationStarting;
        browser.NavigationCompleted -= SuiXiangBrowser_NavigationCompleted;
        browser.NewWindowRequested -= SuiXiangBrowser_NewWindowRequested;
        browser.ProcessFailed -= SuiXiangBrowser_ProcessFailed;
        _configuredBrowser = null;
    }

    private void SuiXiangBrowser_NavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        var action = SuiXiangNavigationPolicy.Classify(
            e.Uri,
            e.IsUserInitiated);
        if (action == EmbeddedNavigationAction.AllowEmbedded)
        {
            _activeNavigationId = e.NavigationId;
            _webViewNavigationInProgress = true;
            _webViewNavigationTimedOut = false;
            SetWebViewStatus(
                "正在加载随想页面…",
                "Loading the SuiXiang page...");
            StartWebViewSoftTimeout(
                "页面加载时间较长。你可以点击“刷新”重试，或直接继续填写 API Key。",
                "The page is taking longer than expected. Select Refresh to retry, or continue to the API key.",
                allowRefreshOnTimeout: true);
            UpdateWebViewControls();
            return;
        }

        e.Cancel = true;
        if (action == EmbeddedNavigationAction.OpenExternal &&
            Uri.TryCreate(e.Uri, UriKind.Absolute, out var externalUri))
        {
            TryOpenInSystemBrowser(externalUri);
            return;
        }

        SetWizardStatus(
            "已阻止未经点击触发或不安全的外部链接。",
            "An unsafe or non-user-initiated external link was blocked.");
    }

    private void SuiXiangBrowser_NavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_isClosed || _activeNavigationId != e.NavigationId)
        {
            return;
        }

        _activeNavigationId = null;
        _webViewNavigationInProgress = false;
        _webViewNavigationTimedOut = false;
        StopWebViewSoftTimeout();
        if (e.IsSuccess && e.HttpStatusCode < 400)
        {
            WebViewStatusText.Visibility = Visibility.Collapsed;
            SetWizardStatus(
                "随想页面已加载。本程序不会判断登录是否成功；完成或跳过后可以继续填写 API Key。",
                "The SuiXiang page loaded. The app does not determine whether sign-in succeeded; finish or skip it, then continue to the API key.");
        }
        else
        {
            var detail = e.HttpStatusCode >= 400
                ? $"HTTP {e.HttpStatusCode}"
                : e.WebErrorStatus.ToString();
            SetWebViewStatus(
                $"页面加载失败（{detail}）。请点击“刷新”重试，或直接继续填写 API Key。",
                $"The page could not load ({detail}). Select Refresh to retry, or continue to the API key.");
        }

        UpdateWebViewControls();
    }

    private void SuiXiangBrowser_NewWindowRequested(
        object? sender,
        CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        var action = SuiXiangNavigationPolicy.Classify(
            e.Uri,
            e.IsUserInitiated);
        if (action == EmbeddedNavigationAction.AllowEmbedded &&
            _configuredBrowser is { } browser)
        {
            // Foundation fallback: keep allowed provider/CAPTCHA popups in the
            // same embedded view. No popup content or login state is inspected.
            browser.Navigate(e.Uri);
            return;
        }

        if (action == EmbeddedNavigationAction.OpenExternal &&
            Uri.TryCreate(e.Uri, UriKind.Absolute, out var externalUri))
        {
            TryOpenInSystemBrowser(externalUri);
            return;
        }

        SetWizardStatus(
            "已阻止未经点击触发或不安全的外部链接。",
            "An unsafe or non-user-initiated external link was blocked.");
    }

    private void SuiXiangBrowser_ProcessFailed(
        object? sender,
        CoreWebView2ProcessFailedEventArgs e)
    {
        if (_isClosed)
        {
            return;
        }

        _activeNavigationId = null;
        _webViewNavigationInProgress = false;
        StopWebViewSoftTimeout();
        if (e.ProcessFailedKind ==
            CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            _browserProcessUnavailable = true;
            _webViewInitialized = false;
            _webViewInitializationTask = null;
            DetachSuiXiangBrowser();
            SetWebViewStatus(
                "登录窗口进程已停止。请关闭并重新打开设置；你也可以直接继续填写 API Key。",
                "The sign-in window process stopped. Close and reopen setup, or continue to the API key.");
        }
        else
        {
            _webViewNavigationTimedOut = true;
            SetWebViewStatus(
                $"登录页面进程异常（{e.ProcessFailedKind}）。请点击“刷新”重试。",
                $"The sign-in page process failed ({e.ProcessFailedKind}). Select Refresh to retry.");
        }

        UpdateWebViewControls();
    }

    private bool TryOpenInSystemBrowser(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true
            });
            SetWizardStatus(
                "已在系统浏览器中打开外部页面。完成后可以回到这里继续。",
                "The external page opened in your system browser. Return here when it is complete.");
            return true;
        }
        catch (Exception exception)
        {
            SetWizardStatus(
                $"无法打开系统浏览器：{exception.Message}",
                $"Could not open the system browser: {exception.Message}");
            return false;
        }
    }

    private void StartWebViewSoftTimeout(
        string chinese,
        string english,
        bool allowRefreshOnTimeout = false)
    {
        StopWebViewSoftTimeout();
        if (_isClosed)
        {
            return;
        }

        var revision = ++_webViewStatusRevision;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _windowLifetime.Token);
        _webViewStatusTimeout = cancellation;
        _ = ShowWebViewSoftTimeoutAsync(
            revision,
            chinese,
            english,
            allowRefreshOnTimeout,
            cancellation.Token);
    }

    private async Task ShowWebViewSoftTimeoutAsync(
        int revision,
        string chinese,
        string english,
        bool allowRefreshOnTimeout,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(WebViewSoftTimeout, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_isClosed || revision != _webViewStatusRevision)
        {
            return;
        }

        _webViewNavigationTimedOut = allowRefreshOnTimeout;
        SetWebViewStatus(chinese, english);
        UpdateWebViewControls();
    }

    private void StopWebViewSoftTimeout()
    {
        _webViewStatusRevision++;
        var cancellation = _webViewStatusTimeout;
        _webViewStatusTimeout = null;
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        cancellation.Dispose();
    }

    private void UpdateWebViewControls()
    {
        var browserReady =
            _configuredBrowser is not null &&
            !_browserProcessUnavailable;
        SuiXiangRefreshButton.IsEnabled =
            !_isClosed &&
            !_browserProcessUnavailable &&
            !_webViewInitializationInProgress &&
            !_clearWebViewDataInProgress &&
            (!browserReady ||
             !_webViewNavigationInProgress ||
             _webViewNavigationTimedOut);
        ClearSuiXiangLoginButton.IsEnabled =
            !_isClosed &&
            browserReady &&
            !_webViewInitializationInProgress &&
            !_webViewNavigationInProgress &&
            !_clearWebViewDataInProgress;
        SuiXiangContinueButton.IsEnabled =
            !_isClosed &&
            !_clearWebViewDataInProgress;
    }

    private void ApplyLanguage()
    {
        HeaderTitleText.Text = T("开始设置", "Set up Codex Switcher");
        HeaderSubtitleText.Text = T("以后可以随时重新设置", "You can run setup again at any time");
        WelcomeTitleText.Text = T("先把线路设置好", "Set up your route");
        WelcomeDescriptionText.Text = T(
            "这个设置不会删除官方登录，也不会移动你的聊天记录。",
            "This setup does not delete your official sign-in or move your chat history.");
        WelcomeContinueButton.Content = T("继续", "Continue");

        EnvironmentCheckTitleText.Text = T(
            "检查运行环境",
            "Check the environment");
        EnvironmentCheckDescriptionText.Text = T(
            "这里只检查是否准备好，不会读取 API Key，也不会修改当前线路。",
            "This only checks readiness. It does not read API keys or change the current route.");
        EnvironmentCheckBackButton.Content = T("返回", "Back");
        EnvironmentRecheckButton.Content = T("重新检查", "Check again");
        EnvironmentContinueButton.Content = T("继续", "Continue");
        RenderEnvironmentCheck();

        ChoiceTitleText.Text = T("选择你现在想做的事", "Choose what you want to do");
        ChoiceDescriptionText.Text = T(
            "随想登录是可选的。你也可以直接填写任何兼容 OpenAI 的服务。",
            "SuiXiang sign-in is optional. You can also enter any OpenAI-compatible service directly.");
        SuiXiangChoiceButton.Content = T("登录随想", "Sign in with SuiXiang");
        SuiXiangChoiceDescriptionText.Text = T(
            "在应用内完成登录和验证码，然后手动粘贴新的 API Key。",
            "Complete sign-in and CAPTCHA in the app, then paste a new API key manually.");
        CustomChoiceButton.Content = T("使用其他服务", "Use another service");
        CustomChoiceDescriptionText.Text = T(
            "填写 Base URL、模型和新的 API Key。",
            "Enter a Base URL, model, and new API key.");
        OfficialChoiceButton.Content = T("暂时只用官方 Codex", "Use official Codex for now");

        SuiXiangLoginTitleText.Text = T("登录随想", "Sign in with SuiXiang");
        SuiXiangLoginDescriptionText.Text = T(
            "请自行完成登录和腾讯验证码。本程序不会判断登录是否成功，也不会读取密码、验证码或 Cookie。",
            "Complete sign-in and Tencent CAPTCHA yourself. The app does not determine whether sign-in succeeded and never reads passwords, CAPTCHA data, or cookies.");
        SuiXiangBackButton.Content = T("返回", "Back");
        SuiXiangRefreshButton.Content = T("刷新", "Refresh");
        ClearSuiXiangLoginButton.Content = T("清除登录数据", "Clear sign-in data");
        SuiXiangContinueButton.Content = T(
            "完成或跳过，继续填写 API Key",
            "Done or skip, continue to API key");

        ProviderDetailsTitleText.Text = _isSuiXiangChoice
            ? T("连接随想", "Connect SuiXiang")
            : T("连接服务", "Connect a service");
        ProviderDetailsDescriptionText.Text = T(
            "连接前会先做一次 Responses API 测试，失败时不会切换当前线路。",
            "The Responses API is tested before connecting. A failed test does not switch the current route.");
        ProviderNameLabelText.Text = T("显示名称", "Display name");
        WizardModelLabelText.Text = T("模型", "Model");
        WizardApiKeyLabelText.Text = T("新的 API Key", "New API key");
        WizardApiKeyHintText.Text = _isSuiXiangChoice
            ? T(
                "完成随想登录后，在随想页面创建 API Key 并粘贴到这里。密钥只会保存到 Windows 凭据管理器。",
                "After signing in, create an API key in SuiXiang and paste it here. It is saved only in Windows Credential Manager.")
            : T(
                "密钥只会保存到 Windows 凭据管理器。",
                "The key is saved only in Windows Credential Manager.");
        ProviderDetailsBackButton.Content = T("返回", "Back");
        ConnectProviderButton.Content = T("连接并切换", "Connect and switch");

        CompletionTitleText.Text = T(
            "设置已准备好",
            "Setup is ready");
        CompletionDescriptionText.Text = T(
            "请最后确认一次。只有点击“应用设置”后，程序才会开始验证和切换。",
            "Review the selection. Validation and switching begin only after you click Apply settings.");
        CompletionRouteLabelText.Text = T("准备使用", "Ready to use");
        CompletionSafetyText.Text = T(
            "官方登录和聊天记录不会被删除；API Key 不会显示在此页。",
            "Official sign-in and chat history are not deleted; the API key is not shown on this page.");
        CompletionBackButton.Content = T("返回", "Back");
        CompletionFinishButton.Content = T("应用设置", "Apply settings");
        RenderCompletionSummary();

        CancelWizardButton.Content = T("稍后再说", "Not now");
        if (_statusChinese is null || _statusEnglish is null)
        {
            _statusChinese = "聊天记录和官方登录不会被删除。";
            _statusEnglish = "Chat history and official sign-in are never deleted.";
        }

        RenderWizardStatus();
        RenderWebViewStatus();
        ChineseLanguageButton.FontWeight = Localizer.Current == AppLanguage.Chinese
            ? FontWeights.SemiBold
            : FontWeights.Normal;
        EnglishLanguageButton.FontWeight = Localizer.Current == AppLanguage.English
            ? FontWeights.SemiBold
            : FontWeights.Normal;
    }

    private void RenderCompletionSummary()
    {
        if (_pendingResult is null)
        {
            CompletionRouteValueText.Text = T(
                "尚未选择线路",
                "No route selected");
            CompletionDetailsText.Text = T(
                "返回上一步选择线路。",
                "Go back and choose a route.");
            return;
        }

        if (_pendingResult.UseOfficial)
        {
            CompletionRouteValueText.Text = T(
                "官方 Codex",
                "Official Codex");
            CompletionDetailsText.Text = F(
                "模型：{0}。将继续使用官方登录。",
                "Model: {0}. Official sign-in will continue to be used.",
                _settings.OfficialModel);
            return;
        }

        var displayName = string.IsNullOrWhiteSpace(_pendingResult.DisplayName)
            ? _pendingResult.ProviderKind == ProviderKinds.SuiXiang
                ? T("随想", "SuiXiang")
                : T("其他服务", "Other service")
            : _pendingResult.DisplayName;
        CompletionRouteValueText.Text = displayName;
        CompletionDetailsText.Text = F(
            "Base URL：{0}\n模型：{1}\n新的 API Key 将在连接测试通过后保存到 Windows 凭据管理器。",
            "Base URL: {0}\nModel: {1}\nThe new API key is saved to Windows Credential Manager after the connection test passes.",
            _pendingResult.BaseUrl,
            _pendingResult.Model);
    }

    private void SetWizardStatus(string chinese, string english)
    {
        _statusChinese = chinese;
        _statusEnglish = english;
        RenderWizardStatus();
    }

    private void RenderWizardStatus()
    {
        WizardStatusText.Text = Localizer.Current == AppLanguage.Chinese
            ? _statusChinese ?? string.Empty
            : _statusEnglish ?? string.Empty;
    }

    private void SetWebViewStatus(string chinese, string english)
    {
        _webViewStatusChinese = chinese;
        _webViewStatusEnglish = english;
        WebViewStatusText.Visibility = Visibility.Visible;
        RenderWebViewStatus();
    }

    private void RenderWebViewStatus()
    {
        if (_webViewStatusChinese is null || _webViewStatusEnglish is null)
        {
            return;
        }

        WebViewStatusText.Text = Localizer.Current == AppLanguage.Chinese
            ? _webViewStatusChinese
            : _webViewStatusEnglish;
    }

    private static string T(string chinese, string english) =>
        Localizer.Text(chinese, english);

    private static string F(
        string chineseFormat,
        string englishFormat,
        params object?[] arguments) =>
        Localizer.Format(chineseFormat, englishFormat, arguments);
}
