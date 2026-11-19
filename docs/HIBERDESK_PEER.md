# HiberDesk archive peer

GhostCode runs independently. **Settings → Allow HiberDesk** creates a versioned, CurrentUser DPAPI grant for the dedicated same-user peer. **Revoke HiberDesk access** closes the listener and removes that grant. GhostCode and HiberDesk need the same Windows account and elevation. GhostCode registers the peer and tray in its existing NativeShellKit host and consumes the pinned `0.2.0-alpha.3` NuGet feed from SDK commit `0c652d7e720309cb1f38d04b10fcb5bd102928b6`.

| Operation | Scope |
| --- | --- |
| `ghostcode.status` | Protocol version, archive counts, pending and failed uploads, last successful sync time when available, and a small queue of selected notification metadata |
| `ghostcode.open.archive` | Show the current GhostCode window and navigate to the existing Archive page |

GhostCode applies its existing notification preferences and cooldown before sharing selected `backup`, `sync_success`, `cloud`, and `integrity` records. Shared records have short lifetimes and contain no original notification text, source paths, repository names, credentials, or source content. The status and archive action use NativeShellKit's signed, request-correlated pipe protocol. The dedicated key is separate from application and browser IPC identities.
