# Changelog

All notable changes to this project are documented here.

## [1.1.0] - 2026-07-24

### Added

- Official host diagnostics for the existing ChatGPT login and the Apps,
  plugins, Remote, and image-generation feature flags.
- A strict two-request function-calling probe that verifies both
  `function_call` output and the tool-result round trip required by plugins.
- A real `/v1/images/generations` probe matching Codex's current image backend.
  It saves a verified PNG, JPEG, or WebP result under the local Diagnostics
  folder.
- A GUI shortcut to the official Codex Connections page used for mobile Remote
  pairing.

### Changed

- Responses compatibility testing now requires a complete, valid SSE response
  instead of accepting the first non-empty event.
- Restarting Codex no longer terminates every WSL `codex app-server` process,
  avoiding the previous blanket cleanup across unrelated WSL tasks.
- When third-party mode is already active, the GUI reads the current endpoint
  and model from `config.toml`.
- Successful tool and image checks persist across launches and are tied to the
  exact endpoint/model fingerprint, preventing stale results after a provider
  change.

### Verified

- Official ChatGPT login and the Apps, plugins, Remote, and image-generation
  host flags remain enabled while the configured third-party route is active.
- The configured third-party API completed Responses text streaming, a
  function-call/tool-output round trip, and an actual Images API request.
- The generated diagnostic PNG was decoded, signature-checked, saved, and
  visually inspected.

### Boundaries

- Each plugin can still require its own OAuth connection and permission
  approval.
- Mobile Remote pairing must be completed in the official ChatGPT desktop and
  mobile apps with the same account and workspace. It cannot be completed by
  this utility alone.
- A requested ChatGPT desktop-app restart can briefly disconnect Remote, but
  the switcher does not sign out or delete device pairings.
- The release remains unsigned and may show a Windows SmartScreen warning.

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
