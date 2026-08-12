# Codex Provider Switcher

A native Windows WPF application that switches Codex between:

- the existing official ChatGPT/OpenAI login; and
- an OpenAI-compatible third-party Responses API; or
- the experimental SuiXiang K3 route through the bundled loopback adapter.

The switcher deliberately keeps `model_provider = "OpenAI"` in both modes. Codex
stores the provider ID in its thread index, so changing that ID creates separate
history buckets and makes conversations appear to disappear. This application
changes only the backend settings behind the stable ID.

This is an independent open-source utility and is not an official OpenAI
product.

## Install

1. Download the Windows x64 ZIP from the latest GitHub Release.
2. Extract the archive.
3. Run `install.ps1` from PowerShell.
4. Start **Codex Provider Switcher** from the desktop shortcut.

Windows 10 or 11 and the .NET 8 Windows Desktop Runtime are required. The
optional embedded SuiXiang sign-in also needs the Microsoft Edge WebView2
Runtime, which is normally installed with current Edge. The release is currently
unsigned, so Windows SmartScreen may ask for confirmation.

Use **Settings** to switch the complete interface between Chinese and English,
and to select Light, Dark, or System appearance. Both choices are remembered.

## Guided setup

On a new install, the application opens a short bilingual setup flow. It first
checks the Codex configuration, WebView2 availability, current route, and
managed credential reference without reading an API key or changing any
setting. It then offers four equally valid choices:

- **Sign in with SuiXiang** opens SuiXiang's real sign-in page in an isolated
  WebView2 profile. The user completes any Tencent CAPTCHA personally. The app
  does not read passwords, CAPTCHA data, or cookies. After sign-in, the user
  creates an API key with SuiXiang and pastes it into the app.
- **Connect SuiXiang K3 (experimental)** uses a SuiXiang API key for **`k3`**.
  An existing key is reused only when exactly one saved K3 account matches the
  normalized Base URL, model, and adapter. Direct SuiXiang accounts and
  duplicate K3 accounts are never selected implicitly. The app verifies
  SuiXiang's upstream Chat Completions endpoint,
  health-checks and starts the bundled Linux router inside WSL, then builds its
  managed model catalog and restarts Codex around the startup-only
  catalog/config write. Other SuiXiang model IDs remain ordinary direct
  Responses routes; only `k3` uses the experimental bridge.
- **Use another service** accepts an OpenAI-compatible Base URL, model, and a
  newly generated API key.
- **Use official Codex for now** keeps the current official route unchanged.

Every choice ends on a confirmation page. Provider validation and switching
start only after the user selects **Apply settings**; Back, Cancel, and the
environment check do not write provider configuration or credentials.

The SuiXiang route is optional. It is not required to use a custom provider or
official Codex. Automatic API-key retrieval or creation is intentionally not
implemented until SuiXiang supplies an approved desktop authorization and key
management API. The embedded page reports loading, network, HTTP, and process
failures with retry or manual-key fallback, but never guesses whether sign-in
succeeded.

After setup, Home shows the current route and one primary action: connect a
provider, switch to it, or switch back to official Codex. Advanced provider,
diagnostic, backup, language, and appearance controls remain in the navigation
when needed. **Run setup again** is available at the top of Settings and never
deletes anything merely by opening or cancelling the guide. Connecting a new
provider requires its own explicitly supplied API key; official sign-in,
history, and backups are not removed.

The Providers and setup model fields are editable ComboBoxes for ordinary
custom providers. **Refresh model list** resolves the credential for the
currently entered Base URL only, never reusing a key from another profile. A
missing current model is retained and called out as still requiring a live
compatibility test. SuiXiang refreshes its list dynamically and every switch
performs a fresh live compatibility test; direct models use SuiXiang Responses,
while `k3` uses the upstream Chat Completions contract plus the WSL bridge
health check. Any SuiXiang failure is fail closed, with no write-anyway option.
The Providers page also lets you create and select multiple saved key profiles,
including multiple keys for the same SuiXiang Base URL.

## Interface

Version 1.4.2 uses a compact native Windows workspace:

- **Home:** current route, shared-history health, and quick switching.
- **Providers:** official OpenAI and third-party endpoint, model, and key
  management.
- **Diagnostics:** official host, plugin tool protocol, image generation, and
  Mobile Remote prerequisites.
- **Backups:** a read-only table of every timestamped `config.toml` backup.
- **Settings:** language, appearance, restart behavior, Sol Ultra readiness,
  optional Luna task agent, automatic update status, and local data access.

The SuiXiang K3 route is experimental and intentionally limited to text
Responses and ordinary function tools. It does not promise image generation,
Mobile Remote, or native Codex plugin/app transports. Its API key remains
scoped to the SuiXiang upstream profile while Codex itself talks only to the
WSL-local `127.0.0.1:17866/v1` router. The release also retains a Windows
router binary for migration and diagnostics, but active Codex traffic does not
cross the Windows/WSL loopback boundary.

In Simplified Chinese Codex builds, xhigh and Ultra can both appear as `极高`.
Ultra is the bottom item with the `更快消耗使用额度` warning. The switcher checks
the durable `enabled-reasoning-efforts` list; the native
`show-ultra-in-model-picker-slider` value is only a one-shot enablement request
and normally returns to `false` after Codex consumes it.

The navigation pane collapses to icons at narrow window sizes. All operational
status remains visible in the bottom status bar.

## Automatic update checks

The application checks the repository's latest stable GitHub Release in the
background after startup. When a newer version is available, Home shows a
compact notice that opens the trusted release page. Settings also shows the
current update status and provides a manual **Check now** action.

The check uses GitHub's public latest-release API and does not require a GitHub
account or token. A network or rate-limit failure does not block startup or
provider switching. The application never downloads or installs an update
silently; the user chooses the release asset from GitHub.

## Safety model

- The official ChatGPT login is never logged out or overwritten.
- The third-party API key is stored in Windows Credential Manager.
- Every saved provider profile has its own managed Credential Manager target.
  Existing v1.3 SuiXiang keys continue using their original target after an
  in-place migration.
- `config.toml` contains only a token-broker command, never the API key.
- Every configuration write first creates a timestamped backup under
  `%LOCALAPPDATA%\CodexProviderSwitcher\Backups`.
- Session JSONL files and chat bodies are not rewritten during provider switches.
- The GUI requires a complete `/v1/responses` SSE result because Codex does not
  use the Chat Completions wire protocol for custom providers.
- SuiXiang K3 switching starts the bundled WSL-local router only after an
  upstream compatibility test and an in-WSL health check. The router is
  launched without API keys or secrets in command-line arguments or logs.

An API key pasted into a chat must be considered exposed. Revoke it at the
provider and create a new one before saving it in this app.

## Capability diagnostics

Version 1.1 adds separate checks for the host and the selected third-party
provider:

- **Official host:** confirms the existing ChatGPT login and reports the Apps,
  plugins, Remote, and image-generation feature flags.
- **Plugin tool protocol:** performs a harmless two-request function-call
  round trip. This checks both the model's `function_call` output and its
  handling of `function_call_output`.
- **Image generation:** calls `/v1/images/generations`, which is the backend
  used by Codex's current image-generation tool. A test only passes after a real
  PNG, JPEG, or WebP file has been decoded and saved under
  `%LOCALAPPDATA%\CodexProviderSwitcher\Diagnostics`. The probe mirrors the
  current Codex request with `gpt-image-2` and automatic image settings.
- **Mobile Remote:** opens the official Codex app. Initial phone pairing starts
  from **Set up Remote** in the official sidebar when that account and workspace
  expose the entry.

Successful tool and image checks are remembered for the exact endpoint and
model that passed. Changing either value returns the corresponding status to
untested, so a result from one provider is never shown for another.

The release validation completed text streaming, the full function-call round
trip, and an actual `/v1/images/generations` request through the configured
third-party endpoint.

## Optional Luna task agent

Settings can install a narrowly scoped Codex task-agent definition at
`%CODEX_HOME%\agents\luna-worker.toml` (or `%USERPROFILE%\.codex\agents` when
`CODEX_HOME` is not set). It selects `gpt-5.6-luna` with maximum reasoning for
bounded delegated tasks. Installation is optional and does not change
`config.toml`, the active provider, official authentication, or chat history.

The switcher manages only the exact file it created. The official OpenAI route
supports this managed Luna agent. SuiXiang currently does not, so when switching
to SuiXiang the managed file is parked as
`luna-worker.toml.disabled-by-provider-switcher`; switching back to official
restores it automatically. Other custom providers are provider-dependent: the
switcher does not automatically label them unsupported or substitute another
model. If a different `luna-worker.toml` already exists, the switcher reports a
conflict and leaves it untouched. Other agent definitions are never changed.

## Sol Ultra readiness

Settings reports Ultra as ready when `ultra` is present in
`[desktop].enabled-reasoning-efforts`. If it is missing, the one-click action
closes Codex, writes the native one-shot
`show-ultra-in-model-picker-slider = true` request with a backup, and relaunches
Codex. Codex normally consumes that request and resets it to `false`; this does
not mean Ultra was disabled.

Sol supports Ultra in Codex; the optional Luna task agent remains on Max. The
switcher does not write `model_reasoning_effort = "ultra"`, change the selected
model, or modify chat history. In Simplified Chinese Codex, the bottom `极高`
option with the `更快消耗使用额度` warning is Ultra.

OpenAI references:

- [Plugins](https://learn.chatgpt.com/docs/plugins)
- [Remote connections](https://learn.chatgpt.com/docs/remote-connections)
- [Function calling](https://developers.openai.com/api/docs/guides/function-calling)
- [Image generation](https://developers.openai.com/api/docs/guides/image-generation)

## Boundaries

- Installed plugins and their configuration remain available in third-party
  mode, but each plugin may still require its own OAuth connection and
  permission approval.
- Plugins are not a native mobile surface. Mobile Remote uses the connected
  host's plugins, credentials, permissions, and local tools.
- The switcher can preserve and inspect the prerequisites for Remote, but the
  official desktop/mobile pairing cannot be completed or proven by this
  utility.
- If automatic restart is enabled, switching briefly interrupts active Remote
  sessions while the official desktop app restarts. The utility does not sign
  out or delete device pairings.
- The endpoint must support `/v1/responses` and SSE streaming. A
  Chat-Completions-only endpoint is not compatible.
- SuiXiang K3 is an experimental adapter for `k3` only. The router's managed
  catalog is startup-loaded, so SuiXiang K3 switches and switches away from
  SuiXiang K3 always stop and restart Codex even when the generic restart
  preference is disabled.
- SuiXiang K3 currently has no image, Mobile Remote, or native Codex plugin/app
  capability guarantee; other SuiXiang models and ordinary custom providers
  retain direct Responses behavior and editable model IDs.
- The default endpoint and model are examples and can be changed in the GUI.
- Provider traffic can contain prompts, source code, and tool context. The
  third-party provider's privacy, retention, billing, and availability policies
  apply.

## Build

The installed application requires the .NET 8 Windows Desktop Runtime, which is
already included on the target machine. A .NET 8 SDK is needed only to build.

```powershell
.\build.ps1 -DotNet "C:\path\to\dotnet.exe"
.\install.ps1
```

To create the versioned ZIP and SHA-256 file used by GitHub Releases:

```powershell
.\release.ps1 -Version 1.4.2 -DotNet "C:\path\to\dotnet.exe"
```

The installed files are placed in:

```text
%LOCALAPPDATA%\Programs\CodexProviderSwitcher
```

The installer creates:

```text
Desktop\Codex Provider Switcher.lnk
```
