# Changelog

All notable changes to this project are documented here.

## [1.3.0] - 2026-07-25

### Added

- A native Windows navigation workspace with Home, Providers, Diagnostics,
  Backups, and Settings views.
- Persistent Light, Dark, and System appearance modes.
- A dense, read-only backup table with refresh and folder-opening actions.
- Responsive icon-only navigation at narrow window widths.

### Changed

- Provider switching and shared-history health are now visible from the Home
  view, while endpoint and credential management have a dedicated view.
- Capability results now use semantic status indicators alongside their full
  text.
- Mobile Remote guidance now opens official Codex and correctly points initial
  pairing to the official **Set up Remote** entry instead of treating SSH
  Connections as phone pairing.
- The local build output is now a complete install source, including current
  documentation and license files.

### Fixed

- Upgrade installation now stages and replaces the application directory,
  preventing stale README or CHANGELOG files from older releases.
- Theme-dependent colors now update dynamically instead of remaining tied to
  the previous dark-only palette.

### Verified

- Existing provider, stable-history, SSE, tool-calling, image, host, credential,
  and configuration round-trip self-tests continue to pass.
- Light and Dark appearances, Chinese and English, all five views, and the
  responsive `680 x 520` layout were checked in the running Windows app.
- Language and appearance changes leave Codex provider configuration, official
  authentication, credentials, sessions, and backups untouched.

## [1.2.0] - 2026-07-25

### Added

- A `CHN / ENG` language selector in the main window.
- Complete Chinese and English text for labels, status messages, dialogs,
  provider checks, host diagnostics, and validation errors.
- Persistent language selection in the existing local `settings.json`.

### Changed

- The header now wraps cleanly at the supported minimum window width.
- Third-party privacy and key status text now use the full content width, with
  provider actions placed on a separate responsive row.

### Verified

- Existing provider, history, SSE, tool-calling, image, host, and credential
  self-tests continue to pass.
- Chinese and English layouts were checked at the default size and at the
  `680 x 520` minimum size.
- Language selection was verified across application restarts.

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
