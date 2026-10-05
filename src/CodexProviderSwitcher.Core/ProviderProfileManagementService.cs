namespace CodexProviderSwitcher.Core;

public sealed record ProviderProfileEdit(
    string DisplayName, string BaseUrl, string Model, string? NewApiKey = null);

// Account management never writes Codex config, changes its provider, or restarts it.
// Injected persistence/credential operations allow failure-after-write regression tests.
public sealed class ProviderProfileManagementService(
    Action<SwitcherSettings> saveSettings,
    Func<string, string?> readCredential,
    Action<string, string> writeCredential,
    Action<string> deleteCredential)
{
    public static ProviderProfile CreateTestDraft(string baseUrl, string model)
    {
        var normalized = ConfigService.NormalizeBaseUrl(baseUrl);
        model = model.Trim();
        if (model.Length == 0) throw new ArgumentException(Localizer.Text(
            "第三方模型不能为空。", "The third-party model cannot be empty."));
        ProviderAvailabilityPolicy.RequireAvailableThirdPartyRoute(normalized, model);
        return new ProviderProfile
        {
            BaseUrl = normalized,
            Model = model,
            Kind = SettingsStore.IsKimiBaseUrl(normalized) ? ProviderKinds.SuiXiang : ProviderKinds.Custom
        };
    }

    public ProviderProfile SaveEdit(
        SwitcherSettings settings, string profileId, ProviderProfileEdit edit,
        ConfigStatus liveStatus)
    {
        var profile = RequireProfile(settings, profileId);
        var name = edit.DisplayName.Trim();
        if (name.Length > 80)
        {
            throw new ArgumentException(Localizer.Text(
                "账号名称不能超过 80 个字符。", "The account name cannot exceed 80 characters."));
        }
        var baseUrl = ConfigService.NormalizeBaseUrl(edit.BaseUrl);
        var model = edit.Model.Trim();
        if (model.Length == 0 || model.Contains('\r') || model.Contains('\n'))
        {
            throw new ArgumentException(Localizer.Text("请输入有效模型名称。", "Enter a valid model name."));
        }
        ProviderAvailabilityPolicy.RequireAvailableThirdPartyRoute(baseUrl, model);
        var key = ValidateOptionalKey(edit.NewApiKey);
        var endpointChanged = true;
        try
        {
            endpointChanged = !string.Equals(
                ConfigService.NormalizeBaseUrl(profile.BaseUrl), baseUrl, StringComparison.Ordinal);
        }
        catch (ArgumentException) { /* An incomplete old account can be repaired with a new key. */ }
        if (endpointChanged && key is null)
        {
            throw new InvalidOperationException(Localizer.Text(
                "更改 Base URL 时必须填写新 API Key，避免旧密钥发送到不同服务。",
                "Changing the Base URL requires a new API key so the saved key cannot be sent to another service."));
        }

        var pending = profile.HasPendingChanges ||
            (IsLiveProfile(settings, profile, liveStatus) &&
             (endpointChanged || profile.Model != model || key is not null));
        return SaveTransaction(settings, () =>
        {
            profile.DisplayName = name;
            profile.BaseUrl = baseUrl;
            profile.Model = model;
            profile.Kind = SettingsStore.IsKimiBaseUrl(baseUrl)
                ? ProviderKinds.SuiXiang : ProviderKinds.Custom;
            profile.HasPendingChanges = pending;
            return profile;
        }, key);
    }

    public ProviderProfile SaveKey(
        SwitcherSettings settings, Func<ProviderProfile> prepareProfile,
        string newApiKey, ConfigStatus liveStatus)
    {
        var key = ValidateOptionalKey(newApiKey) ?? throw new ArgumentException(Localizer.Text(
            "请输入完整 API Key。", "Enter the complete API key."));
        var liveProfileIds = settings.ProviderProfiles
            .Where(profile => IsLiveProfile(settings, profile, liveStatus))
            .Select(profile => profile.Id).ToHashSet(StringComparer.Ordinal);
        return SaveTransaction(settings, () =>
        {
            var profile = prepareProfile();
            if (!settings.ProviderProfiles.Contains(profile))
            {
                throw new InvalidOperationException("The prepared account is not in the settings collection.");
            }
            profile.HasPendingChanges |= liveProfileIds.Contains(profile.Id);
            return profile;
        }, key);
    }

    public void Delete(SwitcherSettings settings, string profileId, ConfigStatus liveStatus)
    {
        var profile = RequireProfile(settings, profileId);
        if (liveStatus.Mode == ProviderMode.Unknown || IsLiveProfile(settings, profile, liveStatus))
        {
            throw new InvalidOperationException(Localizer.Text(
                "不能删除当前使用或尚未确认的账号。请先切换到官方 Codex 或另一个账号，再删除。",
                "The current or unverified account cannot be deleted. Switch to Official Codex or another account first."));
        }

        var snapshot = new SettingsSnapshot(settings);
        var removeKey = CredentialTargetFactory.IsValid(profile.CredentialTarget) &&
            !settings.ProviderProfiles.Any(other =>
                other.Id != profile.Id && other.CredentialTarget == profile.CredentialTarget);
        var previousKey = removeKey ? readCredential(profile.CredentialTarget) : null;
        var deletionAttempted = false;
        var saveAttempted = false;
        try
        {
            settings.ProviderProfiles.Remove(profile);
            if (settings.ActiveProviderProfileId == profile.Id)
            {
                settings.ActiveProviderProfileId = settings.ProviderProfiles.FirstOrDefault(other =>
                    !ProviderAvailabilityPolicy.IsRetiredKimiProfile(other))?.Id ??
                    settings.ProviderProfiles.FirstOrDefault()?.Id;
            }
            saveAttempted = true;
            saveSettings(settings);
            if (removeKey)
            {
                deletionAttempted = true;
                deleteCredential(profile.CredentialTarget);
            }
        }
        catch (Exception original)
        {
            var rollbackFailures = new List<Exception>();
            snapshot.Restore(settings);
            if (deletionAttempted)
            {
                TryRollback(() =>
                {
                    if (previousKey is not null) writeCredential(profile.CredentialTarget, previousKey);
                    else deleteCredential(profile.CredentialTarget);
                }, rollbackFailures);
            }
            if (saveAttempted) TryRollback(() => saveSettings(settings), rollbackFailures);
            ThrowIfRollbackFailed(original, rollbackFailures);
            throw;
        }
    }

    public static bool IsLiveProfile(
        SwitcherSettings settings, ProviderProfile profile, ConfigStatus liveStatus) =>
        liveStatus.Mode == ProviderMode.ThirdParty &&
        ((CredentialTargetFactory.IsValid(liveStatus.CredentialTarget) &&
          profile.CredentialTarget == liveStatus.CredentialTarget) ||
         (settings.ActiveProviderProfileId == profile.Id &&
          (profile.HasPendingChanges || !CredentialTargetFactory.IsValid(liveStatus.CredentialTarget))));

    private ProviderProfile SaveTransaction(
        SwitcherSettings settings, Func<ProviderProfile> prepareProfile, string? key)
    {
        var snapshot = new SettingsSnapshot(settings);
        string? newTarget = null;
        var saveAttempted = false;
        try
        {
            var profile = prepareProfile();
            if (key is not null)
            {
                // A fresh slot keeps both the running config and older config backups valid.
                string candidate;
                do { candidate = CredentialTargetFactory.CreateForProfileId(Guid.NewGuid().ToString("N")); }
                while (readCredential(candidate) is not null);
                newTarget = candidate;
                writeCredential(newTarget, key);
                profile.CredentialTarget = newTarget;
            }
            saveAttempted = true;
            saveSettings(settings);
            return profile;
        }
        catch (Exception original)
        {
            var rollbackFailures = new List<Exception>();
            snapshot.Restore(settings);
            if (newTarget is not null) TryRollback(() => deleteCredential(newTarget), rollbackFailures);
            if (saveAttempted) TryRollback(() => saveSettings(settings), rollbackFailures);
            ThrowIfRollbackFailed(original, rollbackFailures);
            throw;
        }
    }

    private static ProviderProfile RequireProfile(SwitcherSettings settings, string profileId)
    {
        var matches = settings.ProviderProfiles.Where(profile => profile.Id == profileId).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException(Localizer.Text(
            "请选择一个有效的已保存账号。", "Select a valid saved account."));
        return matches[0];
    }

    private static string? ValidateOptionalKey(string? value)
    {
        var key = value?.Trim();
        if (string.IsNullOrEmpty(key)) return null;
        if (key.Length < 16) throw new ArgumentException(Localizer.Text(
            "请输入新生成的完整 API Key。", "Enter the complete newly generated API key."));
        return key;
    }

    private static void TryRollback(Action action, List<Exception> failures)
    {
        try { action(); } catch (Exception exception) { failures.Add(exception); }
    }

    private static void ThrowIfRollbackFailed(Exception original, List<Exception> failures)
    {
        if (failures.Count == 0) return;
        throw new AggregateException(Localizer.Text(
            "账号操作失败，且未能完整恢复。请保留设置文件并重试，不要重复添加账号。",
            "The account operation failed and could not be fully restored. Retain the settings file and retry without adding duplicate accounts."),
            new[] { original }.Concat(failures));
    }

    private sealed class SettingsSnapshot(SwitcherSettings settings)
    {
        private readonly string? _activeId = settings.ActiveProviderProfileId;
        private readonly (ProviderProfile Original, ProviderProfile Copy)[] _profiles =
            settings.ProviderProfiles.Select(profile => (profile, new ProviderProfile
            {
                Id = profile.Id,
                Kind = profile.Kind,
                DisplayName = profile.DisplayName,
                BaseUrl = profile.BaseUrl,
                Model = profile.Model,
                CredentialTarget = profile.CredentialTarget,
                HasPendingChanges = profile.HasPendingChanges
            })).ToArray();

        public void Restore(SwitcherSettings settings)
        {
            settings.ProviderProfiles.Clear();
            foreach (var (original, copy) in _profiles)
            {
                original.Id = copy.Id;
                original.Kind = copy.Kind;
                original.DisplayName = copy.DisplayName;
                original.BaseUrl = copy.BaseUrl;
                original.Model = copy.Model;
                original.CredentialTarget = copy.CredentialTarget;
                original.HasPendingChanges = copy.HasPendingChanges;
                settings.ProviderProfiles.Add(original);
            }
            settings.ActiveProviderProfileId = _activeId;
            settings.SyncLegacyThirdPartyFields();
        }
    }
}
