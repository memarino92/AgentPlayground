# Personal assistant tooling

The application's build and tests do not require OpenCode, Codex skills, or assistant plugins. Repository conventions remain in `AGENTS.md` with a short Copilot pointer.

## User installation

| Content | User location |
| --- | --- |
| 95 reusable .NET skills | `~/.agents/skills/<name>/SKILL.md` |
| 16 OpenCode agent definitions | `~/.config/opencode/agents/` |
| OpenCode worktree plugin and helpers | `~/.config/opencode/plugins/` |
| OpenCode plugin dependencies | `~/.config/opencode/package.json` |
| Roslyn language-server settings | `~/.config/opencode/opencode.jsonc` |
| Original .NET snapshot provenance/license | `~/.config/opencode/dotnet-skills/` |

Codex and OpenCode both discover `~/.agents/skills`; no duplicate Codex installation or explicit include setting is required. OpenCode's agent and plugin formats remain specific to OpenCode. Sources checked 2026-09-07: [Codex local skill discovery](https://learn.chatgpt.com/docs/build-skills), [OpenCode skills](https://opencode.ai/docs/skills/), [global config](https://opencode.ai/docs/config/), and [agents](https://opencode.ai/docs/agents/).

Restart OpenCode and start a fresh Codex session to verify skill discovery. OpenCode was not on this shell's PATH during migration, so plugin execution and Roslyn startup were not exercised here. Skills mentioning optional MCP servers still require those integrations; moving instructions does not install those servers.

## Maintenance and rollback

The migrated skills retain the original upstream snapshot at `dotnet/skills` commit `ce75c355694bb358162464058081121645767d6f`. Update generic skills independently of application changes and preserve their licenses. Retain a copy of user configuration in personal backups/dotfiles; never commit credentials there.

The 2026-09-07 migration preserved the entire former `.opencode` directory (including uncommitted package changes and dependencies), root `opencode.jsonc`, and `.ocx` receipt under a dated `~/.config/agentplayground-tooling-backup-*` directory. That backup also contains pre-migration global config/lockfiles in `global-before/` and a `copy-manifest.json` with verified source/target hashes. It is an archive outside the global plugin discovery path.

For rollback, quit the tools, use the manifest to identify only the newly installed destinations, preserve any later edits, restore the prior global files, and restore the archived repo configuration if explicitly desired. Do not broadly delete existing user skill/config directories. The repo ignore rules prevent accidental re-vendoring of personal OpenCode tooling.
