# 0047. Shell research and interactive results

Proposed on 2026-11-18 following the owner's request for recurring internet research, numerical analysis and interactive phone displays.

## Decision

Extend the Worker program path from [0040](0040-worker-owned-railway-sandboxes.md) with POSIX shell scripts and per-run Dockerfiles. The latter build only inside an isolated Railway VM from an empty file context. The Worker runs the resulting image by immutable ID and records it in the run evidence. The author can choose the language and install dependencies while constructing the image. The same owner permission, immutable recipe version, isolated network, runtime isolation and cleanup apply to immediate and scheduled execution. Return self-contained interactive HTML through the authenticated run dashboard. Opening the display explicitly starts its scripts in a sandboxed frame with an opaque origin.

The custom image path allows eight minutes for building and five for execution within a fifteen-minute controller deadline. Its cleanup lease lasts sixteen minutes.

## Alternatives and consequences

A coding job can edit the mobile application, but preparing a branch, validating it and distributing an app build adds unnecessary work for a single report. Small displays can travel as ordinary program output, survive VM cleanup and open in the phone browser. Larger datasets must be reduced inside the VM to fit the output budget. Platform source changes still use draft PRs and the release pipeline owns distribution.

## Verification

Exercise custom image builds, shell dispatch, literal script handling, isolated display rendering and compatibility with existing C# programs. Live acceptance requires a configured Railway worker, a scheduled research run and opening its result on a phone.
