# 0006: Keep reusable assistant tooling in user configuration

- Status: Accepted; migrated locally
- Recorded: 2026-09-07
- Evidence: Maintainer request; former `.opencode/dotnet-skills/README.md` recorded upstream `dotnet/skills` commit `ce75c355694bb358162464058081121645767d6f`

## Context and decision

The repository contained 95 generic .NET skills, 16 OpenCode-specific agent definitions, a worktree plugin, package dependencies, and language-server configuration. These describe the maintainer's tools, not the application's domain or build prerequisites.

Keep repository conventions in `AGENTS.md` and a short Copilot pointer. Store reusable skills in the shared user `~/.agents/skills` location, which Codex and OpenCode discover. Keep OpenCode agents/plugins/dependencies/Roslyn settings in `~/.config/opencode`. Preserve upstream provenance and licenses with the local installation. Do not require contributors to install this personal tooling to build or test the application.

## Alternatives and consequences

Vendoring tool collections pins them with the application but creates large unrelated diffs and makes the repo assistant-specific. Separate copies per tool are easy to drift. A shared skill directory gives one installation; tool-specific agents and executable plugins retain their host-specific formats. OpenCode agents are not automatically converted into Codex agents.

Machine configuration is no longer recovered by cloning this application. Back it up separately or manage it in personal dotfiles. Future truly repo-specific skills can be added deliberately; this does not prohibit them.

## Delivery and verification

Migrated 95 skill folders and 16 OpenCode agents, plus plugin/provenance files; 278 copied files were SHA-256 verified before moving the repo originals to a dated user backup. Preserved local package edits and lockfiles. Global plugin dependencies installed with lifecycle scripts disabled. Skills have unique declared names; global JSON settings parse. Interactive discovery/plugin/LSP startup still requires restarting the tools and checking a fresh session. See [personal tooling](../runbooks/personal-tooling.md).
