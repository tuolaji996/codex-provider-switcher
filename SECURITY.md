# Security

## Reporting a vulnerability

Use GitHub's private vulnerability reporting feature when it is available for
this repository. Do not put API keys, `auth.json`, access tokens, private
configuration, or personal chat history in a public issue.

## Credential handling

The application stores third-party API keys in Windows Credential Manager.
`config.toml` contains a command-backed token reference rather than the key
itself. A key pasted into a chat, issue, log, screenshot, or commit must be
revoked and replaced.

Third-party providers receive the prompts, code excerpts, and tool context sent
through their endpoint. Review the provider's privacy and retention policy
before use.
