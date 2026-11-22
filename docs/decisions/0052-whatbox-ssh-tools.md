# 0052 Whatbox SSH tools

- Status: Proposed design with local implementation. Railway acceptance pending.
- Recorded: 2026-11-22

## Context

PersonalAgent needs to answer questions about seedbox storage and rTorrent without relying on a workstation's SSH identity. Whatbox provides public-key SSH access. Its Manage page reports the provider's plan usage, while the mounted filesystem may include other accounts.

## Decision

The API owns the Whatbox client and opens a pinned SSH.NET connection for each operation. Settings are deployment-administrator-only, stored as one encrypted versioned PostgreSQL row, and read per operation. The deployment has a dedicated key. Two Owner tool permissions default to off. The API selects the host, port and fixed remote operations from configuration and code. The configuration test also works while the chat tools are disabled.

Disk usage measures allocated bytes under the administrator-configured files directory and reports available and total filesystem capacity as separate fields. Seeding status queries the administrator-configured local rTorrent SCGI socket and returns counts and upload rate. Torrent names, paths and raw responses stay on the server. The API records operation, duration and outcome.

## Alternatives and consequences

A generic SSH function would grant the model shell operations beyond this feature's scope. An agent on the workstation would depend on the workstation being available. The chosen approach requires API egress on port 22, a trusted server fingerprint, and Python 3 on the slot. rTorrent must expose the expected local socket for seeding status. A dedicated key allows independent revocation. The key authenticates as the slot account, so its material requires the same protection as other account credentials.

See the [Whatbox runbook](../runbooks/whatbox.md) for setup and verification.
