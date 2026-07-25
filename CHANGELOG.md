# Changelog

All notable changes to this project are documented here.

## [1.0.0] - 2026-07-24

### Added

- Native Windows WPF interface for switching Codex between the existing
  ChatGPT login and a Responses API-compatible third-party endpoint.
- Stable `model_provider` identity so both routes use the same Codex history
  partition.
- Windows Credential Manager storage for the third-party API key.
- Automatic `config.toml` backups before every switch.
- Responses API authentication and SSE compatibility test.
- Optional Codex restart after switching.
- Desktop shortcut installer.

### Known limitations

- Third-party mode does not yet guarantee Codex official plugins, image
  generation, or mobile Remote support.
- The release is unsigned and may show a Windows SmartScreen warning.
- The third-party endpoint must implement the OpenAI Responses API with SSE
  streaming.
