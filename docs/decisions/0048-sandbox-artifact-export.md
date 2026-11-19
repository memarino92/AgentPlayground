# 0048. Persistent sandbox artifacts

Proposed on 2026-11-19 following the owner's request to export complete artifacts from sandbox images.

The Worker collects binary and text files from a dedicated output directory after successful execution. A private ZIP and a manifest are committed with the existing durable sandbox result in PostgreSQL. Run access and successful step completion govern downloads. The agent can inspect file metadata and the owner can download individual files or the whole directory tree.

Passing files through stdout would consume the diagnostic budget. A separate object store could support larger outputs but would introduce credentials and lifecycle management. This implementation budgets 64 MiB of file content and 512 files per step. The ZIP stays with the sandbox result after VM cleanup. Revisit storage and retention as recurring jobs accumulate data.

Local Docker verification retained an image-baked HTML file and a 128 KiB runtime binary after container removal. Adapter, Worker, PostgreSQL and dashboard tests cover streamed transfer, byte fidelity, export rejection, replay and download links. A hosted export and download after Railway cleanup remain live acceptance checks.
