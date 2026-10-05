# Codex Provider Switcher

A native Windows WPF application that switches Codex between:

- the existing official ChatGPT/OpenAI login; and
- an OpenAI-compatible third-party Responses API.

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
setting. It then offers three choices:

- **Sign in with SuiXiang** opens SuiXiang's real sign-in page in an isolated
  WebView2 profile. The user completes any Tencent CAPTCHA personally. The app
  does not read passwords, CAPTCHA data, or cookies. After sign-in, the user
  creates an API key with SuiXiang and pastes it into the app.
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
performs a fresh live compatibility test through SuiXiang Responses. The retired
`k3` route is filtered from discovery and rejected before any network or
configuration change. Any SuiXiang failure is fail closed, with no write-anyway
option.
The Providers page also lets you create and select multiple saved key profiles,
including multiple keys for the same SuiXiang Base URL.

## Interface

Version 1.4.8 uses a compact native Windows workspace:

- **Home:** current route, shared-history health, and quick switching.
- **Providers:** official OpenAI and third-party endpoint, model, and key
  management.
- **Diagnostics:** official host, plugin tool protocol, image generation, and
  Mobile Remote prerequisites.
- **Backups:** a read-only table of every timestamped `config.toml` backup.
- **Settings:** language, appearance, restart behavior, Sol Ultra readiness,
  Sol/Terra 1M context, optional Luna task agent, automatic update status, and
  local data access.

The experimental SuiXiang K3 route is retired in v1.4.3. It is hidden from new
setup, model discovery, and the saved-account picker, and core validation blocks
new K3 tests and switches. Existing K3 profiles and credentials are preserved
for recovery but are not exposed as selectable accounts. If a legacy K3 config
is still active, the app shows a bilingual warning and lets the user switch to
official OpenAI or a supported direct provider without deleting chat history.

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
- Retiring K3 does not delete its saved profile, credential, backups, or chat
  history. New K3 network tests and configuration writes fail closed.

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

## Sol Max / Ultra menu

Switching to GPT-6.1 Sol enables `max` and `ultra` in the desktop menu
permissions and requests Ultra slider visibility. Settings also provides a
one-click repair with a backup and restart. Existing permitted efforts are
preserved, including multiline arrays; the selected API reasoning effort is
not changed. The UI reports menu configuration, not guaranteed model access.

Max and Ultra are separate desktop controls. GPT-6.1 Sol's API supports `max`;
desktop Ultra is an orchestration mode and remains dependent on the client and
account. The switcher never inserts a fabricated `ultra` reasoning level into
the native model metadata. The optional Luna task agent remains unchanged.

## GPT-6.1 Sol and model discovery

New setups default to `gpt-6.1-sol`. Existing model selections and saved API
keys are preserved. Opening the app or selecting a saved provider account
automatically loads that account's current `/models` list. You can still refresh
manually or type a custom model ID if the endpoint does not advertise models.
Switching a selected saved Sol account between `gpt-5.6-sol`, `gpt-6-sol`, and
`gpt-6.1-sol` on the same endpoint reuses that account's key; keys are not copied
to a different endpoint or guessed from another account.

Select `gpt-6.1-sol` and apply the provider change, then start a new Codex task.
Keep Codex updated if its model picker does not list GPT-6.1 Sol.
An advertised model ID alone does not prove Responses API compatibility; use
the live connection test to verify the selected route.

## Sol / Terra context preset

Settings can request the one-million-token Codex configuration for
`gpt-6.1-sol`, `gpt-6-sol`, `gpt-5.6-sol`, or `gpt-5.6-terra` with one action:

```toml
model_context_window = 1000000
model_auto_compact_token_limit = 900000
```

GPT-6.1 Sol switches apply this preset automatically when no custom values or
explicit opt-out exist. Restoring defaults records an opt-out so later provider
switches do not re-enable it. Other supported models retain the manual action.

When GPT-6.1 Sol requests long context, the switcher writes a separate managed
`codex-provider-switcher-sol-model-catalog.json` using the exact installed 6.1
entry. It corrects an outdated 272K cap to the documented 922,000-token maximum
input (the total model window is 1,050,000 with up to 128,000 output tokens).
Native instructions, tools, reasoning levels, headroom percentage, and other
models are preserved. `models_cache.json` is never edited. A user-owned catalog
is never replaced; missing exact 6.1 metadata safely blocks catalog generation.
Failed writes roll back both configuration and the managed catalog.

These are requested configuration values, not a guarantee of the actual
runtime context window. Codex may clamp them using its model catalog and
effective-context adjustment; the upstream provider may impose a lower limit.
The action stops Codex, creates a
timestamped `config.toml` backup, writes and verifies both top-level values,
then starts Codex again. Start a new Codex task after the restart so the new
context budget is used. Restoring Codex defaults removes the managed pair
through the same transaction.

Only values written by the switcher are automatically removed when the active
model changes away from Sol or Terra. Existing custom context values are
reported as custom and are not silently overwritten or deleted. A third-party
route using one of these model IDs still depends on that provider
actually supporting the requested window; this setting changes the Codex client
budget, not the upstream model.
Long inputs can cost more; check your provider's current pricing before use.

OpenAI references:

- [Plugins](https://learn.chatgpt.com/docs/plugins)
- [Remote connections](https://learn.chatgpt.com/docs/remote-connections)
- [Function calling](https://developers.openai.com/api/docs/guides/function-calling)
- [Image generation](https://developers.openai.com/api/docs/guides/image-generation)
- [Codex configuration reference](https://learn.chatgpt.com/docs/config-file/config-reference)
- [Codex changelog: GPT-6.1 Sol support](https://learn.chatgpt.com/docs/changelog)
- [GPT-5.6 Sol model](https://developers.openai.com/api/docs/models/gpt-5.6-sol)
- [GPT-6.1 Sol specifications](https://developers.openai.com/api/docs/models/gpt-6.1-sol)
- [OpenAI model context limits on Amazon Bedrock](https://developers.openai.com/api/docs/guides/amazon-bedrock#responses-api-feature-availability)

## Boundaries

- Installed plugins and their configuration remain available in third-party
  mode, but each plugin may still require its own OAuth connection and
  permission approval.
- Plugins are not a native mobile surface. Mobile Remote uses the connected
  host's plugins, credentials, permissions, and local tools.
- The switcher can preserve and inspect the prerequisites for Remote, but the
  official desktop/mobile pairing cannot be completed or proven by this
  utility.
- Every provider switch briefly interrupts active tasks and Remote sessions:
  the utility stops Codex, writes and verifies the route, then starts Codex
  and waits for it to stabilize. It does not sign out or delete device
  pairings.
- The endpoint must support `/v1/responses` and SSE streaming. A
  Chat-Completions-only endpoint is not compatible.
- K3 remains retired. The bundled adapter files are retained only so upgrades
  can recognize and safely leave an existing legacy configuration; they are not
  a supported provider option. Other SuiXiang models and ordinary custom
  providers retain direct Responses behavior and editable model IDs.
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
.\release.ps1 -Version 1.4.8 -DotNet "C:\path\to\dotnet.exe"
```

The installed files are placed in:

```text
%LOCALAPPDATA%\Programs\CodexProviderSwitcher
```

The installer creates:

```text
Desktop\Codex Provider Switcher.lnk
```
