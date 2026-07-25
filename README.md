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

1. Download `CodexProviderSwitcher-v1.2.0-win-x64.zip` from the latest GitHub
   Release.
2. Extract the archive.
3. Run `install.ps1` from PowerShell.
4. Start **Codex Provider Switcher** from the desktop shortcut.

Windows 10 or 11 and the .NET 8 Windows Desktop Runtime are required. The
release is currently unsigned, so Windows SmartScreen may ask for confirmation.

Use the `CHN / ENG` selector in the window header to change the complete
interface language. The selection is remembered for the next launch.

## Safety model

- The official ChatGPT login is never logged out or overwritten.
- The third-party API key is stored in Windows Credential Manager.
- `config.toml` contains only a token-broker command, never the API key.
- Every configuration write first creates a timestamped backup under
  `%LOCALAPPDATA%\CodexProviderSwitcher\Backups`.
- Session JSONL files and chat bodies are not rewritten during provider switches.
- The GUI requires a complete `/v1/responses` SSE result because Codex does not
  use the Chat Completions wire protocol for custom providers.

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
- **Mobile Remote:** opens the official Codex Connections page. Complete the QR
  pairing in the ChatGPT desktop and mobile apps with the same account and
  workspace.

Successful tool and image checks are remembered for the exact endpoint and
model that passed. Changing either value returns the corresponding status to
untested, so a result from one provider is never shown for another.

The release validation completed text streaming, the full function-call round
trip, and an actual `/v1/images/generations` request through the configured
third-party endpoint.

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
  official desktop/mobile QR pairing cannot be completed by this utility.
- If automatic restart is enabled, switching briefly interrupts active Remote
  sessions while the official desktop app restarts. The utility does not sign
  out or delete device pairings.
- The endpoint must support `/v1/responses` and SSE streaming. A
  Chat-Completions-only endpoint is not compatible.
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
.\release.ps1 -Version 1.2.0 -DotNet "C:\path\to\dotnet.exe"
```

The installed files are placed in:

```text
%LOCALAPPDATA%\Programs\CodexProviderSwitcher
```

The installer creates:

```text
Desktop\Codex Provider Switcher.lnk
```
