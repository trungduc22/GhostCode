//! UI-independent engine library for AutoName Code.

pub mod analyzer;
pub mod browser_bridge;
pub mod clipboard;
pub mod config;
pub mod context;
pub mod drive_sync;
pub mod ide_watcher;
pub mod namer;
pub mod organizer;
pub mod similarity;
pub mod snapshot_engine;

use crate::browser_bridge::{ActiveProcesses, SharedBridgeState};
use crate::config::{AppConfig, UiLanguage};
use crate::context::{ContextSource, Platform, ProblemContext};
use crate::drive_sync::{ArchiveRecord, ArchiveState, SyncReport};
use crate::snapshot_engine::{Snapshot, SnapshotEngine};
use chrono::Utc;
use std::fs;
use std::io;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::Duration;

#[derive(Clone, Debug, Default)]
pub struct EngineSnapshotView {
    pub config: AppConfig,
    pub active_context: Option<ProblemContext>,
    pub active_processes: ActiveProcesses,
    pub browser_titles: Vec<String>,
    pub recent_records: Vec<ArchiveRecord>,
    pub latest_ready_path: Option<PathBuf>,
    pub total_archived: usize,
    pub pending_sync: usize,
    pub online: bool,
    pub status_message: String,
}

struct EngineInner {
    config: AppConfig,
    config_path: PathBuf,
    archive_state: ArchiveState,
    snapshot_engine: SnapshotEngine,
    online: bool,
    status_message: String,
}

/// Thread-safe handle driving background HTTP bridge, Win32 scanner, file watcher, and Drive sync.
#[derive(Clone)]
pub struct EngineHandle {
    inner: Arc<Mutex<EngineInner>>,
    bridge: SharedBridgeState,
    stop: Arc<AtomicBool>,
}

impl EngineHandle {
    pub fn start(config_path: PathBuf) -> Self {
        let config = AppConfig::load_or_default(&config_path);
        let archive_state = ArchiveState::load_or_default(&config.state_file);
        let total = archive_state.records.len();

        let bridge = SharedBridgeState::new(config.watched_dir.to_string_lossy().into_owned());
        if let Ok(mut c) = bridge.archive_count.lock() {
            *c = total;
        }

        let inner = Arc::new(Mutex::new(EngineInner {
            config: config.clone(),
            config_path,
            archive_state,
            snapshot_engine: SnapshotEngine::new(),
            online: drive_sync::check_internet(),
            status_message: String::new(),
        }));

        let stop = Arc::new(AtomicBool::new(false));

        // 1. HTTP bridge for Edge & Thorium extension
        browser_bridge::spawn_http_server(config.http_port, bridge.clone(), stop.clone());

        // 2. Win32 window title scanner + running IDE/browser detector
        browser_bridge::spawn_win32_scanner(bridge.clone(), stop.clone());

        // 3. File system watcher for watched_dir
        let inner_w = inner.clone();
        let bridge_w = bridge.clone();
        ide_watcher::spawn_ide_watcher(
            config.watched_dir.clone(),
            stop.clone(),
            move |path, bytes, compiled_ok| {
                Self::process_file_change(&inner_w, &bridge_w, &path, bytes, compiled_ok);
            },
        );

        // 4. Periodic Google Drive background sync worker (every 15 seconds)
        let inner_s = inner.clone();
        let stop_s = stop.clone();
        thread::spawn(move || {
            let mut ticks = 0u32;
            while !stop_s.load(Ordering::Relaxed) {
                thread::sleep(Duration::from_millis(500));
                ticks += 1;
                if ticks % 30 == 0 {
                    let (cfg, mut st) = {
                        let g = inner_s.lock().unwrap();
                        (g.config.clone(), g.archive_state.clone())
                    };
                    let report = drive_sync::sync_pending_to_drive(&cfg, &mut st);
                    let mut g = inner_s.lock().unwrap();
                    g.archive_state = st;
                    g.online = report.online;
                    if report.uploaded > 0 {
                        g.status_message = format!("Synced {} file(s) to Drive", report.uploaded);
                    }
                }
            }
        });

        Self {
            inner,
            bridge,
            stop,
        }
    }

    fn process_file_change(
        inner: &Arc<Mutex<EngineInner>>,
        bridge: &SharedBridgeState,
        path: &Path,
        bytes: Vec<u8>,
        compiled_ok: bool,
    ) {
        let now = Utc::now().timestamp();
        let ctx = bridge.get_context();

        let (cfg, mut st, action) = {
            let mut g = inner.lock().unwrap();
            let action = g
                .snapshot_engine
                .on_file_event(path, bytes, ctx, compiled_ok, now);
            (g.config.clone(), g.archive_state.clone(), action)
        };

        let mut changed = false;
        let mut last_msg = None;

        if let Some(prev_snap) = action.archive_previous {
            let is_ptit = prev_snap
                .context
                .as_ref()
                .map(|c| c.platform == Platform::Ptit)
                .unwrap_or(false);
            if let Ok(rec) = drive_sync::write_archived_file(&cfg, &mut st, &prev_snap, is_ptit) {
                last_msg = Some(format!("Archived previous: {}", rec.file_name));
                changed = true;
            }
        }

        if let Some(curr_snap) = action.hot_update_current {
            let is_ptit = curr_snap
                .context
                .as_ref()
                .map(|c| c.platform == Platform::Ptit)
                .unwrap_or(false);
            // In PTIT mode (or when a browser context is active), always hot-create/update immediately
            if is_ptit || curr_snap.context.is_some() {
                if let Ok(rec) = drive_sync::write_archived_file(&cfg, &mut st, &curr_snap, is_ptit)
                {
                    last_msg = Some(format!("Hot saved: {}", rec.file_name));
                    changed = true;
                }
            }
        }

        if changed {
            let total = st.records.len();
            if let Ok(mut c) = bridge.archive_count.lock() {
                *c = total;
            }
            let mut g = inner.lock().unwrap();
            g.archive_state = st;
            if let Some(msg) = last_msg {
                g.status_message = msg;
            }
        }
    }

    pub fn stop(&self) {
        self.stop.store(true, Ordering::Relaxed);
    }

    pub fn view(&self) -> EngineSnapshotView {
        let g = self.inner.lock().unwrap();
        let active_context = self.bridge.get_context();
        let active_processes = self
            .bridge
            .active_processes
            .lock()
            .map(|p| *p)
            .unwrap_or_default();
        let browser_titles = self
            .bridge
            .browser_titles
            .lock()
            .map(|t| t.clone())
            .unwrap_or_default();

        let total_archived = g.archive_state.records.len();
        let pending_sync = g.archive_state.pending_count();
        let mut recent_records: Vec<ArchiveRecord> = g
            .archive_state
            .records
            .iter()
            .rev()
            .take(10)
            .cloned()
            .collect();
        let latest_ready_path = g
            .archive_state
            .records
            .iter()
            .rev()
            .find_map(|r| r.ready_path.clone());

        // Also guarantee fresh status on screen
        if recent_records.is_empty() && !g.archive_state.records.is_empty() {
            recent_records = g.archive_state.records.clone();
        }

        EngineSnapshotView {
            config: g.config.clone(),
            active_context,
            active_processes,
            browser_titles,
            recent_records,
            latest_ready_path,
            total_archived,
            pending_sync,
            online: g.online,
            status_message: g.status_message.clone(),
        }
    }

    pub fn set_language(&self, lang: UiLanguage) {
        let mut g = self.inner.lock().unwrap();
        g.config.language = lang;
        let _ = g.config.save(&g.config_path);
    }

    pub fn toggle_language(&self) -> UiLanguage {
        let mut g = self.inner.lock().unwrap();
        g.config.language = match g.config.language {
            UiLanguage::Vi => UiLanguage::En,
            UiLanguage::En => UiLanguage::Vi,
        };
        let lang = g.config.language;
        let _ = g.config.save(&g.config_path);
        lang
    }

    pub fn toggle_dark_mode(&self) -> bool {
        let mut g = self.inner.lock().unwrap();
        g.config.dark_mode = !g.config.dark_mode;
        let dm = g.config.dark_mode;
        let _ = g.config.save(&g.config_path);
        dm
    }

    pub fn toggle_auto_copy(&self) -> bool {
        let mut g = self.inner.lock().unwrap();
        g.config.auto_copy_ready_path = !g.config.auto_copy_ready_path;
        let v = g.config.auto_copy_ready_path;
        let _ = g.config.save(&g.config_path);
        v
    }

    pub fn organize_existing(&self) -> io::Result<Vec<ArchiveRecord>> {
        let (cfg, mut st) = {
            let g = self.inner.lock().unwrap();
            (g.config.clone(), g.archive_state.clone())
        };
        let recs = organizer::organize_existing_files(&cfg, &mut st)?;
        let count = recs.len();
        let total = st.records.len();
        if let Ok(mut c) = self.bridge.archive_count.lock() {
            *c = total;
        }
        let mut g = self.inner.lock().unwrap();
        g.archive_state = st;
        g.status_message = format!("Organized {count} existing file(s)");
        Ok(recs)
    }

    pub fn sync_now(&self) -> SyncReport {
        let (cfg, mut st) = {
            let g = self.inner.lock().unwrap();
            (g.config.clone(), g.archive_state.clone())
        };
        let report = drive_sync::sync_pending_to_drive(&cfg, &mut st);
        let mut g = self.inner.lock().unwrap();
        g.archive_state = st;
        g.online = report.online;
        g.status_message = if report.uploaded > 0 {
            format!("Uploaded {} file(s) to Drive", report.uploaded)
        } else if let Some(err) = &report.last_error {
            format!("Sync error: {err}")
        } else if report.online {
            "All files already synced".into()
        } else {
            "Offline (queued)".into()
        };
        report
    }

    pub fn toggle_drive_enabled(&self) -> bool {
        let mut g = self.inner.lock().unwrap();
        g.config.drive_enabled = !g.config.drive_enabled;
        let v = g.config.drive_enabled;
        let _ = g.config.save(&g.config_path);
        v
    }

    pub fn force_scan_now(&self) {
        browser_bridge::refresh_browser_scan(&self.bridge);
    }

    pub fn set_manual_context(&self, platform: Platform, code: &str, title: &str) {
        let clean_code = context::sanitize_code(code).unwrap_or_else(|| "CUSTOM".into());
        let ctx = ProblemContext {
            platform,
            code: clean_code,
            title: title.trim().to_string(),
            url: String::new(),
            source: ContextSource::Manual,
            seen_at: Utc::now().timestamp(),
        };
        self.bridge.set_context(ctx);
    }

    pub fn copy_latest_ready_path(&self) -> Option<PathBuf> {
        let g = self.inner.lock().unwrap();
        let path = g
            .archive_state
            .records
            .iter()
            .rev()
            .find_map(|r| r.ready_path.clone())?;
        let _ = clipboard::set_clipboard_text(&path.to_string_lossy());
        Some(path)
    }

    pub fn copy_latest_source_code(&self) -> Option<String> {
        let g = self.inner.lock().unwrap();
        let rec = g
            .archive_state
            .records
            .iter()
            .rev()
            .find(|r| r.ready_path.as_ref().is_some_and(|p| p.exists()) || r.archive_path.exists())?;
        let path = rec
            .ready_path
            .as_ref()
            .filter(|p| p.exists())
            .unwrap_or(&rec.archive_path);
        let raw = fs::read(path).ok()?;
        let stripped = namer::strip_header(&raw);
        let code_text = String::from_utf8_lossy(stripped).into_owned();
        let _ = clipboard::set_clipboard_text(&code_text);
        Some(rec.file_name.clone())
    }

    pub fn set_status_message(&self, msg: String) {
        if let Ok(mut g) = self.inner.lock() {
            g.status_message = msg;
        }
    }

    pub fn simulate_problem(
        config: &AppConfig,
        state: &mut ArchiveState,
        platform: Platform,
        code: &str,
        title: &str,
        source_file: &Path,
    ) -> io::Result<ArchiveRecord> {
        let raw_bytes = fs::read(source_file)?;
        let text = String::from_utf8_lossy(&raw_bytes).into_owned();
        let lang = source_file
            .extension()
            .and_then(|e| e.to_str())
            .and_then(namer::Lang::from_ext)
            .unwrap_or(namer::Lang::Cpp);
        let now = Utc::now().timestamp();
        let upper_code = code.to_ascii_uppercase();
        let url = match platform {
            Platform::Ptit => format!("https://code.ptit.edu.vn/student/question/{upper_code}"),
            Platform::Codeforces => {
                let short = upper_code.strip_prefix("CF_").unwrap_or(&upper_code);
                format!("https://codeforces.com/problemset/problem/{short}")
            }
            _ => String::new(),
        };
        let ctx = ProblemContext {
            platform,
            code: upper_code,
            title: title.to_string(),
            url,
            source: ContextSource::Manual,
            seen_at: now,
        };
        let snap = Snapshot {
            source_path: source_file.to_path_buf(),
            content: raw_bytes,
            text: text.clone(),
            norm_hash: similarity::normalized_hash(&text),
            lang,
            context: Some(ctx),
            compiled_ok: true,
            updated_at: now,
        };
        drive_sync::write_archived_file(config, state, &snap, platform == Platform::Ptit)
    }

    pub fn simulate_ptit(
        config: &AppConfig,
        state: &mut ArchiveState,
        code: &str,
        title: &str,
        source_file: &Path,
    ) -> io::Result<ArchiveRecord> {
        Self::simulate_problem(config, state, Platform::Ptit, code, title, source_file)
    }
}
