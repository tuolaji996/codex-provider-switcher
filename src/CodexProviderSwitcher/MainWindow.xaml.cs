using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CodexProviderSwitcher.Core;

namespace CodexProviderSwitcher;

public partial class MainWindow : Window
{
    private readonly ConfigService _configService = new();
    private readonly SettingsStore _settingsStore = new();
    private readonly SessionHealthService _sessionHealthService = new();
    private readonly ConnectionTestService _connectionTestService = new();
    private readonly HostCapabilityDiagnosticsService _hostDiagnosticsService = new();
    private readonly CodexProcessService _processService = new();
    private SwitcherSettings _settings = new();
    private bool _isBusy;
    private bool _isInitialized;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    private string TokenBrokerPath =>
        Path.Combine(AppContext.BaseDirectory, "CodexProviderToken.exe");

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var status = _configService.ReadStatus();
            _settings = _settingsStore.Load(status);
            Localizer.Use(_settings.UiLanguage);
            ApplyLanguage();
            BaseUrlTextBox.Text = _settings.ThirdPartyBaseUrl;
            ModelTextBox.Text = _settings.ThirdPartyModel;
            RestartCheckBox.IsChecked = _settings.RestartAfterSwitch;
            OpenGeneratedImageButton.IsEnabled = HasGeneratedImage();
            UpdatePersistedProviderCapabilityStatuses();
            await RefreshStatusAsync();
            await RefreshCapabilitiesAsync();
            _isInitialized = true;
            ChineseLanguageButton.IsEnabled = true;
            EnglishLanguageButton.IsEnabled = true;
            MainScrollViewer.ScrollToTop();
            Keyboard.ClearFocus();
            Focus();
        }
        catch (Exception exception)
        {
            ShowFailure(T("初始化失败", "Initialization failed"), exception);
        }
    }

    private async Task RefreshStatusAsync()
    {
        var status = _configService.ReadStatus();
        UpdateModeBadge(status);

        CurrentRouteText.Text = status.Mode switch
        {
            ProviderMode.Official =>
                F(
                    "官方线路 · ChatGPT 登录 · 模型 {0}",
                    "Official route · ChatGPT sign-in · Model {0}",
                    status.Model ?? T("未指定", "Not specified")),
            ProviderMode.ThirdParty =>
                F(
                    "第三方线路 · {0} · 模型 {1}",
                    "Third-party route · {0} · Model {1}",
                    status.BaseUrl,
                    status.Model ?? T("未指定", "Not specified")),
            _ =>
                F(
                    "检测到未受管理的配置（model_provider = {0}）",
                    "Unmanaged configuration detected (model_provider = {0})",
                    status.ProviderId)
        };

        KeyStatusText.Text = CredentialVault.Exists(AppPaths.CredentialTarget)
            ? T("密钥状态：已安全保存", "Key status: securely saved")
            : T(
                "密钥状态：尚未保存（请使用撤销旧密钥后生成的新密钥）",
                "Key status: not saved (revoke the exposed key and use a newly generated key)");

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
            UpdatePersistedProviderCapabilityStatuses();
            await RefreshStatusAsync();
            await RefreshCapabilitiesAsync();
            OperationStatusText.Text = T(
                "界面语言已切换为中文。",
                "Interface language switched to English.");
        });
    }

    private async void SaveKeyButton_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            var key = ApiKeyPasswordBox.Password.Trim();
            if (key.Length < 16)
            {
                throw new InvalidOperationException(T(
                    "请输入新生成的完整 API Key。",
                    "Enter the complete newly generated API key."));
            }

            CredentialVault.Write(AppPaths.CredentialTarget, key);
            ApiKeyPasswordBox.Clear();
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
            CredentialVault.Delete(AppPaths.CredentialTarget);
            ApiKeyPasswordBox.Clear();
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
            SaveNonSecretSettings();
            var key = ResolveAndOptionallySaveKey();
            var result = await TestConnectionAsync(key);
            OperationStatusText.Text = result.Summary;

            if (result.Success)
            {
                _settings.LastSuccessfulCompatibilityTestUtc = DateTimeOffset.UtcNow;
                _settings.LastTestedEndpointFingerprint =
                    ConnectionTestService.EndpointFingerprint(
                        _settings.ThirdPartyBaseUrl,
                        _settings.ThirdPartyModel);
                _settingsStore.Save(_settings);
            }

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

    private async void RefreshCapabilitiesButton_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(RefreshCapabilitiesAsync);
    }

    private async void TestToolCallingButton_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            SaveNonSecretSettings();
            var key = ResolveAndOptionallySaveKey();
            ToolCapabilityStatusText.Text =
                T(
                    "第三方插件工具调用：正在测试 function_call 与工具结果回传…",
                    "Third-party plugin tools: testing function_call and tool-result replay...");

            ConnectionTestResult result;
            try
            {
                result = await _connectionTestService.TestFunctionCallingAsync(
                    _settings.ThirdPartyBaseUrl,
                    _settings.ThirdPartyModel,
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
                    CurrentToolEndpointFingerprint();
            }
            else
            {
                ClearCurrentToolTestResult();
            }
            _settingsStore.Save(_settings);

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
            SaveNonSecretSettings();
            var key = ResolveAndOptionallySaveKey();
            ImageCapabilityStatusText.Text =
                T(
                    "第三方图片生成：正在实测 Codex 当前使用的 Images API…",
                    "Third-party image generation: testing the Images API currently used by Codex...");

            ImageGenerationTestResult result;
            try
            {
                result = await _connectionTestService.TestImageGenerationAsync(
                    _settings.ThirdPartyBaseUrl,
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
                    CurrentImageEndpointFingerprint();
                _settings.LastGeneratedImagePath = result.ArtifactPath;
            }
            else
            {
                ClearCurrentImageTestResult();
            }
            _settingsStore.Save(_settings);

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
        if (!HostCapabilityDiagnosticsService.OpenOfficialConnectionsSettings())
        {
            MessageBox.Show(
                this,
                T(
                    "无法打开 Codex Connections。请先确认官方 Codex Windows 应用已安装。",
                    "Could not open Codex Connections. Confirm that the official Codex Windows app is installed."),
                T("无法打开 Remote 设置", "Could not open Remote settings"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        OperationStatusText.Text =
            T(
                "已打开官方 Connections 页面；请使用同一 ChatGPT 账号在手机端完成配对。",
                "Opened the official Connections page. Complete pairing on your phone with the same ChatGPT account.");
    }

    private async void SwitchThirdPartyButton_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            SaveNonSecretSettings();
            var key = ResolveAndOptionallySaveKey();

            var fingerprint = ConnectionTestService.EndpointFingerprint(
                _settings.ThirdPartyBaseUrl,
                _settings.ThirdPartyModel);
            var recentlyTested =
                _settings.LastTestedEndpointFingerprint == fingerprint &&
                _settings.LastSuccessfulCompatibilityTestUtc is not null &&
                DateTimeOffset.UtcNow - _settings.LastSuccessfulCompatibilityTestUtc <
                TimeSpan.FromDays(7);

            if (!recentlyTested)
            {
                OperationStatusText.Text = T(
                    "正在验证第三方 Responses API…",
                    "Validating the third-party Responses API...");
                var result = await TestConnectionAsync(key);
                if (!result.Success)
                {
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
                        OperationStatusText.Text = T(
                            "已取消切换；当前 Codex 配置未改动。",
                            "Switch cancelled; the current Codex configuration was not changed.");
                        return;
                    }
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

            var backupFolder = _configService.CreateBackup();
            var original = File.ReadAllText(AppPaths.ConfigPath);
            var updated = _configService.BuildThirdPartyConfig(
                original,
                _settings.ThirdPartyModel,
                _settings.ThirdPartyBaseUrl,
                TokenBrokerPath);
            _configService.WriteConfig(updated);

            var verification = _configService.ReadStatus();
            if (verification.Mode != ProviderMode.ThirdParty ||
                verification.ProviderId != AppPaths.StableProviderId)
            {
                throw new InvalidOperationException(F(
                    "配置写入后的自检失败。备份位于：{0}",
                    "Post-write verification failed. Backup: {0}",
                    backupFolder));
            }

            OperationStatusText.Text =
                F(
                    "已切换到第三方；历史分区保持 {0}。备份：{1}",
                    "Switched to the third-party route; the history partition remains {0}. Backup: {1}",
                    AppPaths.StableProviderId,
                    backupFolder);
            await RefreshStatusAsync();
            await RestartIfRequestedAsync();
        });
    }

    private async void SwitchOfficialButton_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            SaveNonSecretSettings();
            var backupFolder = _configService.CreateBackup();
            var original = File.ReadAllText(AppPaths.ConfigPath);
            var updated = _configService.BuildOfficialConfig(
                original,
                _settings.OfficialModel,
                _settings.OfficialReviewModel);
            _configService.WriteConfig(updated);

            var verification = _configService.ReadStatus();
            if (verification.Mode != ProviderMode.Official ||
                verification.ProviderId != AppPaths.StableProviderId)
            {
                throw new InvalidOperationException(F(
                    "配置写入后的自检失败。备份位于：{0}",
                    "Post-write verification failed. Backup: {0}",
                    backupFolder));
            }

            OperationStatusText.Text =
                F(
                    "已切换到官方 OpenAI；官方登录凭据保持不变。备份：{0}",
                    "Switched to official OpenAI; official sign-in credentials were preserved. Backup: {0}",
                    backupFolder);
            await RefreshStatusAsync();
            await RestartIfRequestedAsync();
        });
    }

    private void RestartCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized)
        {
            return;
        }

        _settings.RestartAfterSwitch = RestartCheckBox.IsChecked == true;
        _settingsStore.Save(_settings);
    }

    private void OpenBackupsButton_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.BackupsRoot);
        Process.Start(new ProcessStartInfo
        {
            FileName = AppPaths.BackupsRoot,
            UseShellExecute = true
        });
    }

    private void SaveNonSecretSettings()
    {
        _settings.ThirdPartyBaseUrl =
            ConfigService.NormalizeBaseUrl(BaseUrlTextBox.Text);
        _settings.ThirdPartyModel = ModelTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(_settings.ThirdPartyModel))
        {
            throw new InvalidOperationException(T(
                "第三方模型不能为空。",
                "The third-party model cannot be empty."));
        }

        _settings.RestartAfterSwitch = RestartCheckBox.IsChecked == true;
        _settingsStore.Save(_settings);
        UpdatePersistedProviderCapabilityStatuses();
    }

    private void ProviderSettingsTextBox_TextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        if (_isInitialized)
        {
            UpdatePersistedProviderCapabilityStatuses();
        }
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

            CredentialVault.Write(AppPaths.CredentialTarget, entered);
            ApiKeyPasswordBox.Clear();
            return entered;
        }

        return CredentialVault.Read(AppPaths.CredentialTarget)
            ?? throw new InvalidOperationException(
                T(
                    "尚未保存第三方密钥。请先撤销已暴露的旧密钥，再粘贴新密钥。",
                    "No third-party key is saved. Revoke the exposed old key, then paste a newly generated key."));
    }

    private Task<ConnectionTestResult> TestConnectionAsync(string key) =>
        _connectionTestService.TestResponsesApiAsync(
            _settings.ThirdPartyBaseUrl,
            _settings.ThirdPartyModel,
            key);

    private async Task RefreshCapabilitiesAsync()
    {
        HostCapabilityStatusText.Text = T(
            "官方登录与功能开关：正在检测…",
            "Official sign-in and feature flags: checking...");
        var diagnostics = await _hostDiagnosticsService.InspectAsync();
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
                    "手机 Remote：本机登录与功能开关已就绪；仍需在官方 Connections 页面用手机实机配对。",
                    "Mobile Remote: local sign-in and feature flags are ready; physical phone pairing is still required in the official Connections page."),
            _ =>
                T(
                    "手机 Remote：本机状态未完整识别；可打开官方 Connections 页面继续检查。",
                    "Mobile Remote: local state was not fully detected; open the official Connections page to continue checking.")
        };
    }

    private bool HasGeneratedImage() =>
        !string.IsNullOrWhiteSpace(_settings.LastGeneratedImagePath) &&
        File.Exists(_settings.LastGeneratedImagePath);

    private string CurrentToolEndpointFingerprint() =>
        ConnectionTestService.EndpointFingerprint(
            _settings.ThirdPartyBaseUrl,
            _settings.ThirdPartyModel);

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
            var displayedModel = ModelTextBox.Text.Trim();
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

    private async Task RestartIfRequestedAsync()
    {
        if (!_settings.RestartAfterSwitch)
        {
            OperationStatusText.Text += T(
                " 请手动重启 Codex 后生效。",
                " Restart Codex manually to apply the change.");
            return;
        }

        OperationStatusText.Text += T(
            " 正在重启 Codex…",
            " Restarting Codex...");
        await _processService.RestartAsync();
        OperationStatusText.Text += T(
            " Codex 已重新启动。",
            " Codex restarted.");
    }

    private void UpdateModeBadge(ConfigStatus status)
    {
        switch (status.Mode)
        {
            case ProviderMode.Official:
                ModeBadge.Background = new SolidColorBrush(Color.FromRgb(31, 68, 120));
                ModeBadgeText.Text = T("官方 OpenAI", "Official OpenAI");
                break;
            case ProviderMode.ThirdParty:
                ModeBadge.Background = new SolidColorBrush(Color.FromRgb(24, 74, 55));
                ModeBadgeText.Text = T("第三方线路", "Third-party");
                break;
            default:
                ModeBadge.Background = new SolidColorBrush(Color.FromRgb(89, 62, 24));
                ModeBadgeText.Text = T("需要初始化", "Setup required");
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
        SaveKeyButton.IsEnabled = !busy;
        DeleteKeyButton.IsEnabled = !busy;
        TestConnectionButton.IsEnabled = !busy;
        RefreshCapabilitiesButton.IsEnabled = !busy;
        TestToolCallingButton.IsEnabled = !busy;
        TestImageGenerationButton.IsEnabled = !busy;
        OpenGeneratedImageButton.IsEnabled = !busy && HasGeneratedImage();
        OpenRemoteSettingsButton.IsEnabled = !busy;
        SwitchThirdPartyButton.IsEnabled = !busy;
        SwitchOfficialButton.IsEnabled = !busy;
        ChineseLanguageButton.IsEnabled = !busy;
        EnglishLanguageButton.IsEnabled = !busy;
        BaseUrlTextBox.IsEnabled = !busy;
        ModelTextBox.IsEnabled = !busy;
        ApiKeyPasswordBox.IsEnabled = !busy;
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
        TaglineText.Text = T(
            "官方登录与第三方线路，共用同一份聊天历史",
            "Official sign-in and third-party routes, with one shared chat history");
        ModeBadgeText.Text = T("正在检测", "Checking");
        CurrentStatusTitleText.Text = T("当前状态", "Current status");
        StableHistoryText.Text = T(
            "固定历史分区：OpenAI（切换时永远不改）",
            "Stable history partition: OpenAI (never changed when switching)");
        OpenBackupsButton.Content = T("打开备份", "Open backups");

        ThirdPartyTitleText.Text = T("第三方线路", "Third-party route");
        KeyStorageDescriptionText.Text = T(
            "密钥保存在 Windows 凭据管理器；不会写进 config.toml、源码或日志。",
            "The key is stored in Windows Credential Manager and is never written to config.toml, source code, or logs.");
        ModelLabelText.Text = T("模型", "Model");
        ApiKeyLabelText.Text = T("新 API Key", "New API key");
        SaveKeyButton.Content = T("保存密钥", "Save key");
        DeleteKeyButton.Content = T("删除", "Delete");
        ThirdPartyPrivacyWarningText.Text = T(
            "注意：第三方服务会接收你发送给 Codex 的提示词、代码片段和工具上下文。",
            "Note: the third-party service receives prompts, code snippets, and tool context sent through Codex.");
        TestConnectionButton.Content = T(
            "测试 Responses 兼容性",
            "Test Responses compatibility");
        SwitchThirdPartyButton.Content = T("切换到第三方", "Switch to third-party");

        CapabilityTitleText.Text = T("能力诊断", "Capability diagnostics");
        CapabilityDescriptionText.Text = T(
            "官方账号能力保留在本机；第三方模型仍需通过 Responses 工具协议与图片接口实测。",
            "Official account capabilities remain local; third-party models still require real Responses tool-protocol and image API tests.");
        HostCapabilityStatusText.Text = T(
            "官方登录与功能开关：正在检测…",
            "Official sign-in and feature flags: checking...");
        RemoteCapabilityStatusText.Text = T(
            "手机 Remote：需使用相同 ChatGPT 账号在官方 Connections 页面扫码配对",
            "Mobile Remote: pair from the official Connections page with the same ChatGPT account");
        RefreshCapabilitiesButton.Content = T("刷新官方能力", "Refresh official capabilities");
        TestToolCallingButton.Content = T("测试插件工具调用", "Test plugin tool calling");
        TestImageGenerationButton.Content = T("生成测试图片", "Generate test image");
        OpenGeneratedImageButton.Content = T("打开测试图片", "Open test image");
        OpenRemoteSettingsButton.Content = T("打开 Remote 设置", "Open Remote settings");

        OfficialTitleText.Text = T("官方 OpenAI Codex", "Official OpenAI Codex");
        OfficialDescriptionText.Text = T(
            "使用现有 ChatGPT 登录与订阅额度；官方登录凭据不会被删除或覆盖。",
            "Uses your existing ChatGPT sign-in and subscription allowance; official credentials are never deleted or overwritten.");
        SwitchOfficialButton.Content = T("切换到官方", "Switch to official");

        RestartTitleText.Text = T(
            "切换后自动重启 Codex",
            "Restart Codex automatically after switching");
        RestartDescriptionText.Text = T(
            "重启会中断正在运行的 Codex 任务。配置写入前会自动生成备份。",
            "Restarting interrupts active Codex tasks. A backup is created before the configuration is written.");

        ChineseLanguageButton.ToolTip = T("切换到中文", "Switch to Chinese");
        EnglishLanguageButton.ToolTip = T("切换到英文", "Switch to English");
        UpdateLanguageButtons();
    }

    private void UpdateLanguageButtons()
    {
        var chineseSelected = Localizer.Current == AppLanguage.Chinese;
        ChineseLanguageButton.IsChecked = chineseSelected;
        EnglishLanguageButton.IsChecked = !chineseSelected;
    }

    private static string T(string chinese, string english) =>
        Localizer.Text(chinese, english);

    private static string F(
        string chineseFormat,
        string englishFormat,
        params object?[] arguments) =>
        Localizer.Format(chineseFormat, englishFormat, arguments);
}
