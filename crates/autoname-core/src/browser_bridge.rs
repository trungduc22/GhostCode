//! Browser context bridge (HTTP 127.0.0.1:48123 + Win32 window title scanner)
//! and active IDE/browser process detector.

use crate::context::{ExtensionPayload, ProblemContext};
use chrono::Utc;
use serde::Serialize;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::thread;
use std::time::Duration;
use tiny_http::{Header, Method, Response, Server, StatusCode};

#[derive(Clone, Copy, Debug, Default, Serialize)]
pub struct ActiveProcesses {
    pub devcpp: bool,
    pub vscode: bool,
    pub python: bool,
    pub edge: bool,
    pub thorium: bool,
}

#[derive(Clone)]
pub struct SharedBridgeState {
    pub current_context: Arc<Mutex<Option<ProblemContext>>>,
    pub active_processes: Arc<Mutex<ActiveProcesses>>,
    pub browser_titles: Arc<Mutex<Vec<String>>>,
    pub watched_dir: String,
    pub archive_count: Arc<Mutex<usize>>,
}

impl SharedBridgeState {
    pub fn new(watched_dir: String) -> Self {
        Self {
            current_context: Arc::new(Mutex::new(None)),
            active_processes: Arc::new(Mutex::new(ActiveProcesses::default())),
            browser_titles: Arc::new(Mutex::new(Vec::new())),
            watched_dir,
            archive_count: Arc::new(Mutex::new(0)),
        }
    }

    pub fn set_context(&self, ctx: ProblemContext) {
        if let Ok(mut guard) = self.current_context.lock() {
            *guard = Some(ctx);
        }
    }

    pub fn get_context(&self) -> Option<ProblemContext> {
        self.current_context.lock().ok().and_then(|g| g.clone())
    }
}

fn cors_headers() -> Vec<Header> {
    vec![
        Header::from_bytes(&b"Access-Control-Allow-Origin"[..], &b"*"[..]).unwrap(),
        Header::from_bytes(
            &b"Access-Control-Allow-Methods"[..],
            &b"GET, POST, OPTIONS"[..],
        )
        .unwrap(),
        Header::from_bytes(
            &b"Access-Control-Allow-Headers"[..],
            &b"Content-Type"[..],
        )
        .unwrap(),
        Header::from_bytes(&b"Content-Type"[..], &b"application/json; charset=utf-8"[..]).unwrap(),
    ]
}

/// Start Tier-1 local HTTP listener (`127.0.0.1:<port>`) for the Edge/Thorium browser extension.
pub fn spawn_http_server(
    port: u16,
    state: SharedBridgeState,
    stop: Arc<AtomicBool>,
) -> thread::JoinHandle<()> {
    thread::spawn(move || {
        let addr = format!("127.0.0.1:{port}");
        let server = match Server::http(&addr) {
            Ok(s) => s,
            Err(_) => return,
        };

        while !stop.load(Ordering::Relaxed) {
            let mut req = match server.recv_timeout(Duration::from_millis(400)) {
                Ok(Some(r)) => r,
                Ok(None) => continue,
                Err(_) => break,
            };

            let url = req.url().to_string();
            let method = req.method().clone();

            if method == Method::Options {
                let mut resp = Response::from_string("").with_status_code(StatusCode(204));
                for h in cors_headers() {
                    resp.add_header(h);
                }
                let _ = req.respond(resp);
                continue;
            }

            if method == Method::Post && url.starts_with("/context") {
                let mut body = String::new();
                let _ = req.as_reader().read_to_string(&mut body);
                let now = Utc::now().timestamp();
                let ok = match serde_json::from_str::<ExtensionPayload>(&body) {
                    Ok(payload) => {
                        if let Some(ctx) = payload.into_context(now) {
                            state.set_context(ctx);
                            true
                        } else {
                            false
                        }
                    }
                    Err(_) => false,
                };
                let body_out = if ok {
                    r#"{"ok":true}"#
                } else {
                    r#"{"ok":false}"#
                };
                let status = if ok { StatusCode(200) } else { StatusCode(400) };
                let mut resp = Response::from_string(body_out).with_status_code(status);
                for h in cors_headers() {
                    resp.add_header(h);
                }
                let _ = req.respond(resp);
            } else if method == Method::Get && url.starts_with("/status") {
                let ctx = state.get_context();
                let procs = state
                    .active_processes
                    .lock()
                    .map(|p| *p)
                    .unwrap_or_default();
                let count = state.archive_count.lock().map(|c| *c).unwrap_or(0);
                let titles = state
                    .browser_titles
                    .lock()
                    .map(|t| t.clone())
                    .unwrap_or_default();
                let json = serde_json::json!({
                    "ok": true,
                    "context": ctx,
                    "processes": procs,
                    "browser_titles": titles,
                    "watched_dir": state.watched_dir,
                    "archived_count": count,
                })
                .to_string();
                let mut resp = Response::from_string(json).with_status_code(StatusCode(200));
                for h in cors_headers() {
                    resp.add_header(h);
                }
                let _ = req.respond(resp);
            } else {
                let mut resp =
                    Response::from_string(r#"{"error":"not found"}"#).with_status_code(StatusCode(404));
                for h in cors_headers() {
                    resp.add_header(h);
                }
                let _ = req.respond(resp);
            }
        }
    })
}

#[cfg(windows)]
mod win32 {
    use super::ActiveProcesses;
    use crate::context::{self, ContextSource, ProblemContext};
    use std::ffi::OsString;
    use std::os::windows::ffi::OsStringExt;
    use windows_sys::Win32::Foundation::{CloseHandle, HWND, INVALID_HANDLE_VALUE, LPARAM};
    use windows_sys::Win32::System::Diagnostics::ToolHelp::{
        CreateToolhelp32Snapshot, Process32FirstW, Process32NextW, PROCESSENTRY32W,
        TH32CS_SNAPPROCESS,
    };
    use windows_sys::Win32::System::Threading::{
        OpenProcess, QueryFullProcessImageNameW, PROCESS_QUERY_LIMITED_INFORMATION,
    };
    use windows_sys::Win32::UI::WindowsAndMessaging::{
        EnumWindows, GetWindowTextLengthW, GetWindowTextW, GetWindowThreadProcessId,
        IsWindowVisible,
    };

    pub fn detect_active_ides() -> ActiveProcesses {
        let mut procs = ActiveProcesses::default();
        unsafe {
            let snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if snap == INVALID_HANDLE_VALUE {
                return procs;
            }
            let mut entry: PROCESSENTRY32W = std::mem::zeroed();
            entry.dwSize = std::mem::size_of::<PROCESSENTRY32W>() as u32;

            if Process32FirstW(snap, &mut entry) != 0 {
                loop {
                    let len = entry
                        .szExeFile
                        .iter()
                        .position(|&c| c == 0)
                        .unwrap_or(entry.szExeFile.len());
                    let name = OsString::from_wide(&entry.szExeFile[..len])
                        .to_string_lossy()
                        .to_ascii_lowercase();
                    match name.as_str() {
                        "devcpp.exe" => procs.devcpp = true,
                        "code.exe" => procs.vscode = true,
                        "python.exe" | "pythonw.exe" => procs.python = true,
                        "msedge.exe" => procs.edge = true,
                        "thorium.exe" => procs.thorium = true,
                        _ => {}
                    }
                    if Process32NextW(snap, &mut entry) == 0 {
                        break;
                    }
                }
            }
            CloseHandle(snap);
        }
        procs
    }

    fn process_exe_name(pid: u32) -> Option<String> {
        unsafe {
            let h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid);
            if h.is_null() {
                return None;
            }
            let mut buf = [0u16; 520];
            let mut len = buf.len() as u32;
            let ok = QueryFullProcessImageNameW(h, 0, buf.as_mut_ptr(), &mut len);
            CloseHandle(h);
            if ok == 0 || len == 0 {
                return None;
            }
            let full = OsString::from_wide(&buf[..len as usize])
                .to_string_lossy()
                .to_ascii_lowercase();
            full.rsplit('\\').next().map(|s| s.to_string())
        }
    }

    struct ScanAcc {
        now: i64,
        found: Option<ProblemContext>,
        titles: Vec<String>,
    }

    unsafe extern "system" fn enum_cb(hwnd: HWND, lparam: LPARAM) -> i32 {
        if IsWindowVisible(hwnd) == 0 {
            return 1;
        }
        let len = GetWindowTextLengthW(hwnd);
        if len <= 0 || len > 512 {
            return 1;
        }
        let mut buf = vec![0u16; (len as usize) + 1];
        let copied = GetWindowTextW(hwnd, buf.as_mut_ptr(), buf.len() as i32);
        if copied <= 0 {
            return 1;
        }
        let title = OsString::from_wide(&buf[..copied as usize])
            .to_string_lossy()
            .into_owned();

        let mut pid = 0u32;
        GetWindowThreadProcessId(hwnd, &mut pid);
        let exe = process_exe_name(pid).unwrap_or_default();
        let lower_title = title.to_ascii_lowercase();
        let is_target_browser = exe == "msedge.exe"
            || exe == "thorium.exe"
            || lower_title.contains("microsoft\u{200b} edge")
            || lower_title.contains("microsoft edge")
            || lower_title.contains("- thorium");

        if !is_target_browser {
            return 1;
        }

        let acc = &mut *(lparam as *mut ScanAcc);
        let clean = context::strip_browser_suffix(&title);
        if !clean.is_empty() && !acc.titles.contains(&clean) {
            acc.titles.push(clean);
        }
        if acc.found.is_none() {
            if let Some(ctx) = context::from_window_title(&title, acc.now) {
                acc.found = Some(ctx);
            }
        }
        1
    }

    pub fn scan_browser_windows(now: i64) -> (Option<ProblemContext>, Vec<String>) {
        let mut acc = ScanAcc {
            now,
            found: None,
            titles: Vec::new(),
        };
        unsafe {
            EnumWindows(Some(enum_cb), (&mut acc as *mut ScanAcc) as LPARAM);
        }
        let ctx = acc.found.map(|mut c| {
            c.source = ContextSource::WindowTitle;
            c
        });
        (ctx, acc.titles)
    }

    pub fn scan_browser_window_title(now: i64) -> Option<ProblemContext> {
        scan_browser_windows(now).0
    }
}

#[cfg(not(windows))]
mod win32 {
    use super::ActiveProcesses;
    use crate::context::ProblemContext;

    pub fn detect_active_ides() -> ActiveProcesses {
        ActiveProcesses::default()
    }

    pub fn scan_browser_windows(_now: i64) -> (Option<ProblemContext>, Vec<String>) {
        (None, Vec::new())
    }

    pub fn scan_browser_window_title(_now: i64) -> Option<ProblemContext> {
        None
    }
}

pub use win32::{detect_active_ides, scan_browser_window_title, scan_browser_windows};

/// Immediately scan running IDE/browser processes and Edge/Thorium window titles.
pub fn refresh_browser_scan(state: &SharedBridgeState) {
    let procs = detect_active_ides();
    if let Ok(mut g) = state.active_processes.lock() {
        *g = procs;
    }

    let now = Utc::now().timestamp();
    let (win_ctx, titles) = scan_browser_windows(now);
    if let Ok(mut t) = state.browser_titles.lock() {
        *t = titles;
    }

    if let Some(win_ctx) = win_ctx {
        if let Ok(mut guard) = state.current_context.lock() {
            let should_replace = match &*guard {
                None => true,
                Some(existing) => {
                    existing.source != crate::context::ContextSource::Extension
                        || now - existing.seen_at > 30
                }
            };
            if should_replace {
                *guard = Some(win_ctx);
            }
        }
    }
}

/// Periodically scan Edge/Thorium window titles (Tier 2 fallback) and running IDEs.
pub fn spawn_win32_scanner(
    state: SharedBridgeState,
    stop: Arc<AtomicBool>,
) -> thread::JoinHandle<()> {
    thread::spawn(move || {
        while !stop.load(Ordering::Relaxed) {
            refresh_browser_scan(&state);

            for _ in 0..20 {
                if stop.load(Ordering::Relaxed) {
                    break;
                }
                thread::sleep(Duration::from_millis(100));
            }
        }
    })
}
