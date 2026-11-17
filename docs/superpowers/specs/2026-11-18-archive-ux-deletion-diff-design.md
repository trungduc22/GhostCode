# GhostCode Archive UX, Safe Deletion & GitHub-Style Diff — Design Spec

## Scope
Transform **Kho lưu trữ / Archive** into a clean, fast, GitHub-style code history browser with safe two-level deletion, recoverable 30-day Trash, deletion-aware cloud tombstones, simplified search, single horizontal action toolbar with overflow, minimal metadata line + details dialog, short semantic storage labels, and full-width Unified & Side-by-Side Diff.

## Architecture Decisions
1. **Non-Destructive Additive SQLite Trash & Tombstones (`SqliteArchiveStore` + `LosslessArchiver`)**
   - Add `is_deleted`, `deleted_at`, `trash_batch_id`, `delete_scope`, `deleted_origin_device` columns cleanly to `archive_versions` via idempotent `ALTER TABLE` migrations (`EnsureArchiveVersionColumnsAsync`).
   - Add `archive_tombstones` table tracking deleted `Version` and `Problem` records (`id`, `entity_type`, `version_id`, `problem_key`, `source_path`, `content_sha256`, `drive_file_id`, `delete_remote`, `remote_delete_state`, `attempt_count`, `last_error`, `deleted_at`, `updated_at`, `device_name`, `expires_at`).
   - All normal archive queries filter `is_deleted = 0`; version numbering continues from `COALESCE(MAX(version_num), 0) + 1` across all rows so deleting `v2` leaves `v1` and `v3` intact with unchanged IDs and never reuses `v2`.
   - Local deletion (`deleteRemote = false`, default for normal confirmation & `Shift + Delete`) moves only GhostCode-managed snapshots into `.ghostcode/trash/<batchId>/`, clears stale `ready_path` if active, cancels `upload_queue` resurrection (`Pending`/`Uploading`/`Failed` marked `Cancelled`), records a local tombstone so cloud sync & directory reconciliation never re-create deleted items, and leaves user working files (e.g., `Untitled1.cpp`) 100% untouched.
   - Watcher suppression: if `source_path` still matches a tombstone's `content_sha256`, `LosslessArchiver` suppresses duplicate re-archiving of unchanged deleted content; any future legitimate edit (`newSha256 != content_sha256`) archives normally as the next version.

2. **Cloud Deletion-Aware Reconciliation (`ICloudArchiveProvider`, `GoogleDriveService`, `OneDriveService`, `CloudManifestCodec`)**
   - Add `Task<bool> DeleteRemoteFileAsync(string remoteId, CancellationToken ct = default)` on `ICloudArchiveProvider`.
   - Remote cloud objects are deleted **only** when explicitly requested (`deleteRemote = true`).
   - `CloudManifestCodec` v2 adds optional `tombstones` array; remote tombstones are published only for explicit remote deletes and respect conflict safety (`updated_at > deleted_at` / newer edits on other devices are never silently destroyed).
   - Pending remote deletes retry safely with failure tracking and accurate diagnostics if offline or API disabled.

3. **Archive UX Layout (`ArchivePage.xaml`, `ArchivePage.xaml.cs`, `MainViewModel.cs`)**
   - Single compact inline filter row: embedded `AutoSuggestBox` with icon & clear button (Enter submits immediately, 180 ms debounced in-memory filter, zero keystroke DB scans), inline Platform & Status ComboBoxes, Pull Cloud, and `Thùng rác (N)`. Distinct from `Ctrl+K` Global Search in `MainWindow.xaml`.
   - Minimal 1-line version metadata (`v2 · 21:14 · Cục bộ`) + `[Chi tiết]` button opening a clean metadata dialog (Original filename, Source path, Archive path, SHA-256, File size, Timestamp, Version ID, Problem identity, Sync state, with Copy buttons).
   - Short status labels (`Cục bộ`, `Đã đồng bộ`, `Chờ đồng bộ`, `Chỉ đám mây`, `Lỗi đồng bộ` / English equivalents) with semantic dots and full provider tooltips.
   - Single horizontal action toolbar: `[▶ Chạy] [Sao chép ▾] [Mở trong IDE ▾] [So sánh] [Khôi phục]` + far-right `[⋯]` adaptive overflow + separated muted `[🗑]` icon button (supports native `Shift + Click` instant delete + Undo InfoBar).
   - Full-width GitHub-style Diff mode: hides Runner pane (`Column 2`), spans Diff workspace wide, provides independent `Từ [v ▼] → Đến [v ▼]`, `+N thêm −M xóa ~K sửa`, Unified & Side-by-Side virtualized `ListView`s with Light/Dark contrast strips + line numbers + change-hunk navigation (`Chuyển thay đổi trước` / `Chuyển thay đổi tiếp`), single-version / identical-version / empty-diff guards, and immediate buffer release on `Đóng so sánh`.
