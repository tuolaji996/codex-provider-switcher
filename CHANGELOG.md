# Changelog

All notable changes to this project are documented here.

## [Unreleased]

## [1.3.5] - 2026-08-08

### Added

- A bilingual Settings switch that exposes Sol Ultra in the Codex model picker.

### Safety

- The switch updates only
  `[desktop].show-ultra-in-model-picker-slider`, with a timestamped backup,
  atomic write, read-back verification, and rollback on failure.
- Luna task agents remain on `gpt-5.6-luna / max`. The switcher never writes
  Ultra as a Responses API reasoning value, changes the selected model or
  provider, or rewrites chat history.

### Verified

- Self-tests cover enabled, disabled, and missing desktop settings, idempotent
  rewrites, CRLF preservation, sibling-key retention, and the exact pre-write
  backup.

## [1.3.4] - 2026-08-02

### Added

- A background startup check for the repository's latest stable GitHub Release.
- A bilingual Home update notice and a Settings status with manual retry.
- Offline response tests for newer, current, older, malformed, and failed
  GitHub release checks.

### Safety

- Update checks use the public GitHub API without credentials and never block
  startup, Provider switching, official authentication, or chat history.
- Release links are constructed for this repository instead of trusting an
  arbitrary URL returned in the API response.
- Updates are never downloaded or installed silently; the user opens the
  trusted GitHub Release page and chooses the asset.

## [1.3.3] - 2026-08-02

### Added

- An optional bilingual Settings action that installs the scoped
  `luna_worker` task-agent definition with `gpt-5.6-luna` and maximum reasoning.
- Managed-state detection for a missing, installed, or conflicting
  `%CODEX_HOME%\agents\luna-worker.toml` file.

### Safety

- Installing the Luna task agent does not edit Codex `config.toml`, switch the
  active provider, alter official authentication, or rewrite chat history.
- A different existing `luna-worker.toml` is reported as a conflict and is never
  overwritten; unrelated agent definitions are left untouched.

### Boundaries

- Third-party routes must expose `gpt-5.6-luna` for this task agent to work.
  The switcher does not substitute another model when that model is unavailable.

## [1.3.2] - 2026-07-26

### Added

- A six-step first-run guide with an explicit read-only environment check and a
  final confirmation page before any provider setting is validated or applied.
- Windows CI for Release publishing, self-tests, bilingual-copy validation,
  required artifact checks, and a redacted repository secret scan.
- A pure embedded-navigation policy with regression coverage for SuiXiang,
  Tencent CAPTCHA hosts, external links, malformed URLs, and unsafe schemes.

### Changed

- The optional SuiXiang WebView now serializes initialization and data clearing,
  reports navigation and process failures, provides a 20-second soft timeout,
  and keeps retry or manual API-key entry available.
- External HTTPS pages open only after a user-initiated navigation. Passive
  redirects, malformed URLs, and unsafe protocols are blocked.
- The application icon is now a clean nine-size, 32-bit Windows icon instead of
  a single 256-pixel, 16-color frame that became muddy in title bars.

### Verified

- Existing v1.3 official and third-party settings migration, fresh and malformed
  settings, managed credential targets, and missing token-broker credentials
  have explicit self-test coverage.

### Boundaries

- The embedded page does not inspect DOM, passwords, CAPTCHA data, cookies, or
  infer whether sign-in succeeded. Automatic SuiXiang API-key creation still
  requires an approved provider API, and real CAPTCHA remains an E2E test item.

## [1.3.1] - 2026-07-26

### Added

- A first-run bilingual setup flow with three parallel choices: optional
  SuiXiang sign-in, a manually configured OpenAI-compatible service, or
  official Codex only.
- An isolated Microsoft Edge WebView2 profile for the optional SuiXiang
  sign-in page. The user completes CAPTCHA and any login interaction directly;
  the application does not inspect passwords, CAPTCHA data, or cookies.
- A `Run setup again` action in Settings that reopens the guide without clearing
  data merely by opening or cancelling it, plus an explicit action to clear the
  isolated SuiXiang WebView sign-in data.
- Per-provider managed Credential Manager targets and a credential-target-aware
  Codex token broker, so new custom providers do not share the legacy key slot.

### Changed

- The Home page now centers the current route and one dynamic daily action:
  connect a provider, switch to the configured provider, or return to official
  Codex.
- Existing v1.3 settings migrate in place without forcing users through setup;
  their saved SuiXiang credential target is retained.
- Generated third-party `config.toml` uses the selected profile's managed
  credential target while preserving `model_provider = "OpenAI"`.
- A Base URL change now requires an explicitly supplied new API key unless the
  endpoint already has its own saved provider profile; an old key is never sent
  to a newly entered service.

### Fixed

- Malformed local settings are quarantined with a timestamp before new defaults
  are created, rather than being silently overwritten.

### Boundaries

- SuiXiang sign-in is optional. The current release still requires the user to
  paste a newly created API key after login. Automatic key retrieval or creation
  requires an approved provider authorization and key-management API.

### Verified

- The Windows .NET 8 build publishes both native executables and passes the
  migration, credential-target, token-broker, and configuration round-trip
  self-tests.
- The published WPF application starts as a responsive native Windows window.
- The release package requires and includes `WebView2Loader.dll` for the
  optional embedded sign-in flow.

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
