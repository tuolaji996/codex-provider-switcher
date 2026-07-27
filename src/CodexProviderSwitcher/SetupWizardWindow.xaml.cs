using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using CodexProviderSwitcher.Core;

namespace CodexProviderSwitcher;

public partial class SetupWizardWindow : Window
{
    private enum WizardPage
    {
        Welcome,
        Choice,
        SuiXiangLogin,
        ProviderDetails
    }

    private readonly SwitcherSettings _settings;
    private SetupWizardResult? _draft;
    private WizardPage _currentPage;
    private bool _isSuiXiangChoice;
    private bool _webViewInitialized;

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
        SuiXiangWebView.Dispose();
    }

    private void WelcomeContinueButton_Click(object sender, RoutedEventArgs e) =>
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
        Result = new SetupWizardResult(
            true,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            null);
        DialogResult = true;
    }

    private void SuiXiangBackButton_Click(object sender, RoutedEventArgs e) =>
        ShowPage(WizardPage.Choice);

    private void SuiXiangContinueButton_Click(object sender, RoutedEventArgs e)
    {
        PrepareProviderDetails();
        ShowPage(WizardPage.ProviderDetails);
    }

    private void SuiXiangRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (SuiXiangWebView.CoreWebView2 is not null)
        {
            SuiXiangWebView.CoreWebView2.Reload();
        }
    }

    private async void ClearSuiXiangLoginButton_Click(object sender, RoutedEventArgs e)
    {
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
            if (SuiXiangWebView.CoreWebView2 is not { } browser)
            {
                return;
            }

            await browser.Profile.ClearBrowsingDataAsync();
            browser.Navigate("https://sui-xiang.com/");
            WizardStatusText.Text = T(
                "已清除本应用内的随想登录数据。",
                "SuiXiang sign-in data stored by this app was cleared.");
        }
        catch (Exception exception)
        {
            WizardStatusText.Text = F(
                "无法清除随想登录数据：{0}",
                "Could not clear SuiXiang sign-in data: {0}",
                exception.Message);
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
            WizardStatusText.Text = T(
                "请填写 Base URL、模型和完整的新 API Key。",
                "Enter a Base URL, model, and complete new API key.");
            return;
        }

        Result = new SetupWizardResult(
            false,
            _isSuiXiangChoice ? ProviderKinds.SuiXiang : ProviderKinds.Custom,
            ProviderNameTextBox.Text.Trim(),
            baseUrl,
            model,
            apiKey);
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
        ApplyLanguage();
    }

    private void PrepareProviderDetails()
    {
        var profile = _settings.ActiveProviderProfile;
        var draft = _draft;
        if (_isSuiXiangChoice)
        {
            ProviderNameTextBox.Text = T("随想", "SuiXiang");
            WizardBaseUrlTextBox.Text = AppPaths.DefaultBaseUrl;
            WizardModelTextBox.Text = AppPaths.DefaultThirdPartyModel;
        }
        else
        {
            ProviderNameTextBox.Text = draft?.DisplayName ?? profile?.DisplayName ?? string.Empty;
            WizardBaseUrlTextBox.Text = draft?.BaseUrl ?? profile?.BaseUrl ?? string.Empty;
            WizardModelTextBox.Text = draft?.Model ?? profile?.Model ?? string.Empty;
        }

        if (draft?.ApiKey is { Length: > 0 })
        {
            WizardApiKeyPasswordBox.Password = draft.ApiKey;
        }

        _draft = null;
    }

    private async Task EnsureSuiXiangBrowserAsync()
    {
        if (_webViewInitialized)
        {
            return;
        }

        WebViewStatusText.Visibility = Visibility.Visible;
        WebViewStatusText.Text = T(
            "正在准备登录窗口…",
            "Preparing the sign-in window...");
        try
        {
            Directory.CreateDirectory(AppPaths.SuiXiangWebView2UserDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: AppPaths.SuiXiangWebView2UserDataFolder);
            await SuiXiangWebView.EnsureCoreWebView2Async(environment);
            ConfigureSuiXiangBrowser(SuiXiangWebView.CoreWebView2);
            _webViewInitialized = true;
            SuiXiangWebView.CoreWebView2.Navigate("https://sui-xiang.com/");
            WebViewStatusText.Visibility = Visibility.Collapsed;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            WebViewStatusText.Text = T(
                "未检测到 Microsoft Edge WebView2 Runtime。安装或更新 Edge 后重试。",
                "Microsoft Edge WebView2 Runtime was not found. Install or update Edge, then retry.");
        }
        catch (Exception exception)
        {
            WebViewStatusText.Text = F(
                "无法打开随想登录窗口：{0}",
                "Could not open the SuiXiang sign-in window: {0}",
                exception.Message);
        }
    }

    private void ConfigureSuiXiangBrowser(CoreWebView2 browser)
    {
        browser.Settings.AreDevToolsEnabled = false;
        browser.Settings.AreDefaultContextMenusEnabled = false;
        browser.Settings.IsStatusBarEnabled = false;
        browser.NavigationStarting += SuiXiangBrowser_NavigationStarting;
        browser.NewWindowRequested += SuiXiangBrowser_NewWindowRequested;
    }

    private void SuiXiangBrowser_NavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) ||
            IsAllowedSuiXiangNavigation(uri))
        {
            return;
        }

        e.Cancel = true;
        if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            OpenInSystemBrowser(uri);
            WizardStatusText.Text = T(
                "已在系统浏览器中打开外部登录页面。完成后可以回到这里继续。",
                "The external sign-in page opened in your system browser. Return here when it is complete.");
        }
        else
        {
            WizardStatusText.Text = T(
                "已阻止不安全的外部登录链接。",
                "An unsafe external sign-in link was blocked.");
        }
    }

    private void SuiXiangBrowser_NewWindowRequested(
        object? sender,
        CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
        {
            if (IsAllowedSuiXiangNavigation(uri) &&
                SuiXiangWebView.CoreWebView2 is { } browser)
            {
                browser.Navigate(uri.AbsoluteUri);
            }
            else if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                OpenInSystemBrowser(uri);
            }
            else
            {
                WizardStatusText.Text = T(
                    "已阻止不安全的外部登录链接。",
                    "An unsafe external sign-in link was blocked.");
            }
        }
    }

    private static bool IsAllowedSuiXiangNavigation(Uri uri)
    {
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var host = uri.Host;
        return host.Equals("sui-xiang.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".sui-xiang.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".qq.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".qcloud.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".tencent-cloud.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".tencentcs.com", StringComparison.OrdinalIgnoreCase);
    }

    private static void OpenInSystemBrowser(Uri uri)
    {
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = uri.AbsoluteUri,
            UseShellExecute = true
        });
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
            "请自行完成登录和腾讯验证码。登录不会自动导入 API Key；这个页面由随想提供，本程序不会读取密码、验证码或 Cookie。",
            "Complete sign-in and Tencent CAPTCHA yourself. Sign-in does not automatically import an API key; this page is provided by SuiXiang and the app never reads passwords, CAPTCHA data, or cookies.");
        SuiXiangBackButton.Content = T("返回", "Back");
        SuiXiangRefreshButton.Content = T("刷新", "Refresh");
        ClearSuiXiangLoginButton.Content = T("清除登录数据", "Clear sign-in data");
        SuiXiangContinueButton.Content = T("继续填写 API Key", "Continue to API key");

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
        CancelWizardButton.Content = T("稍后再说", "Not now");
        WizardStatusText.Text = T(
            "聊天记录和官方登录不会被删除。",
            "Chat history and official sign-in are never deleted.");
        ChineseLanguageButton.FontWeight = Localizer.Current == AppLanguage.Chinese
            ? FontWeights.SemiBold
            : FontWeights.Normal;
        EnglishLanguageButton.FontWeight = Localizer.Current == AppLanguage.English
            ? FontWeights.SemiBold
            : FontWeights.Normal;
    }

    private static string T(string chinese, string english) =>
        Localizer.Text(chinese, english);

    private static string F(
        string chineseFormat,
        string englishFormat,
        params object?[] arguments) =>
        Localizer.Format(chineseFormat, englishFormat, arguments);
}
