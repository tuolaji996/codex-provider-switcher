using System.Windows;
using System.Windows.Controls;
using CodexProviderSwitcher.Core;

namespace CodexProviderSwitcher;

public partial class ProviderProfileEditorWindow : Window
{
    private readonly string? _originalBaseUrl;

    public ProviderProfileEdit? Edit { get; private set; }

    public ProviderProfileEditorWindow(ProviderProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        InitializeComponent();

        // Copy non-secret values only; the caller owns all profile mutations.
        AccountNameTextBox.Text = profile.DisplayName ?? string.Empty;
        BaseUrlTextBox.Text = profile.BaseUrl ?? string.Empty;
        ModelTextBox.Text = profile.Model ?? string.Empty;
        try
        {
            _originalBaseUrl = ConfigService.NormalizeBaseUrl(BaseUrlTextBox.Text);
        }
        catch (ArgumentException)
        {
            // An invalid saved endpoint cannot safely reuse its saved key.
            _originalBaseUrl = null;
        }

        ApplyLanguage();
    }

    private void ApplyLanguage()
    {
        Title = HeaderTitleText.Text = T("编辑账号", "Edit account");
        SavedAccountHintText.Text = T(
            "保存仅更新该账号的资料。要更改当前线路，请在主窗口应用此账号。",
            "Saving only updates this saved account. Apply the account in the main window to change the active route.");
        AccountNameLabelText.Text = T(
            "账号名称（可选，最多 80 个字符）",
            "Account name (optional, up to 80 characters)");
        ModelLabelText.Text = T("模型", "Model");
        NewApiKeyLabelText.Text = T("新的 API Key（可选）", "New API key (optional)");
        ApiKeyHintText.Text = T(
            "留空会保留同一 Base URL 已保存的密钥；更改 Base URL 必须提供新的 API Key。新密钥至少需要 16 个字符。已保存的密钥不会被读取或显示。",
            "Leave blank to preserve the saved key for the same Base URL. Changing the Base URL requires a new API key of at least 16 characters. The saved key is never read or displayed.");
        SaveButton.Content = T("保存", "Save");
        CancelButton.Content = T("取消", "Cancel");
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var displayName = AccountNameTextBox.Text.Trim();
        if (displayName.Length > 80)
        {
            ShowValidationError(
                "账号名称不能超过 80 个字符。",
                "Account name must not exceed 80 characters.",
                AccountNameTextBox);
            return;
        }

        string baseUrl;
        try
        {
            baseUrl = ConfigService.NormalizeBaseUrl(BaseUrlTextBox.Text);
        }
        catch (ArgumentException)
        {
            ShowValidationError(
                "Base URL 必须是完整的 http:// 或 https:// 地址。",
                "Base URL must be a complete http:// or https:// address.",
                BaseUrlTextBox);
            return;
        }

        var model = ModelTextBox.Text.Trim();
        if (model.Length == 0 || model.Contains('\r') || model.Contains('\n'))
        {
            ShowValidationError(
                "请输入有效模型名称，不能包含换行符。",
                "Enter a valid model name without line breaks.",
                ModelTextBox);
            return;
        }

        try
        {
            ProviderAvailabilityPolicy.RequireAvailableThirdPartyRoute(baseUrl, model);
        }
        catch (InvalidOperationException)
        {
            ShowValidationError(
                "K3 线路已停用。请选择官方 Codex 或随想当前支持的 OpenAI 模型。",
                "The K3 route has been retired. Choose Official Codex or a currently supported SuiXiang OpenAI model.",
                ModelTextBox);
            return;
        }

        // Read only the newly entered key, never the saved credential.
        var newApiKey = NewApiKeyPasswordBox.Password.Trim();
        if (newApiKey.Length > 0 && newApiKey.Length < 16)
        {
            ShowValidationError(
                "新的 API Key 至少需要 16 个字符。",
                "A new API key must contain at least 16 characters.",
                NewApiKeyPasswordBox);
            return;
        }

        if (newApiKey.Length == 0 &&
            !string.Equals(baseUrl, _originalBaseUrl, StringComparison.Ordinal))
        {
            ShowValidationError(
                "更改 Base URL 时必须输入新的 API Key。",
                "Enter a new API key when changing the Base URL.",
                NewApiKeyPasswordBox);
            return;
        }

        Edit = new ProviderProfileEdit(
            displayName,
            baseUrl,
            model,
            newApiKey.Length == 0 ? null : newApiKey);
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Edit = null;
        DialogResult = false;
    }

    private void ShowValidationError(string chinese, string english, Control field)
    {
        ValidationErrorText.Text = T(chinese, english);
        ValidationErrorText.Visibility = Visibility.Visible;
        field.Focus();
    }

    protected override void OnClosed(EventArgs e)
    {
        NewApiKeyPasswordBox.Clear();
        if (DialogResult != true)
        {
            Edit = null;
        }

        base.OnClosed(e);
    }

    private static string T(string chinese, string english) =>
        Localizer.Text(chinese, english);
}
