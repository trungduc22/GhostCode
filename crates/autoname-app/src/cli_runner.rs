//! Shared CLI action execution used by both `autoname-cli` and `autoname`.

use autoname_core::config::AppConfig;
use autoname_core::context::Platform;
use autoname_core::drive_sync::{self, ArchiveState};
use autoname_core::organizer;
use autoname_core::EngineHandle;
use std::path::{Path, PathBuf};

fn run_simulated_problem(
    config: &AppConfig,
    state: &mut ArchiveState,
    platform: Platform,
    code: &str,
    title: &str,
    src: &Path,
) -> i32 {
    if !src.is_file() {
        eprintln!("Source file not found: {}", src.display());
        return 2;
    }
    match EngineHandle::simulate_problem(config, state, platform, code, title, src) {
        Ok(rec) => {
            println!("Hot-created Archive file : {}", rec.archive_path.display());
            if let Some(rp) = &rec.ready_path {
                println!("Hot-created Ready file   : {}", rp.display());
            }
            let report = drive_sync::sync_pending_to_drive(config, state);
            println!(
                "Drive sync: online={}, uploaded={}, failed={}, pending={}",
                report.online,
                report.uploaded,
                report.failed,
                state.pending_count()
            );
            if let Some(err) = report.last_error {
                eprintln!("Drive sync error: {err}");
                if report.uploaded == 0 && state.pending_count() > 0 {
                    return 1;
                }
            }
            0
        }
        Err(e) => {
            eprintln!("Simulation failed: {e}");
            1
        }
    }
}

/// Returns `Some(exit_code)` if CLI flags were handled, or `None` if GUI mode should launch.
pub fn handle_cli_args(args: &[String]) -> Option<i32> {
    if args.len() <= 1 {
        return None;
    }
    let config_path = AppConfig::default_config_path();
    let config = AppConfig::load_or_default(&config_path);
    let mut state = ArchiveState::load_or_default(&config.state_file);

    let cmd = args[1].as_str();
    match cmd {
        "--help" | "-h" => {
            println!("AutoName Code CLI");
            println!("  --organize-existing                       Scan watched folder, rename legacy scratch files into CodeArchive, and sync to Drive");
            println!("  --simulate-ptit <CODE> <TITLE> <FILE>     Simulate PTIT context, hot-create Ready + Archive file, and sync to Drive");
            println!("  --simulate-cf <CODE> <TITLE> <FILE>       Simulate Codeforces context, hot-create Ready + Archive file, and sync to Drive");
            println!("  --sync-now                                Upload all pending files in archive_state.json to Google Drive");
            println!("  --status                                  Show current configuration and archive/sync counts");
            Some(0)
        }
        "--status" => {
            println!("Config path     : {}", config_path.display());
            println!("Watched folder  : {}", config.watched_dir.display());
            println!("PTIT Ready dir  : {}", config.ready_dir.display());
            println!("Archive dir     : {}", config.archive_dir.display());
            println!("State file      : {}", config.state_file.display());
            println!("Language        : {:?}", config.language);
            println!("Dark mode       : {}", config.dark_mode);
            println!("Drive enabled   : {}", config.drive_enabled);
            println!("Drive folder id : {:?}", state.drive_root_folder_id);
            println!("Archived total  : {}", state.records.len());
            println!("Pending sync    : {}", state.pending_count());
            Some(0)
        }
        "--organize-existing" => {
            println!(
                "Scanning existing scratch files in: {}",
                config.watched_dir.display()
            );
            match organizer::organize_existing_files(&config, &mut state) {
                Ok(records) => {
                    println!("Processed {} file(s):", records.len());
                    for r in &records {
                        println!(
                            "  {} -> {}",
                            r.source_path
                                .file_name()
                                .unwrap_or_default()
                                .to_string_lossy(),
                            r.archive_path.display()
                        );
                    }
                    let report = drive_sync::sync_pending_to_drive(&config, &mut state);
                    println!(
                        "Drive sync: online={}, uploaded={}, failed={}, pending={}",
                        report.online,
                        report.uploaded,
                        report.failed,
                        state.pending_count()
                    );
                    if let Some(err) = report.last_error {
                        eprintln!("Drive sync note: {err}");
                    }
                    Some(0)
                }
                Err(e) => {
                    eprintln!("Error organizing existing files: {e}");
                    Some(1)
                }
            }
        }
        "--simulate-ptit" => {
            if args.len() < 5 {
                eprintln!("Usage: --simulate-ptit <CODE> <TITLE> <SOURCE_FILE>");
                return Some(2);
            }
            let src = PathBuf::from(&args[4]);
            Some(run_simulated_problem(
                &config,
                &mut state,
                Platform::Ptit,
                &args[2],
                &args[3],
                &src,
            ))
        }
        "--simulate-cf" => {
            if args.len() < 5 {
                eprintln!("Usage: --simulate-cf <CODE> <TITLE> <SOURCE_FILE>");
                return Some(2);
            }
            let raw = args[2].to_ascii_uppercase();
            let code = if raw.starts_with("CF_") {
                raw
            } else {
                format!("CF_{raw}")
            };
            let src = PathBuf::from(&args[4]);
            Some(run_simulated_problem(
                &config,
                &mut state,
                Platform::Codeforces,
                &code,
                &args[3],
                &src,
            ))
        }
        "--sync-now" => {
            let report = drive_sync::sync_pending_to_drive(&config, &mut state);
            println!(
                "Drive sync: online={}, uploaded={}, failed={}, pending={}",
                report.online,
                report.uploaded,
                report.failed,
                state.pending_count()
            );
            if let Some(err) = report.last_error {
                eprintln!("Drive sync error: {err}");
                return Some(1);
            }
            Some(0)
        }
        _ => None,
    }
}
