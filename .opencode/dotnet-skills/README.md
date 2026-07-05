# dotnet/skills Vendor Snapshot

This repo vendors the stable skills and agents from the official `dotnet/skills` repository so opencode can auto-discover and use them from project-local `.opencode` paths.

Upstream repository: `https://github.com/dotnet/skills`
Pinned commit: `ce75c355694bb358162464058081121645767d6f`
License: MIT

Included plugin families:

- `dotnet`
- `dotnet-advanced`
- `dotnet-ai`
- `dotnet-aspnetcore`
- `dotnet-blazor`
- `dotnet-data`
- `dotnet-diag`
- `dotnet-maui`
- `dotnet-msbuild`
- `dotnet-nuget`
- `dotnet-template-engine`
- `dotnet-test`
- `dotnet-test-migration`
- `dotnet-upgrade`
- `dotnet11`

Excluded plugin families:

- `dotnet-experimental`

Imported locations:

- Skills: `.opencode/skills/*`
- Agents: `.opencode/agents/*.md`

Notes:

- Skill directories were copied whole so sibling prompt, example, and extension files remain available.
- Agents were converted from upstream `*.agent.md` format into opencode project agent files.
- Upstream agent references to `search` and `execute` were not rewritten inline; imported agent files include a short compatibility note mapping them to opencode `glob`/`grep` and `bash`.
- The official `plugins/dotnet/lsp.json` shape was adapted to opencode's project `lsp` config in `opencode.jsonc`.

Refresh procedure:

1. Clone or update `https://github.com/dotnet/skills`.
2. Copy stable plugin `skills/` directories into `.opencode/skills/`.
3. Convert stable plugin `agents/*.agent.md` files into `.opencode/agents/*.md` with opencode frontmatter.
4. Update the pinned commit in this file.
5. Restart opencode so it reloads config, skills, agents, and LSP settings.
