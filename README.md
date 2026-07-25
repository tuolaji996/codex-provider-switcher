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

1. Download `CodexProviderSwitcher-v1.0.0-win-x64.zip` from the latest GitHub
   Release.
2. Extract the archive.
3. Run `install.ps1` from PowerShell.
4. Start **Codex Provider Switcher** from the desktop shortcut.

Windows 10 or 11 and the .NET 8 Windows Desktop Runtime are required. The
release is currently unsigned, so Windows SmartScreen may ask for confirmation.

## Safety model

- The official ChatGPT login is never logged out or overwritten.
- The third-party API key is stored in Windows Credential Manager.
- `config.toml` contains only a token-broker command, never the API key.
- Every configuration write first creates a timestamped backup under
  `%LOCALAPPDATA%\CodexProviderSwitcher\Backups`.
- Session JSONL files and chat bodies are not rewritten during provider switches.
- The GUI tests `/v1/responses` plus SSE streaming because Codex does not use the
  Chat Completions wire protocol for custom providers.

An API key pasted into a chat must be considered exposed. Revoke it at the
provider and create a new one before saving it in this app.

## Version 1.0 limitations

- Third-party mode does not yet guarantee official Codex plugins, image
  generation, or mobile Remote support.
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
.\release.ps1 -Version 1.0.0 -DotNet "C:\path\to\dotnet.exe"
```

The installed files are placed in:

```text
%LOCALAPPDATA%\Programs\CodexProviderSwitcher
```

The installer creates:

```text
Desktop\Codex Provider Switcher.lnk
```
