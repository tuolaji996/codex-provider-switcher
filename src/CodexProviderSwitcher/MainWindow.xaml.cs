using System.Diagnostics;
using System.IO;
using System.Windows;
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
            BaseUrlTextBox.Text = _settings.ThirdPartyBaseUrl;
            ModelTextBox.Text = _settings.ThirdPartyModel;
            RestartCheckBox.IsChecked = _settings.RestartAfterSwitch;
            _isInitialized = true;
            await RefreshStatusAsync();
            MainScrollViewer.ScrollToTop();
            Keyboard.ClearFocus();
            Focus();
        }
        catch (Exception exception)
        {
            ShowFailure("初始化失败", exception);
        }
    }

    private async Task RefreshStatusAsync()
    {
        var status = _configService.ReadStatus();
        UpdateModeBadge(status);

        CurrentRouteText.Text = status.Mode switch
        {
            ProviderMode.Official =>
                $"官方线路 · ChatGPT 登录 · 模型 {status.Model ?? "未指定"}",
            ProviderMode.ThirdParty =>
                $"第三方线路 · {status.BaseUrl} · 模型 {status.Model ?? "未指定"}",
            _ =>
                $"检测到未受管理的配置（model_provider = {status.ProviderId}）"
        };

        KeyStatusText.Text = CredentialVault.Exists(AppPaths.CredentialTarget)
            ? "密钥状态：已安全保存"
            : "密钥状态：尚未保存（请使用撤销旧密钥后生成的新密钥）";

        HistoryStatusText.Text = "正在核对聊天历史文件…";
        var health = await _sessionHealthService.InspectAsync();
        HistoryStatusText.Text =
            $"聊天历史：{health.TotalFiles} 个会话文件；" +
            $"{health.StableProviderFiles} 个在固定分区，" +
            $"{health.OtherProviderFiles} 个在其他分区，" +
            $"{health.UnreadableFiles} 个不可读，" +
            $"{health.EmptyPlaceholderFiles} 个是 0 字节旧占位文件。";
    }

    private async void SaveKeyButton_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            var key = ApiKeyPasswordBox.Password.Trim();
            if (key.Length < 16)
            {
                throw new InvalidOperationException("请输入新生成的完整 API Key。");
            }

            CredentialVault.Write(AppPaths.CredentialTarget, key);
            ApiKeyPasswordBox.Clear();
            OperationStatusText.Text = "新密钥已保存到 Windows 凭据管理器。";
            await RefreshStatusAsync();
        });
    }

    private async void DeleteKeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                this,
                "确认从 Windows 凭据管理器删除第三方密钥？",
                "删除密钥",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            CredentialVault.Delete(AppPaths.CredentialTarget);
            ApiKeyPasswordBox.Clear();
            OperationStatusText.Text = "第三方密钥已删除。";
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
                result.Success ? "兼容性测试通过" : "兼容性测试未通过",
                MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        });
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
                OperationStatusText.Text = "正在验证第三方 Responses API…";
                var result = await TestConnectionAsync(key);
                if (!result.Success)
                {
                    var answer = MessageBox.Show(
                        this,
                        $"{result.Summary}\n\n仍然写入第三方配置吗？",
                        "第三方兼容性未确认",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                    if (answer != MessageBoxResult.Yes)
                    {
                        OperationStatusText.Text = "已取消切换；当前 Codex 配置未改动。";
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
                throw new InvalidOperationException("配置写入后的自检失败。备份位于：" + backupFolder);
            }

            OperationStatusText.Text =
                $"已切换到第三方；历史分区保持 {AppPaths.StableProviderId}。备份：{backupFolder}";
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
                throw new InvalidOperationException("配置写入后的自检失败。备份位于：" + backupFolder);
            }

            OperationStatusText.Text =
                $"已切换到官方 OpenAI；官方登录凭据保持不变。备份：{backupFolder}";
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
            throw new InvalidOperationException("第三方模型不能为空。");
        }

        _settings.RestartAfterSwitch = RestartCheckBox.IsChecked == true;
        _settingsStore.Save(_settings);
    }

    private string ResolveAndOptionallySaveKey()
    {
        var entered = ApiKeyPasswordBox.Password.Trim();
        if (!string.IsNullOrWhiteSpace(entered))
        {
            if (entered.Length < 16)
            {
                throw new InvalidOperationException("请输入新生成的完整 API Key。");
            }

            CredentialVault.Write(AppPaths.CredentialTarget, entered);
            ApiKeyPasswordBox.Clear();
            return entered;
        }

        return CredentialVault.Read(AppPaths.CredentialTarget)
            ?? throw new InvalidOperationException(
                "尚未保存第三方密钥。请先撤销已暴露的旧密钥，再粘贴新密钥。");
    }

    private Task<ConnectionTestResult> TestConnectionAsync(string key) =>
        _connectionTestService.TestResponsesApiAsync(
            _settings.ThirdPartyBaseUrl,
            _settings.ThirdPartyModel,
            key);

    private async Task RestartIfRequestedAsync()
    {
        if (!_settings.RestartAfterSwitch)
        {
            OperationStatusText.Text += " 请手动重启 Codex 后生效。";
            return;
        }

        OperationStatusText.Text += " 正在重启 Codex…";
        await _processService.RestartAsync();
        OperationStatusText.Text += " Codex 已重新启动。";
    }

    private void UpdateModeBadge(ConfigStatus status)
    {
        switch (status.Mode)
        {
            case ProviderMode.Official:
                ModeBadge.Background = new SolidColorBrush(Color.FromRgb(31, 68, 120));
                ModeBadgeText.Text = "官方 OpenAI";
                break;
            case ProviderMode.ThirdParty:
                ModeBadge.Background = new SolidColorBrush(Color.FromRgb(24, 74, 55));
                ModeBadgeText.Text = "第三方线路";
                break;
            default:
                ModeBadge.Background = new SolidColorBrush(Color.FromRgb(89, 62, 24));
                ModeBadgeText.Text = "需要初始化";
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
            ShowFailure("操作失败", exception);
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
        SwitchThirdPartyButton.IsEnabled = !busy;
        SwitchOfficialButton.IsEnabled = !busy;
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
}
