# Whatbox SSH tools

Deploy API and Web from the same revision. The API needs outbound DNS and TCP port 22 to the slot. Setup uses the existing integration administrator authorization and database encryption bootstrap.

1. Generate an Ed25519 key pair dedicated to this deployment, or use its existing dedicated pair. Add only the public key as a new line in the slot's `~/.ssh/authorized_keys`, preferably with the OpenSSH `restrict` option to disable forwarding and PTY allocation. Keep the existing entries. [Whatbox SSH setup](https://web-ams1.infra.whatbox.ca/wiki/SSH)
2. In **Settings → Whatbox**, enter the slot hostname and username, the verified server SHA-256 fingerprint and the deployment private key. Set the remote files directory and rTorrent socket path for the slot. Both fields accept absolute paths or paths beginning with `~/`. Save with the tools disabled. Use the fingerprint of the server host key from a trusted source or an already verified local connection.
3. Select **Test connection**. The API performs the handshake, verifies the pinned key, authenticates and runs a harmless connection check. The button uses the saved settings.
4. Enable Whatbox tools and save. In **Settings → Integrations and coach access**, enable the two Whatbox permissions for Owner and save tool access.
5. In chat, ask for Whatbox disk usage and rTorrent seeding status. Confirm both return current readings from the deployed API. A successful connection test verifies SSH, while these calls also verify remote command prerequisites.

Disk usage reports allocated bytes under the configured files directory, initially suggested as `~/files`, counting hard-linked files once within that scan. The available and total filesystem values are mount-level readings. View the provider's plan allowance and billing usage in its Manage page. [Whatbox Manage page](https://whatbox.ca/wiki/Manage_page)

rTorrent status covers the main view. A complete, active torrent counts as seeding even when its current upload rate is zero. Inactive complete and incomplete torrents have their own counts. The integration requires Python 3 and a running rTorrent instance at the configured socket path, initially suggested as `~/.config/rtorrent/socket`.

For rotation, authorize a new deployment public key, replace the private key in Settings, save, test and revoke the old public key. Blank keeps the saved key. To disable access, turn off the tools or remove the key in Settings. For server-side revocation remove the matching public key entry. Settings changes take effect on the next operation. In-flight operations use the snapshot they started with.

The application logs operation, outcome and timing. Aggregate results may be sent to the chat model and stored in the conversation. The connection credential and raw SSH responses are excluded from tool results.
