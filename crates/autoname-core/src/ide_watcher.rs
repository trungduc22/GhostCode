//! File system watcher for Dev-C++, VS Code, and Python scratch files,
//! including `.exe` compile detection (`F11` Compile & Run in Dev-C++).

use crate::namer::Lang;
use crate::snapshot_engine;
use notify::{EventKind, RecommendedWatcher, RecursiveMode, Watcher};
use std::collections::HashMap;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc;
use std::sync::Arc;
use std::thread;
use std::time::{Duration, Instant};

#[derive(Clone, Debug)]
pub struct FileTrigger {
    pub path: PathBuf,
    pub compiled_ok: bool,
}

fn read_bytes_with_retry(path: &Path) -> Option<Vec<u8>> {
    match fs::read(path) {
        Ok(b) => Some(b),
        Err(_) => {
            thread::sleep(Duration::from_millis(60));
            fs::read(path).ok()
        }
    }
}

fn resolve_source_for_exe(exe_path: &Path) -> Option<PathBuf> {
    let dir = exe_path.parent()?;
    let stem = exe_path.file_stem()?.to_str()?;
    for ext in ["c", "cpp", "cc", "cxx"] {
        let candidate = dir.join(format!("{stem}.{ext}"));
        if candidate.is_file() && snapshot_engine::is_scratch_filename(&candidate) {
            return Some(candidate);
        }
    }
    None
}

/// Spawns a non-recursive watcher on `watched_dir`, debouncing events by 250ms
/// and invoking `on_trigger(path, raw_bytes, compiled_ok)`.
pub fn spawn_ide_watcher<F>(
    watched_dir: PathBuf,
    stop: Arc<AtomicBool>,
    mut on_trigger: F,
) -> thread::JoinHandle<()>
where
    F: FnMut(PathBuf, Vec<u8>, bool) + Send + 'static,
{
    thread::spawn(move || {
        let _ = fs::create_dir_all(&watched_dir);
        let (tx, rx) = mpsc::channel();

        let mut watcher: RecommendedWatcher = match notify::recommended_watcher(move |res| {
            let _ = tx.send(res);
        }) {
            Ok(w) => w,
            Err(_) => return,
        };

        if watcher
            .watch(&watched_dir, RecursiveMode::NonRecursive)
            .is_err()
        {
            return;
        }

        // Map of pending path -> (ready_at, compiled_ok)
        let mut pending: HashMap<PathBuf, (Instant, bool)> = HashMap::new();
        let debounce = Duration::from_millis(250);

        while !stop.load(Ordering::Relaxed) {
            match rx.recv_timeout(Duration::from_millis(120)) {
                Ok(Ok(event)) => {
                    let relevant = matches!(
                        event.kind,
                        EventKind::Create(_) | EventKind::Modify(_) | EventKind::Any
                    );
                    if !relevant {
                        continue;
                    }
                    for path in event.paths {
                        if path.is_dir() {
                            continue;
                        }
                        let ext = path
                            .extension()
                            .and_then(|e| e.to_str())
                            .map(|s| s.to_ascii_lowercase())
                            .unwrap_or_default();

                        if ext == "exe" {
                            if let Some(src) = resolve_source_for_exe(&path) {
                                let ready_at = Instant::now() + debounce;
                                let entry = pending.entry(src).or_insert((ready_at, true));
                                entry.0 = ready_at;
                                entry.1 = true;
                            }
                        } else if Lang::from_ext(&ext).is_some()
                            && snapshot_engine::is_scratch_filename(&path)
                        {
                            let ready_at = Instant::now() + debounce;
                            let entry = pending.entry(path).or_insert((ready_at, false));
                            entry.0 = ready_at;
                        }
                    }
                }
                Ok(Err(_)) => {}
                Err(mpsc::RecvTimeoutError::Timeout) => {}
                Err(mpsc::RecvTimeoutError::Disconnected) => break,
            }

            let now = Instant::now();
            let due: Vec<(PathBuf, bool)> = pending
                .iter()
                .filter(|(_, (t, _))| *t <= now)
                .map(|(p, (_, c))| (p.clone(), *c))
                .collect();

            for (path, compiled_ok) in due {
                pending.remove(&path);
                if let Some(bytes) = read_bytes_with_retry(&path) {
                    on_trigger(path, bytes, compiled_ok);
                }
            }
        }
    })
}
