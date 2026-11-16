//! Archive writer, persistent offline queue (`archive_state.json`), and Google Drive sync via `gdrive` CLI.

use crate::analyzer;
use crate::clipboard;
use crate::config::AppConfig;
use crate::context::Platform;
use crate::namer::{self, Lang};
use crate::similarity;
use crate::snapshot_engine::Snapshot;
use chrono::Local;
use serde::{Deserialize, Serialize};
use std::fs;
use std::io;
use std::net::{SocketAddr, TcpStream};
use std::path::{Path, PathBuf};
use std::process::Command;
use std::time::Duration;

#[cfg(windows)]
use std::os::windows::process::CommandExt;

#[cfg(windows)]
const CREATE_NO_WINDOW: u32 = 0x0800_0000;

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct ArchiveRecord {
    pub id: String,
    pub file_name: String,
    pub archive_path: PathBuf,
    pub ready_path: Option<PathBuf>,
    pub source_path: PathBuf,
    pub problem_code: Option<String>,
    pub problem_title: Option<String>,
    pub platform: String,
    pub norm_hash: u64,
    pub created_at: i64,
    pub synced_to_drive: bool,
    pub drive_file_id: Option<String>,
    pub last_sync_error: Option<String>,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(default)]
pub struct ArchiveState {
    pub drive_root_folder_id: Option<String>,
    pub records: Vec<ArchiveRecord>,
}

impl ArchiveState {
    pub fn load_or_default(path: &Path) -> Self {
        match fs::read_to_string(path) {
            Ok(data) => serde_json::from_str(&data).unwrap_or_default(),
            Err(_) => Self::default(),
        }
    }

    pub fn save(&self, path: &Path) -> io::Result<()> {
        if let Some(parent) = path.parent() {
            if !parent.as_os_str().is_empty() {
                fs::create_dir_all(parent)?;
            }
        }
        let json = serde_json::to_string_pretty(self)
            .map_err(|e| io::Error::new(io::ErrorKind::Other, e))?;
        fs::write(path, json)
    }

    pub fn pending_count(&self) -> usize {
        self.records.iter().filter(|r| !r.synced_to_drive).count()
    }
}

pub fn check_internet() -> bool {
    let targets = ["8.8.8.8:53", "1.1.1.1:53"];
    for t in targets {
        if let Ok(addr) = t.parse::<SocketAddr>() {
            if TcpStream::connect_timeout(&addr, Duration::from_millis(1500)).is_ok() {
                return true;
            }
        }
    }
    false
}

/// Choose a collision-free target file path inside `dir` for `(stem, ext)`.
/// If `dir/stem.ext` already exists and holds the same solution (same `norm_hash` or
/// `similarity >= 0.50`), returns that path to update it in-place.
/// Otherwise appends `_v2`, `_v3`, ... so distinct older solutions are never clobbered.
fn resolve_target_path(
    dir: &Path,
    stem: &str,
    ext: &str,
    new_text: &str,
    new_hash: u64,
    lang: Lang,
) -> (PathBuf, String) {
    let mut ver = 1usize;
    loop {
        let candidate_name = if ver == 1 {
            format!("{stem}.{ext}")
        } else {
            format!("{stem}_v{ver}.{ext}")
        };
        let candidate_path = dir.join(&candidate_name);
        if !candidate_path.exists() {
            return (candidate_path, candidate_name);
        }
        if let Ok(existing_bytes) = fs::read(&candidate_path) {
            let stripped = namer::strip_header(&existing_bytes);
            let existing_text = String::from_utf8_lossy(stripped);
            if similarity::normalized_hash(&existing_text) == new_hash
                || similarity::similarity(&existing_text, new_text, lang) >= 0.50
            {
                return (candidate_path, candidate_name);
            }
        }
        ver += 1;
        if ver > 99 {
            return (candidate_path, candidate_name);
        }
    }
}

/// Write an archived snapshot to `CodeArchive/...` (and `CodePTIT_Ready/...` when PTIT hot mode),
/// updating `state` and persisting `archive_state.json`.
pub fn write_archived_file(
    config: &AppConfig,
    state: &mut ArchiveState,
    snap: &Snapshot,
    is_ptit_hot: bool,
) -> io::Result<ArchiveRecord> {
    write_archived_file_in_subdir(config, state, snap, is_ptit_hot, None)
}

pub fn write_archived_file_in_subdir(
    config: &AppConfig,
    state: &mut ArchiveState,
    snap: &Snapshot,
    is_ptit_hot: bool,
    free_subdir_override: Option<&str>,
) -> io::Result<ArchiveRecord> {
    let non_utf8 = std::str::from_utf8(&snap.content).is_err();
    let source_stem = snap
        .source_path
        .file_stem()
        .and_then(|s| s.to_str())
        .unwrap_or("untitled");

    let (ext, stem, rel_dir, header, problem_code, problem_title, platform_label, is_ptit) =
        if let Some(ctx) = &snap.context {
            let ext = namer::target_ext(Some(ctx), snap.lang, &snap.text);
            let stem = namer::safe_stem(&namer::base_name(ctx));
            let rel_dir = namer::archive_rel_dir(ctx);
            let header = namer::context_header(ctx, snap.lang, non_utf8);
            (
                ext,
                stem,
                rel_dir,
                header,
                Some(ctx.code.clone()),
                if ctx.title.is_empty() {
                    None
                } else {
                    Some(ctx.title.clone())
                },
                ctx.platform.label().to_string(),
                ctx.platform == Platform::Ptit,
            )
        } else if let Some(comment_code) = analyzer::code_in_comments(&snap.text) {
            let desc = analyzer::describe(&snap.text, snap.lang);
            let stem = namer::safe_stem(&format!("{comment_code}_{desc}"));
            let ext = if comment_code.starts_with('C')
                && comment_code[1..].starts_with(|c: char| c.is_ascii_digit())
                && !namer::is_cpp_only(&snap.text)
            {
                "c"
            } else {
                snap.lang.ext()
            };
            let course = namer::ptit_course(&comment_code);
            let rel_dir = free_subdir_override
                .map(|s| s.to_string())
                .unwrap_or_else(|| format!("PTIT/{course}"));
            let when = Local::now().format("%Y-%m-%d %H:%M").to_string();
            let header = namer::free_header(source_stem, &when, snap.lang);
            (
                ext,
                stem,
                rel_dir,
                header,
                Some(comment_code),
                Some(desc),
                "Detected".to_string(),
                false,
            )
        } else {
            let desc = analyzer::describe(&snap.text, snap.lang);
            let date_prefix = Local::now().format("%Y-%m-%d").to_string();
            let raw_stem = if free_subdir_override.is_some() {
                format!("{desc}__{source_stem}")
            } else {
                format!("{date_prefix}_{desc}__{source_stem}")
            };
            let stem = namer::safe_stem(&raw_stem);
            let rel_dir = free_subdir_override.unwrap_or("Free").to_string();
            let when = Local::now().format("%Y-%m-%d %H:%M").to_string();
            let header = namer::free_header(source_stem, &when, snap.lang);
            (
                snap.lang.ext(),
                stem,
                rel_dir,
                header,
                None,
                Some(desc),
                "Offline".to_string(),
                false,
            )
        };

    let target_dir = config.archive_dir.join(&rel_dir);
    fs::create_dir_all(&target_dir)?;

    let (archive_path, file_name) =
        resolve_target_path(&target_dir, &stem, ext, &snap.text, snap.norm_hash, snap.lang);

    let out_bytes = namer::with_header(&snap.content, &header);
    fs::write(&archive_path, &out_bytes)?;

    let ready_path = if is_ptit || is_ptit_hot || snap.context.is_some() {
        fs::create_dir_all(&config.ready_dir)?;
        let rp = config.ready_dir.join(&file_name);
        fs::write(&rp, &out_bytes)?;
        if config.auto_copy_ready_path && is_ptit_hot {
            let _ = clipboard::set_clipboard_text(&rp.to_string_lossy());
        }
        Some(rp)
    } else {
        None
    };

    let id = archive_path
        .file_stem()
        .and_then(|s| s.to_str())
        .unwrap_or(&stem)
        .to_string();

    let mut existing_drive_id = None;
    let mut existing_synced = false;

    if let Some(pos) = state
        .records
        .iter()
        .position(|r| r.archive_path == archive_path)
    {
        let prev = &state.records[pos];
        existing_drive_id = prev.drive_file_id.clone();
        if prev.norm_hash == snap.norm_hash && prev.synced_to_drive {
            existing_synced = true;
        }
        state.records.remove(pos);
    }

    let rec = ArchiveRecord {
        id,
        file_name,
        archive_path,
        ready_path,
        source_path: snap.source_path.clone(),
        problem_code,
        problem_title,
        platform: platform_label,
        norm_hash: snap.norm_hash,
        created_at: snap.updated_at,
        synced_to_drive: existing_synced,
        drive_file_id: existing_drive_id,
        last_sync_error: None,
    };

    state.records.push(rec.clone());
    // Keep newest items easily accessible; cap unbounded growth only at very high counts.
    if state.records.len() > 2000 {
        state.records.drain(..state.records.len() - 2000);
    }
    state.save(&config.state_file)?;

    Ok(rec)
}

/// Locate and run `gdrive` CLI silently without flashing a console window.
fn run_gdrive(args: &[&str]) -> Result<String, String> {
    let mut cmd = build_gdrive_command(args)?;
    let output = cmd
        .output()
        .map_err(|e| format!("Failed to launch gdrive: {e}"))?;
    let stdout = String::from_utf8_lossy(&output.stdout).trim().to_string();
    let stderr = String::from_utf8_lossy(&output.stderr).trim().to_string();
    if output.status.success() {
        Ok(stdout)
    } else {
        let msg = if stderr.is_empty() { stdout } else { stderr };
        Err(msg)
    }
}

fn find_in_path(name: &str) -> Option<PathBuf> {
    let path_var = std::env::var_os("PATH")?;
    for dir in std::env::split_paths(&path_var) {
        let candidate = dir.join(name);
        if candidate.is_file() {
            return Some(candidate);
        }
    }
    None
}

fn build_gdrive_command(args: &[&str]) -> Result<Command, String> {
    // 1. Direct binary on PATH (preferred, no cmd.exe wrapper needed)
    if let Some(exe) = find_in_path("gdrive.exe") {
        let mut cmd = Command::new(exe);
        cmd.args(args);
        #[cfg(windows)]
        cmd.creation_flags(CREATE_NO_WINDOW);
        return Ok(cmd);
    }

    // 2. `.cmd` wrapper on PATH (how Antigravity exposes `gdrive` in session PATH)
    if let Some(cmd_shim) = find_in_path("gdrive.cmd") {
        let mut cmd = Command::new("cmd.exe");
        cmd.arg("/C").arg(cmd_shim).args(args);
        #[cfg(windows)]
        cmd.creation_flags(CREATE_NO_WINDOW);
        return Ok(cmd);
    }

    // 3. Antigravity plugin fallback paths
    if let Ok(profile) = std::env::var("USERPROFILE") {
        let agy_bin = PathBuf::from(&profile).join(r".gemini\antigravity\bin\gdrive.cmd");
        if agy_bin.is_file() {
            let mut cmd = Command::new("cmd.exe");
            cmd.arg("/C").arg(agy_bin).args(args);
            #[cfg(windows)]
            cmd.creation_flags(CREATE_NO_WINDOW);
            return Ok(cmd);
        }
        let plugin_script = PathBuf::from(&profile)
            .join(r".gemini\config\plugins\gdrive\skills\gdrive\scripts\gdrive.cmd");
        if plugin_script.is_file() {
            let mut cmd = Command::new("cmd.exe");
            cmd.arg("/C").arg(plugin_script).args(args);
            #[cfg(windows)]
            cmd.creation_flags(CREATE_NO_WINDOW);
            return Ok(cmd);
        }
    }

    Err("gdrive CLI not found in PATH or Antigravity plugin directory".to_string())
}

fn extract_id_from_json(raw: &str) -> Option<String> {
    if let Ok(val) = serde_json::from_str::<serde_json::Value>(raw) {
        if let Some(id) = extract_id_value(&val) {
            return Some(id);
        }
    }
    if let Some(start) = raw.find("(ID: ") {
        let rest = &raw[start + 5..];
        if let Some(end) = rest.find(')') {
            let id = rest[..end].trim();
            if !id.is_empty() {
                return Some(id.to_string());
            }
        }
    }
    None
}

fn extract_id_value(val: &serde_json::Value) -> Option<String> {
    match val {
        serde_json::Value::Object(map) => {
            if let Some(serde_json::Value::String(id)) = map.get("id") {
                if !id.is_empty() {
                    return Some(id.clone());
                }
            }
            for key in ["file", "folder", "result", "data", "items", "files"] {
                if let Some(inner) = map.get(key) {
                    if let Some(id) = extract_id_value(inner) {
                        return Some(id);
                    }
                }
            }
            None
        }
        serde_json::Value::Array(arr) => arr.first().and_then(extract_id_value),
        _ => None,
    }
}

fn ensure_drive_root_folder(config: &AppConfig, state: &mut ArchiveState) -> Result<String, String> {
    if let Some(id) = &state.drive_root_folder_id {
        if !id.is_empty() {
            return Ok(id.clone());
        }
    }

    let folder_name = &config.drive_folder_name;
    let search_out = run_gdrive(&[
        "readonly",
        "search",
        "--name-exact",
        folder_name,
        "--type",
        "folder",
        "--json",
    ])?;

    if let Some(id) = extract_id_from_json(&search_out) {
        state.drive_root_folder_id = Some(id.clone());
        let _ = state.save(&config.state_file);
        return Ok(id);
    }

    let mkdir_out = run_gdrive(&["mutate", "mkdir", folder_name, "--json"])?;
    let id = extract_id_from_json(&mkdir_out)
        .ok_or_else(|| format!("gdrive mkdir did not return folder id: {mkdir_out}"))?;
    state.drive_root_folder_id = Some(id.clone());
    let _ = state.save(&config.state_file);
    Ok(id)
}

#[derive(Clone, Debug, Default)]
pub struct SyncReport {
    pub uploaded: usize,
    pub failed: usize,
    pub online: bool,
    pub last_error: Option<String>,
}

/// Sync all pending archived files in `state` to Google Drive when internet is available.
pub fn sync_pending_to_drive(config: &AppConfig, state: &mut ArchiveState) -> SyncReport {
    let mut report = SyncReport::default();
    if !config.drive_enabled {
        return report;
    }
    if state.pending_count() == 0 {
        report.online = check_internet();
        return report;
    }
    if !check_internet() {
        report.online = false;
        report.last_error = Some("No internet connection (queued offline)".into());
        return report;
    }
    report.online = true;

    let folder_id = match ensure_drive_root_folder(config, state) {
        Ok(id) => id,
        Err(e) => {
            report.last_error = Some(e);
            report.failed = state.pending_count();
            return report;
        }
    };

    for idx in 0..state.records.len() {
        if state.records[idx].synced_to_drive {
            continue;
        }
        let archive_path = state.records[idx].archive_path.clone();
        if !archive_path.exists() {
            continue;
        }
        let path_str = archive_path.to_string_lossy().to_string();
        let existing_id = state.records[idx].drive_file_id.clone();

        let res = if let Some(fid) = existing_id.as_deref() {
            run_gdrive(&["mutate", "update", fid, &path_str, "--json"])
        } else {
            run_gdrive(&[
                "mutate",
                "upload",
                &path_str,
                "--parent",
                &folder_id,
                "--json",
            ])
        };

        match res {
            Ok(out) => {
                if let Some(fid) = extract_id_from_json(&out) {
                    state.records[idx].drive_file_id = Some(fid);
                }
                state.records[idx].synced_to_drive = true;
                state.records[idx].last_sync_error = None;
                report.uploaded += 1;
            }
            Err(err) => {
                state.records[idx].last_sync_error = Some(err.clone());
                report.last_error = Some(err);
                report.failed += 1;
            }
        }
    }

    let _ = state.save(&config.state_file);
    report
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::context::{ContextSource, ProblemContext};

    #[test]
    fn json_id_extraction() {
        assert_eq!(extract_id_from_json(r#"{"id":"abc123"}"#), Some("abc123".into()));
        assert_eq!(extract_id_from_json(r#"[{"id":"folder99","name":"CodeArchive"}]"#), Some("folder99".into()));
        assert_eq!(extract_id_from_json(r#"[]"#), None);
        assert_eq!(
            extract_id_from_json("Created folder: CodeArchive (ID: 1Yu4a7qpuj36y-yMRtTfoBxF-fmhVtDz6)"),
            Some("1Yu4a7qpuj36y-yMRtTfoBxF-fmhVtDz6".into())
        );
        assert_eq!(
            extract_id_from_json("Uploaded: String_Case_Convert_Untitled1.cpp (ID: 1n-DU313mDq4WmRnWU_tBrq6KfU6zoHaR)"),
            Some("1n-DU313mDq4WmRnWU_tBrq6KfU6zoHaR".into())
        );
    }

    #[test]
    fn write_archived_file_creates_ready_and_collision_versions() {
        let base = std::env::temp_dir().join("autoname_drive_test");
        let _ = fs::remove_dir_all(&base);

        let mut cfg = AppConfig::default();
        cfg.watched_dir = base.clone();
        cfg.ready_dir = base.join("CodePTIT_Ready");
        cfg.archive_dir = base.join("CodeArchive");
        cfg.state_file = cfg.archive_dir.join("archive_state.json");
        cfg.auto_copy_ready_path = false; // don't clobber developer clipboard in tests
        cfg.drive_enabled = false;

        let mut state = ArchiveState::default();
        let ctx = ProblemContext {
            platform: Platform::Ptit,
            code: "C04015".into(),
            title: "ĐẾM SỐ LẦN XUẤT HIỆN 2".into(),
            url: "https://code.ptit.edu.vn/student/question/C04015".into(),
            source: ContextSource::Manual,
            seen_at: 10,
        };

        let code1 = "#include <stdio.h>\nint main(){\n\tint n; scanf(\"%d\", &n);\n\tprintf(\"%d\", n);\n\treturn 0;\n}\n";
        let snap1 = Snapshot {
            source_path: base.join("Untitled1.cpp"),
            content: code1.as_bytes().to_vec(),
            text: code1.into(),
            norm_hash: similarity::normalized_hash(code1),
            lang: Lang::Cpp,
            context: Some(ctx.clone()),
            compiled_ok: true,
            updated_at: 10,
        };

        let r1 = write_archived_file(&cfg, &mut state, &snap1, true).unwrap();
        assert_eq!(r1.file_name, "C04015_Dem_So_Lan_Xuat_Hien_2.c");
        assert!(r1.archive_path.exists());
        assert!(r1.ready_path.as_ref().unwrap().exists());
        let written = fs::read_to_string(&r1.archive_path).unwrap();
        assert!(written.starts_with("// [PTIT] C04015 - ĐẾM SỐ LẦN XUẤT HIỆN 2"));

        // Distinct second solution under same code should get _v2 instead of clobbering
        let code2 = "#include <stdio.h>\nint main(){\n\tchar s[500]; fgets(s, 500, stdin);\n\tfor(int i=0;s[i];i++) if(s[i]=='a') puts(\"OK\");\n\treturn 0;\n}\n";
        let snap2 = Snapshot {
            source_path: base.join("Untitled1.cpp"),
            content: code2.as_bytes().to_vec(),
            text: code2.into(),
            norm_hash: similarity::normalized_hash(code2),
            lang: Lang::Cpp,
            context: Some(ctx),
            compiled_ok: true,
            updated_at: 20,
        };
        let r2 = write_archived_file(&cfg, &mut state, &snap2, true).unwrap();
        assert_eq!(r2.file_name, "C04015_Dem_So_Lan_Xuat_Hien_2_v2.c");
        assert!(r2.archive_path.exists());

        let _ = fs::remove_dir_all(&base);
    }
}
