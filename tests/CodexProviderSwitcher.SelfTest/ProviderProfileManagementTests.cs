using System.Text.Json;
using CodexProviderSwitcher.Core;

internal static class ProviderProfileManagementTests
{
    private const string FirstKey = "fake-issue17-first-credential";
    private const string RunningKey = "fake-issue17-running-credential";
    private const string LastKey = "fake-issue17-last-credential";
    private const string ReplacementKey = "fake-issue17-replacement-credential";
    private static readonly string[] FakeSecrets = [FirstKey, RunningKey, LastKey, ReplacementKey];

    public static void Run(Action<bool, string> check)
    {
        var cases = 0;
        var assertions = 0;

        void Case(string name, Action<Action<bool, string>> test)
        {
            cases++;
            void Verify(bool condition, string message)
            {
                assertions++;
                check(condition, $"Issue #17 / {name}: {message}");
            }
            try { test(Verify); }
            catch (Exception exception)
            {
                // A faulty implementation might echo a key in its exception text.
                Verify(false, $"Unexpected {exception.GetType().Name} escaped the test.");
            }
        }

        foreach (var blankKey in new string?[] { null, "", " \t " })
        {
            Case($"rename/model edit with blank key ({blankKey?.Length.ToString() ?? "null"})", verify =>
            {
                var fixture = new Fixture();
                var profile = fixture.Edited;
                var originalId = profile.Id;
                var originalTarget = profile.CredentialTarget;
                var originalOrder = fixture.Settings.ProviderProfiles.ToArray();
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    var result = fixture.Service.SaveEdit(fixture.Settings, originalId,
                        new ProviderProfileEdit($" Renamed {attempt} ", $" {profile.BaseUrl}/ ",
                            $" model-edited-{attempt} ", blankKey), Official());
                    verify(ReferenceEquals(result, profile) && profile.Id == originalId,
                        "Editing did not preserve the original object and stable account ID.");
                    verify(fixture.Settings.ProviderProfiles.SequenceEqual(originalOrder),
                        "Repeated edits changed account count/order or created a duplicate.");
                    verify(profile.DisplayName == $"Renamed {attempt}" &&
                        profile.Model == $"model-edited-{attempt}" &&
                        profile.BaseUrl == "https://edited.example/v1",
                        "The name/model edit or equivalent endpoint normalization was not saved.");
                }
                verify(fixture.Settings.ActiveProviderProfileId == originalId,
                    "Editing changed the selected account ID.");
                verify(profile.CredentialTarget == originalTarget &&
                    fixture.Vault[originalTarget] == RunningKey &&
                    fixture.WriteCalls == 0 && fixture.DeleteCalls == 0,
                    "A same-endpoint blank-key edit replaced or removed the existing credential.");
                verify(!profile.HasPendingChanges, "An inactive account edit was marked as live/pending.");
                fixture.AssertNoSecrets(verify);
            });
        }

        Case("rename only on live account", verify =>
        {
            var fixture = new Fixture();
            fixture.Service.SaveEdit(fixture.Settings, fixture.Edited.Id,
                new ProviderProfileEdit("Only a new name", fixture.Edited.BaseUrl,
                    fixture.Edited.Model, " "), Live(fixture.Edited));
            verify(!fixture.Edited.HasPendingChanges,
                "A display-name-only edit incorrectly created a pending route change.");
            verify(fixture.Edited.DisplayName == "Only a new name" && fixture.WriteCalls == 0,
                "Renaming the live account changed its credential or failed to save the name.");
            fixture.AssertNoSecrets(verify);
        });

        foreach (var blankKey in new string?[] { null, "", " \t " })
        {
            Case($"endpoint change requires a new key ({blankKey?.Length.ToString() ?? "null"})", verify =>
            {
                var fixture = new Fixture();
                var before = fixture.Capture();
                Expect<InvalidOperationException>(() => fixture.Service.SaveEdit(fixture.Settings,
                    fixture.Edited.Id, new ProviderProfileEdit("Changed", "https://different.example/v1",
                        "different-model", blankKey), Live(fixture.Edited)), verify);
                fixture.AssertRestored(before, verify);
                fixture.AssertNoIo(verify);
            });
        }

        foreach (var saveKey in new[] { false, true })
        {
            Case($"fresh key slot preserves live/shared slot ({(saveKey ? "SaveKey" : "SaveEdit")})", verify =>
            {
                var fixture = new Fixture();
                fixture.ShareRunningSlot();
                var profile = fixture.Edited;
                var live = Live(profile);
                var before = fixture.Capture();
                var oldTarget = profile.CredentialTarget;
                ProviderProfile result;
                if (saveKey)
                {
                    result = fixture.Service.SaveKey(fixture.Settings, () => profile,
                        $" {ReplacementKey} ", live);
                }
                else
                {
                    result = fixture.Service.SaveEdit(fixture.Settings, profile.Id,
                        new ProviderProfileEdit("New endpoint", "https://replacement.example/v1",
                            "replacement-model", $" {ReplacementKey} "), live);
                }
                verify(ReferenceEquals(result, profile) && profile.Id == before.Profiles[1].Id &&
                    fixture.Settings.ProviderProfiles.SequenceEqual(before.References),
                    "Saving a new key replaced the account ID/object or added a duplicate.");
                verify(CredentialTargetFactory.IsValid(profile.CredentialTarget) &&
                    profile.CredentialTarget != oldTarget &&
                    fixture.Vault[profile.CredentialTarget] == ReplacementKey,
                    "The new credential did not get its own fresh, managed slot.");
                verify(fixture.First.CredentialTarget == oldTarget &&
                    fixture.Vault[oldTarget] == RunningKey &&
                    before.Vault.All(pair => fixture.Vault.TryGetValue(pair.Key, out var key) && key == pair.Value),
                    "A shared/running/backup credential slot was overwritten or removed.");
                verify(fixture.Vault.Count == before.Vault.Length + 1 && fixture.DeleteCalls == 0,
                    "Key replacement unexpectedly deleted old credentials or created extra slots.");
                verify(profile.HasPendingChanges && fixture.Settings.ActiveProviderProfileId == profile.Id,
                    "A saved key/route change to the live selected account was not retained as a draft.");
                verify(live.CredentialTarget == oldTarget && live.BaseUrl == "https://edited.example/v1",
                    "Account editing changed the captured live route.");
                fixture.AssertNoSecrets(verify);
            });
        }

        foreach (var withKey in new[] { false, true })
        foreach (var fault in TransactionFaults(withKey))
        {
            Case($"edit rollback ({(withKey ? "new key" : "blank key")}, {fault})", verify =>
            {
                using var scratch = new ScratchSettings();
                var fixture = new Fixture();
                fixture.AttachStore(scratch);
                var before = fixture.Capture();
                fixture.Fault = fault;
                var edit = withKey
                    ? new ProviderProfileEdit("Changed", "https://sui-xiang.com/v1", "gpt-6.1-sol", ReplacementKey)
                    : new ProviderProfileEdit("Changed", fixture.Edited.BaseUrl, "changed-model");
                Expect<IOException>(() => fixture.Service.SaveEdit(fixture.Settings,
                    fixture.Edited.Id, edit, Live(fixture.Edited)), verify);
                fixture.AssertFaultConsumed(verify);
                fixture.AssertRestored(before, verify);
                fixture.AssertNoSecrets(verify);
            });
        }

        foreach (var create in new[] { false, true })
        foreach (var fault in new[] { FaultPoint.PrepareBefore, FaultPoint.PrepareAfter,
                     FaultPoint.WriteBefore, FaultPoint.WriteAfter, FaultPoint.SaveBefore, FaultPoint.SaveAfter })
        {
            Case($"save-key rollback ({(create ? "new" : "existing")} account, {fault})", verify =>
            {
                using var scratch = new ScratchSettings();
                var fixture = new Fixture();
                fixture.AttachStore(scratch);
                var before = fixture.Capture();
                fixture.Fault = fault;
                Expect<IOException>(() => fixture.Service.SaveKey(fixture.Settings,
                    () => fixture.Prepare(create), ReplacementKey, Live(fixture.Edited)), verify);
                fixture.AssertFaultConsumed(verify);
                fixture.AssertRestored(before, verify);
                verify(fixture.PrepareCalls == 1, "The prepare callback was called more than once.");
                fixture.AssertNoSecrets(verify);
            });
        }

        foreach (var invalidKey in new[] { "", " \t\r\n ", "short", "123456789012345", " short " })
        {
            Case($"invalid save-key rejected before prepare ({invalidKey.Length})", verify =>
            {
                var fixture = new Fixture();
                var before = fixture.Capture();
                Expect<ArgumentException>(() => fixture.Service.SaveKey(fixture.Settings,
                    () => fixture.Prepare(create: true), invalidKey, Official()), verify);
                verify(fixture.PrepareCalls == 0, "An invalid key reached prepare and could create an orphan.");
                fixture.AssertNoIo(verify);
                fixture.AssertRestored(before, verify);
            });
        }

        Case("invalid edit key has no side effects", verify =>
        {
            var fixture = new Fixture();
            var before = fixture.Capture();
            Expect<ArgumentException>(() => fixture.Service.SaveEdit(fixture.Settings, fixture.Edited.Id,
                new ProviderProfileEdit("Changed", "https://different.example/v1", "changed-model", "short"),
                Official()), verify);
            fixture.AssertNoIo(verify);
            fixture.AssertRestored(before, verify);
        });

        Case("prepare cannot return an orphan", verify =>
        {
            var fixture = new Fixture();
            var before = fixture.Capture();
            Expect<InvalidOperationException>(() => fixture.Service.SaveKey(fixture.Settings,
                () => CreateProfile("Orphan", "https://orphan.example/v1", "orphan-model"),
                ReplacementKey, Official()), verify);
            fixture.AssertNoIo(verify);
            fixture.AssertRestored(before, verify);
        });

        foreach (var fault in new[] { FaultPoint.WriteBefore, FaultPoint.WriteAfter,
                     FaultPoint.SaveBefore, FaultPoint.SaveAfter })
        {
            Case($"repeated failed save-key does not duplicate ({fault})", verify =>
            {
                using var scratch = new ScratchSettings();
                var fixture = new Fixture();
                fixture.AttachStore(scratch);
                var before = fixture.Capture();
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    fixture.Fault = fault;
                    Expect<IOException>(() => fixture.Service.SaveKey(fixture.Settings,
                        () => fixture.Prepare(create: true), ReplacementKey, Official()), verify);
                    fixture.AssertFaultConsumed(verify);
                    fixture.AssertRestored(before, verify);
                }
                var saved = fixture.Service.SaveKey(fixture.Settings,
                    () => fixture.Prepare(create: true), ReplacementKey, Official());
                verify(fixture.Settings.ProviderProfiles.Count == before.References.Length + 1 &&
                    fixture.Settings.ProviderProfiles.Count(profile => profile.Id == saved.Id) == 1 &&
                    fixture.Settings.ProviderProfiles.Count(profile => profile.DisplayName == "Prepared account") == 1,
                    "Retrying a failed new-account save created duplicate accounts.");
                verify(fixture.Vault.Count == before.Vault.Length + 1 &&
                    fixture.Vault[saved.CredentialTarget] == ReplacementKey,
                    "Retries left orphan credential slots behind.");
                fixture.AssertNoSecrets(verify);
            });
        }

        foreach (var fault in new[] { FaultPoint.SaveBefore, FaultPoint.SaveAfter,
                     FaultPoint.DeleteBefore, FaultPoint.DeleteAfter })
        {
            Case($"delete rollback ({fault})", verify =>
            {
                using var scratch = new ScratchSettings();
                var fixture = new Fixture();
                fixture.AttachStore(scratch);
                var before = fixture.Capture();
                fixture.Fault = fault;
                Expect<IOException>(() => fixture.Service.Delete(fixture.Settings,
                    fixture.Edited.Id, Official()), verify);
                fixture.AssertFaultConsumed(verify);
                fixture.AssertRestored(before, verify);
                verify(fixture.Settings.ActiveProviderProfile == fixture.Edited,
                    "Rollback did not restore the selected account object after deletion.");
                fixture.AssertNoSecrets(verify);
            });
        }

        Case("shared-slot deletion preserves surviving account credential", verify =>
        {
            var fixture = new Fixture();
            fixture.ShareRunningSlot();
            var target = fixture.Edited.CredentialTarget;
            var beforeVault = fixture.Vault.ToArray();
            fixture.Service.Delete(fixture.Settings, fixture.Edited.Id, Official());
            verify(fixture.Settings.ProviderProfiles.SequenceEqual(new[] { fixture.First, fixture.Last }) &&
                fixture.Settings.ActiveProviderProfileId == fixture.First.Id,
                "Deletion did not preserve surviving order or select the surviving account.");
            verify(fixture.DeleteCalls == 0 && fixture.WriteCalls == 0 &&
                fixture.First.CredentialTarget == target && fixture.Vault[target] == RunningKey &&
                VaultEquals(fixture.Vault, beforeVault),
                "Deleting one account removed or changed another account's shared credential.");
            fixture.AssertNoSecrets(verify);
        });

        Case("delete inactive selected account while another account is live", verify =>
        {
            var fixture = new Fixture();
            var target = fixture.Edited.CredentialTarget;
            fixture.Service.Delete(fixture.Settings, fixture.Edited.Id, Live(fixture.First));
            verify(fixture.Settings.ProviderProfiles.SequenceEqual(new[] { fixture.First, fixture.Last }) &&
                fixture.Settings.ActiveProviderProfileId == fixture.First.Id,
                "Deleting the inactive UI selection failed or damaged account order/selection.");
            verify(!fixture.Vault.ContainsKey(target) &&
                fixture.Vault[fixture.First.CredentialTarget] == FirstKey &&
                fixture.Vault[fixture.Last.CredentialTarget] == LastKey,
                "Unique-key deletion failed to remove its slot or touched unrelated credentials.");
            fixture.AssertNoSecrets(verify);
        });

        foreach (var mode in new[] { "live-selected", "live-not-selected", "shared-live-slot",
                     "pending-selected", "unverified-selected", "unknown", "missing-id" })
        {
            Case($"unsafe deletion blocked ({mode})", verify =>
            {
                var fixture = new Fixture();
                var id = fixture.Edited.Id;
                var status = Live(fixture.Edited);
                switch (mode)
                {
                    case "live-not-selected":
                        id = fixture.First.Id;
                        status = Live(fixture.First);
                        break;
                    case "shared-live-slot":
                        fixture.ShareRunningSlot();
                        id = fixture.First.Id;
                        break;
                    case "pending-selected":
                        fixture.Edited.CredentialTarget = CredentialTargetFactory.CreateForProfileId(Guid.NewGuid().ToString("N"));
                        fixture.Vault[fixture.Edited.CredentialTarget] = ReplacementKey;
                        fixture.Edited.HasPendingChanges = true;
                        fixture.PersistBaseline();
                        break;
                    case "unverified-selected": status = status with { CredentialTarget = null }; break;
                    case "unknown": status = status with { Mode = ProviderMode.Unknown }; break;
                    case "missing-id": id = Guid.NewGuid().ToString("N"); status = Official(); break;
                }
                var before = fixture.Capture();
                Expect<InvalidOperationException>(() => fixture.Service.Delete(fixture.Settings, id, status), verify);
                fixture.AssertRestored(before, verify);
                fixture.AssertNoIo(verify);
            });
        }

        Case("delete last inactive account does not reload a ghost", verify =>
        {
            using var scratch = new ScratchSettings();
            var fixture = new Fixture();
            fixture.Settings.ProviderProfiles.Remove(fixture.First);
            fixture.Settings.ProviderProfiles.Remove(fixture.Last);
            fixture.Vault.Remove(fixture.First.CredentialTarget);
            fixture.Vault.Remove(fixture.Last.CredentialTarget);
            fixture.AttachStore(scratch);
            fixture.Service.Delete(fixture.Settings, fixture.Edited.Id, Official());
            verify(fixture.Settings.ProviderProfiles.Count == 0 &&
                fixture.Settings.ActiveProviderProfileId is null && fixture.Vault.Count == 0,
                "Deleting the last inactive account left metadata, selection, or an orphan key.");
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var reloaded = scratch.Store.LoadWithStatus(Official());
                verify(!reloaded.IsNewInstall && !reloaded.WasMigrated &&
                    reloaded.Settings.ProviderProfiles.Count == 0 &&
                    reloaded.Settings.ActiveProviderProfileId is null,
                    "SettingsStore recreated a deleted account from legacy flat fields.");
                scratch.Store.Save(reloaded.Settings);
            }
            fixture.AssertNoSecrets(verify);
        });

        foreach (var draft in new[] { "model", "endpoint-and-key", "key-only", "shared-model", "shared-endpoint-and-key" })
        {
            Case($"pending live draft survives old config reload ({draft})", verify =>
            {
                using var scratch = new ScratchSettings();
                var fixture = new Fixture();
                if (draft.StartsWith("shared-", StringComparison.Ordinal)) fixture.ShareRunningSlot();
                fixture.AttachStore(scratch);
                var profile = fixture.Edited;
                var oldLive = Live(profile);
                var oldTarget = profile.CredentialTarget;
                if (draft == "key-only")
                {
                    fixture.Service.SaveKey(fixture.Settings, () => profile, ReplacementKey, oldLive);
                }
                else
                {
                    var endpointChanged = draft.Contains("endpoint", StringComparison.Ordinal);
                    fixture.Service.SaveEdit(fixture.Settings, profile.Id,
                        new ProviderProfileEdit("Saved draft", endpointChanged
                                ? "https://draft.example/v1" : profile.BaseUrl,
                            "draft-model", endpointChanged ? ReplacementKey : null), oldLive);
                }
                var expected = fixture.Settings.ProviderProfiles.Select(ProfileFields.From).ToArray();
                var expectedId = profile.Id;
                verify(profile.HasPendingChanges, "The live account edit was not marked pending.");
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var loaded = scratch.Store.Load(oldLive);
                    verify(loaded.ActiveProviderProfileId == expectedId,
                        "Reload against the old live config switched selection away from the pending active draft.");
                    verify(loaded.ProviderProfiles.Select(ProfileFields.From).SequenceEqual(expected),
                        "Reload overwrote draft fields/credential target or changed a different saved account.");
                    verify(loaded.ProviderProfiles.Count == expected.Length &&
                        loaded.ProviderProfiles.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() == expected.Length,
                        "Draft reload created a duplicate account.");
                    scratch.Store.Save(loaded);
                }
                verify(fixture.Vault[oldTarget] == RunningKey,
                    "Saving/reloading a draft removed the still-running credential slot.");
                fixture.AssertNoSecrets(verify);
            });
        }

        foreach (var baseUrl in new[] { " https://temporary.example/v1/ ", " https://sui-xiang.com/v1/ " })
        {
            Case($"temporary test drafts do not save accounts ({baseUrl.Trim()})", verify =>
            {
                using var scratch = new ScratchSettings();
                var fixture = new Fixture();
                fixture.AttachStore(scratch);
                var before = fixture.Capture();
                var drafts = new List<ProviderProfile>();
                for (var attempt = 0; attempt < 5; attempt++)
                {
                    var draft = ProviderProfileManagementService.CreateTestDraft(baseUrl, " gpt-6.1-sol ");
                    drafts.Add(draft);
                    verify(draft.BaseUrl == ConfigService.NormalizeBaseUrl(baseUrl) && draft.Model == "gpt-6.1-sol" &&
                        draft.Kind == (SettingsStore.IsKimiBaseUrl(baseUrl) ? ProviderKinds.SuiXiang : ProviderKinds.Custom),
                        "The temporary draft did not normalize its route/model/kind.");
                    verify(!fixture.Settings.ProviderProfiles.Contains(draft) &&
                        fixture.Settings.ProviderProfiles.All(saved => saved.Id != draft.Id) && !draft.HasPendingChanges,
                        "A temporary test draft became a saved account or a pending active edit.");
                    fixture.AssertRestored(before, verify);
                }
                verify(drafts.Select(draft => draft.Id).Distinct(StringComparer.Ordinal).Count() == drafts.Count,
                    "Repeated test attempts reused the same mutable temporary draft identity.");
                fixture.AssertNoIo(verify);
                fixture.AssertNoSecrets(verify);
            });
        }

        Case("retired route is rejected by temporary drafts", verify =>
        {
            var fixture = new Fixture();
            var before = fixture.Capture();
            for (var attempt = 0; attempt < 3; attempt++)
            {
                Expect<InvalidOperationException>(() => ProviderProfileManagementService.CreateTestDraft(
                    " https://sui-xiang.com/v1/ ", " K3 "), verify);
                fixture.AssertRestored(before, verify);
            }
            fixture.AssertNoIo(verify);
            fixture.AssertNoSecrets(verify);
        });

        Console.WriteLine($"Provider profile management: {cases} cases, {assertions} checks.");
    }

    // Build with PROFILE_MANAGEMENT_SELFTEST and Program.cs excluded for fake-vault-only execution.
    // The normal runner has real-vault integration checks, which this suite does not need.
#if PROFILE_MANAGEMENT_SELFTEST
    private static int Main()
    {
        var failures = new List<string>();
        Run((condition, message) => { if (!condition) failures.Add(message); });
        if (failures.Count == 0)
        {
            Console.WriteLine("All profile-management self-tests passed.");
            return 0;
        }
        Console.Error.WriteLine($"Profile-management self-tests failed ({failures.Count}):");
        foreach (var failure in failures) Console.Error.WriteLine($"- {failure}");
        return 1;
    }
#endif

    private static IEnumerable<FaultPoint> TransactionFaults(bool withKey) => withKey
        ? [FaultPoint.WriteBefore, FaultPoint.WriteAfter, FaultPoint.SaveBefore, FaultPoint.SaveAfter]
        : [FaultPoint.SaveBefore, FaultPoint.SaveAfter];

    private static ConfigStatus Official() => new(ProviderMode.Official, AppPaths.StableProviderId,
        "official-test-model", "official-review-model", null, true);

    private static ConfigStatus Live(ProviderProfile profile) => new(ProviderMode.ThirdParty,
        AppPaths.StableProviderId, profile.Model, null, profile.BaseUrl, false, profile.CredentialTarget);

    private static ProviderProfile CreateProfile(string name, string baseUrl, string model)
    {
        var id = Guid.NewGuid().ToString("N");
        return new ProviderProfile
        {
            Id = id, Kind = ProviderKinds.Custom, DisplayName = name, BaseUrl = baseUrl,
            Model = model, CredentialTarget = CredentialTargetFactory.CreateForProfileId(id)
        };
    }

    private static void Expect<T>(Action action, Action<bool, string> check) where T : Exception
    {
        try
        {
            action();
            check(false, $"Expected {typeof(T).Name}, but the operation succeeded.");
        }
        catch (T) { }
        catch (Exception exception)
        {
            check(false, $"Expected {typeof(T).Name}, got {exception.GetType().Name}.");
        }
    }

    private static string Metadata(SwitcherSettings settings) => JsonSerializer.Serialize(settings);

    private static bool VaultEquals(Dictionary<string, string> vault, KeyValuePair<string, string>[] expected) =>
        vault.Count == expected.Length &&
        expected.All(pair => vault.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private enum FaultPoint
    {
        None, PrepareBefore, PrepareAfter, WriteBefore, WriteAfter,
        SaveBefore, SaveAfter, DeleteBefore, DeleteAfter
    }

    private sealed record ProfileFields(string Id, string Kind, string Name, string BaseUrl,
        string Model, string CredentialTarget, bool Pending)
    {
        public static ProfileFields From(ProviderProfile profile) => new(profile.Id, profile.Kind,
            profile.DisplayName, profile.BaseUrl, profile.Model, profile.CredentialTarget, profile.HasPendingChanges);
    }

    private sealed record State(ProviderProfile[] References, ProfileFields[] Profiles, string? ActiveId,
        string Metadata, string PersistedMetadata, string? StoredMetadata, KeyValuePair<string, string>[] Vault);
    private sealed class Fixture
    {
        public ProviderProfile First { get; } = CreateProfile("First account", "https://first.example/v1", "first-model");
        public ProviderProfile Edited { get; } = CreateProfile("Selected account", "https://edited.example/v1", "edited-model");
        public ProviderProfile Last { get; } = CreateProfile("Last account", "https://last.example/v1", "last-model");
        public SwitcherSettings Settings { get; }
        public Dictionary<string, string> Vault { get; } = new(StringComparer.Ordinal);
        public ProviderProfileManagementService Service { get; }
        public FaultPoint Fault { get; set; }
        public int PrepareCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public int WriteCalls { get; private set; }
        public int DeleteCalls { get; private set; }
        public int SaveCalls { get; private set; }
        public string PersistedMetadata { get; private set; } = string.Empty;
        private ScratchSettings? _scratch;

        public Fixture()
        {
            Last.HasPendingChanges = true;
            Settings = new SwitcherSettings
            {
                ProviderProfiles = [First, Edited, Last], ActiveProviderProfileId = Edited.Id,
                OfficialModel = "official-test-model", OfficialReviewModel = "official-review-model",
                UiLanguage = Localizer.EnglishCode, UiTheme = ThemePreference.DarkCode,
                OnboardingCompleted = true, RestartAfterSwitch = false
            };
            Vault[First.CredentialTarget] = FirstKey;
            Vault[Edited.CredentialTarget] = RunningKey;
            Vault[Last.CredentialTarget] = LastKey;
            PersistBaseline();
            Service = new ProviderProfileManagementService(Save, Read, Write, Delete);
        }

        public void AttachStore(ScratchSettings scratch)
        {
            _scratch = scratch;
            PersistBaseline();
        }

        public void ShareRunningSlot()
        {
            Vault.Remove(First.CredentialTarget);
            First.CredentialTarget = Edited.CredentialTarget;
            PersistBaseline();
        }

        public void PersistBaseline()
        {
            Settings.SyncLegacyThirdPartyFields();
            _scratch?.Store.Save(Settings);
            PersistedMetadata = Metadata(Settings);
        }

        public ProviderProfile Prepare(bool create)
        {
            PrepareCalls++;
            ThrowAt(FaultPoint.PrepareBefore);
            var profile = create
                ? CreateProfile("Prepared account", "https://sui-xiang.com/v1", "gpt-6.1-sol")
                : Edited;
            if (!create)
            {
                Settings.ProviderProfiles.Remove(profile);
                profile.DisplayName = "Prepared account";
                profile.Kind = ProviderKinds.SuiXiang;
                profile.BaseUrl = "https://sui-xiang.com/v1";
                profile.Model = "gpt-6.1-sol";
                profile.CredentialTarget = First.CredentialTarget;
                profile.HasPendingChanges = true;
            }
            Settings.ProviderProfiles.Insert(0, profile);
            Settings.ActiveProviderProfileId = create ? profile.Id : First.Id;
            Settings.SyncLegacyThirdPartyFields();
            ThrowAt(FaultPoint.PrepareAfter);
            return profile;
        }

        public State Capture() => new(Settings.ProviderProfiles.ToArray(),
            Settings.ProviderProfiles.Select(ProfileFields.From).ToArray(), Settings.ActiveProviderProfileId,
            Metadata(Settings), PersistedMetadata, StoredMetadata(), Vault.ToArray());

        public void AssertRestored(State before, Action<bool, string> check)
        {
            check(Settings.ProviderProfiles.Count == before.References.Length, "Rollback changed the account count.");
            check(Settings.ProviderProfiles.Select(profile => profile.Id).SequenceEqual(before.Profiles.Select(profile => profile.Id)),
                "Rollback changed stable account IDs or their order.");
            check(Settings.ProviderProfiles.SequenceEqual(before.References),
                "Rollback replaced the original profile objects or did not restore list order.");
            check(Settings.ActiveProviderProfileId == before.ActiveId, "Rollback did not restore the selected active ID.");
            check(Settings.ProviderProfiles.Select(ProfileFields.From).SequenceEqual(before.Profiles),
                "Rollback did not restore every profile field, slot, and pending flag.");
            check(Metadata(Settings) == before.Metadata, "Rollback changed settings or legacy route fields.");
            check(PersistedMetadata == before.PersistedMetadata, "Rollback did not restore persisted metadata.");
            check(StoredMetadata() == before.StoredMetadata, "Rollback did not restore the scratch settings file.");
            check(VaultEquals(Vault, before.Vault), "Rollback lost/changed a credential or left an orphan slot.");
        }

        public void AssertNoIo(Action<bool, string> check) => check(
            ReadCalls == 0 && WriteCalls == 0 && DeleteCalls == 0 && SaveCalls == 0,
            "A rejected operation accessed credentials or persistence.");

        public void AssertFaultConsumed(Action<bool, string> check) => check(Fault == FaultPoint.None,
            "The operation never reached the intended injected failure point.");

        public void AssertNoSecrets(Action<bool, string> check)
        {
            foreach (var json in new[] { Metadata(Settings), PersistedMetadata,
                         JsonSerializer.Serialize(Settings.ProviderProfiles), StoredMetadata() ?? string.Empty })
            {
                check(!FakeSecrets.Any(secret => json.Contains(secret, StringComparison.Ordinal)),
                    "Serialized account/settings metadata contains a fake API secret.");
            }
        }

        private string? StoredMetadata() => _scratch is null ? null : File.ReadAllText(_scratch.SettingsPath);

        private void Save(SwitcherSettings settings)
        {
            SaveCalls++;
            ThrowAt(FaultPoint.SaveBefore);
            settings.SyncLegacyThirdPartyFields();
            _scratch?.Store.Save(settings);
            PersistedMetadata = Metadata(settings);
            ThrowAt(FaultPoint.SaveAfter);
        }

        private string? Read(string target)
        {
            ReadCalls++;
            return Vault.GetValueOrDefault(target);
        }

        private void Write(string target, string key)
        {
            WriteCalls++;
            CredentialTargetFactory.RequireValid(target);
            ThrowAt(FaultPoint.WriteBefore);
            Vault[target] = key;
            ThrowAt(FaultPoint.WriteAfter);
        }

        private void Delete(string target)
        {
            DeleteCalls++;
            CredentialTargetFactory.RequireValid(target);
            ThrowAt(FaultPoint.DeleteBefore);
            Vault.Remove(target);
            ThrowAt(FaultPoint.DeleteAfter);
        }

        private void ThrowAt(FaultPoint point)
        {
            if (Fault != point) return;
            Fault = FaultPoint.None; // Rollback delegates are allowed to succeed.
            throw new IOException($"Injected {point} failure.");
        }
    }
    private sealed class ScratchSettings : IDisposable
    {
        private const string Prefix = "provider-profile-management-selftest-";
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory(Prefix);
        public string SettingsPath { get; }
        public SettingsStore Store { get; }

        public ScratchSettings()
        {
            SettingsPath = Path.Combine(_directory.FullName, "settings.json");
            Store = new SettingsStore(SettingsPath);
        }

        public void Dispose()
        {
            var path = Path.GetFullPath(_directory.FullName);
            var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (!string.Equals(Path.GetDirectoryName(path), temp, StringComparison.OrdinalIgnoreCase) ||
                !_directory.Name.StartsWith(Prefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing to clean up a directory outside the owned test scratch path.");
            }
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }
}
