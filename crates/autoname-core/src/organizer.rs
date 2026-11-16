//! Legacy file organizer (`--organize-existing`): scans lazy-named scratch files
//! in `watched_dir`, names them using offline heuristics, copies them into
//! `CodeArchive/Organized`, and registers them in `archive_state.json`.

use crate::config::AppConfig;
use crate::drive_sync::{self, ArchiveRecord, ArchiveState};
use crate::namer::Lang;
use crate::similarity;
use crate::snapshot_engine::{self, Snapshot};
use chrono::Utc;
use std::fs;
use std::io;

pub fn organize_existing_files(
    config: &AppConfig,
    state: &mut ArchiveState,
) -> io::Result<Vec<ArchiveRecord>> {
    let mut created = Vec::new();
    if !config.watched_dir.is_dir() {
        return Ok(created);
    }

    let mut entries: Vec<_> = fs::read_dir(&config.watched_dir)?
        .filter_map(|e| e.ok())
        .map(|e| e.path())
        .filter(|p| p.is_file() && snapshot_engine::is_scratch_filename(p))
        .collect();
    entries.sort();

    let now = Utc::now().timestamp();

    for path in entries {
        let lang = match path
            .extension()
            .and_then(|e| e.to_str())
            .and_then(Lang::from_ext)
        {
            Some(l) => l,
            None => continue,
        };
        let raw_bytes = match fs::read(&path) {
            Ok(b) => b,
            Err(_) => continue,
        };
        let text = String::from_utf8_lossy(&raw_bytes).into_owned();
        if similarity::is_trivial(&text, lang) {
            continue;
        }
        let norm_hash = similarity::normalized_hash(&text);
        let snap = Snapshot {
            source_path: path,
            content: raw_bytes,
            text,
            norm_hash,
            lang,
            context: None,
            compiled_ok: false,
            updated_at: now,
        };

        let rec = drive_sync::write_archived_file_in_subdir(
            config,
            state,
            &snap,
            false,
            Some("Organized"),
        )?;
        created.push(rec);
    }

    Ok(created)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;

    #[test]
    fn organizes_scratch_files_without_touching_originals() {
        let base = std::env::temp_dir().join("autoname_org_test");
        let _ = fs::remove_dir_all(&base);
        fs::create_dir_all(&base).unwrap();

        let u1 = base.join("Untitled1.cpp");
        let f11 = base.join("11111.cpp");
        let empty = base.join("Untitled2.cpp");
        let already = base.join("C01001_Gap_Doi.c");

        fs::write(
            &u1,
            "#include <stdio.h>\nint main(){ float a; scanf(\"%f\",&a); printf(\"%.2f\",a); }\n",
        )
        .unwrap();
        fs::write(
            &f11,
            "#include <stdio.h>\nint main(){ int a[10]; for(int j=1;j<5;j++) if(a[j]<a[j-1]) printf(\"NO\"); printf(\"YES\"); }\n",
        )
        .unwrap();
        fs::write(&empty, "#include <stdio.h>\nint main(){ return 0; }\n").unwrap();
        fs::write(&already, "// already named\nint main(){ int x=1; printf(\"%d\",x); }\n")
            .unwrap();

        let mut cfg = AppConfig::default();
        cfg.watched_dir = base.clone();
        cfg.ready_dir = base.join("CodePTIT_Ready");
        cfg.archive_dir = base.join("CodeArchive");
        cfg.state_file = cfg.archive_dir.join("archive_state.json");
        cfg.auto_copy_ready_path = false;
        cfg.drive_enabled = false;

        let mut state = ArchiveState::default();
        let records = organize_existing_files(&cfg, &mut state).unwrap();

        assert_eq!(records.len(), 2);
        let names: Vec<&str> = records.iter().map(|r| r.file_name.as_str()).collect();
        assert!(names.iter().any(|n| n.contains("Untitled1")));
        assert!(names.iter().any(|n| n.contains("11111")));
        // Originals remain untouched
        assert!(u1.exists());
        assert!(f11.exists());
        assert!(empty.exists());
        assert!(already.exists());
        // Organized files sit under CodeArchive/Organized
        let organized_dir: PathBuf = cfg.archive_dir.join("Organized");
        assert!(organized_dir.is_dir());

        let _ = fs::remove_dir_all(&base);
    }
}
