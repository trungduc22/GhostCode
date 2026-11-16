//! Scratch-file detection and "Last Good Snapshot" state machine.

use crate::context::ProblemContext;
use crate::namer::Lang;
use crate::similarity;
use std::collections::HashMap;
use std::path::{Path, PathBuf};

#[derive(Clone, Debug)]
pub struct Snapshot {
    pub source_path: PathBuf,
    pub content: Vec<u8>,
    pub text: String,
    pub norm_hash: u64,
    pub lang: Lang,
    pub context: Option<ProblemContext>,
    pub compiled_ok: bool,
    pub updated_at: i64,
}

#[derive(Clone, Debug, Default)]
pub struct SnapshotAction {
    /// Emitted when a previous problem was overwritten, cleared, or replaced by another problem.
    pub archive_previous: Option<Snapshot>,
    /// Emitted whenever a non-trivial scratch file is saved/compiled so Ready/Archive stay updated.
    pub hot_update_current: Option<Snapshot>,
}

/// True if `path` looks like a scratch file (Untitled*, numeric names, main.c, test.py, ...)
/// rather than an already structured problem file (`C04015_...`) inside archive/ready folders.
pub fn is_scratch_filename(path: &Path) -> bool {
    let ext = match path.extension().and_then(|e| e.to_str()) {
        Some(e) => e,
        None => return false,
    };
    if Lang::from_ext(ext).is_none() {
        return false;
    }

    // Never treat files inside CodePTIT_Ready or CodeArchive as scratch inputs.
    for comp in path.components() {
        if let Some(s) = comp.as_os_str().to_str() {
            if s.eq_ignore_ascii_case("CodePTIT_Ready") || s.eq_ignore_ascii_case("CodeArchive") {
                return false;
            }
        }
    }

    let stem = match path.file_stem().and_then(|s| s.to_str()) {
        Some(s) => s.trim(),
        None => return false,
    };
    if stem.is_empty() {
        return false;
    }

    let lower = stem.to_ascii_lowercase();

    // Structured problem prefixes are NOT scratch files.
    let upper = stem.to_ascii_uppercase();
    if upper.starts_with("CF_")
        || upper.starts_with("VNOJ_")
        || upper.starts_with("LC_")
        || upper.starts_with("HR_")
    {
        return false;
    }
    if let Some((first_seg, _)) = upper.split_once('_') {
        if crate::context::looks_like_ptit_code(first_seg) {
            return false;
        }
    }

    // 1. Starts with known scratch prefixes.
    const SCRATCH_PREFIXES: [&str; 9] = [
        "untitled", "main", "test", "temp", "tmp", "nhap", "draft", "bai", "code",
    ];
    if SCRATCH_PREFIXES.iter().any(|p| lower.starts_with(p)) || lower.starts_with("scratch") {
        return true;
    }

    // 2. Single ASCII letter (`a.c`, `b.cpp`, ...).
    if stem.len() == 1 && stem.chars().all(|c| c.is_ascii_alphabetic()) {
        return true;
    }

    // 3. Only digits, spaces, hyphens, underscores, or the word "copy" (e.g. `11111.cpp`, `123 - Copy.cpp`).
    let stripped = lower.replace("copy", "");
    let has_digit = stripped.chars().any(|c| c.is_ascii_digit());
    let only_num_punct = stripped
        .chars()
        .all(|c| c.is_ascii_digit() || c == ' ' || c == '-' || c == '_' || c == '(' || c == ')');
    if has_digit && only_num_punct {
        return true;
    }

    false
}

#[derive(Default)]
pub struct SnapshotEngine {
    snapshots: HashMap<PathBuf, Snapshot>,
}

impl SnapshotEngine {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn get(&self, path: &Path) -> Option<&Snapshot> {
        self.snapshots.get(path)
    }

    pub fn on_file_saved(
        &mut self,
        path: &Path,
        raw_bytes: Vec<u8>,
        current_ctx: Option<ProblemContext>,
        now: i64,
    ) -> SnapshotAction {
        self.on_file_event(path, raw_bytes, current_ctx, false, now)
    }

    pub fn on_file_event(
        &mut self,
        path: &Path,
        raw_bytes: Vec<u8>,
        current_ctx: Option<ProblemContext>,
        compiled_ok: bool,
        now: i64,
    ) -> SnapshotAction {
        let mut action = SnapshotAction::default();
        let lang = match path
            .extension()
            .and_then(|e| e.to_str())
            .and_then(Lang::from_ext)
        {
            Some(l) => l,
            None => return action,
        };

        let text = String::from_utf8_lossy(&raw_bytes).into_owned();

        // If cleared or reset to boilerplate template, archive the previous non-trivial snapshot.
        if similarity::is_trivial(&text, lang) {
            if let Some(prev) = self.snapshots.remove(path) {
                action.archive_previous = Some(prev);
            }
            return action;
        }

        let norm_hash = similarity::normalized_hash(&text);

        if let Some(prev) = self.snapshots.get_mut(path) {
            if prev.norm_hash == norm_hash {
                let ctx_changed = match (&prev.context, &current_ctx) {
                    (Some(a), Some(b)) => !a.same_problem(b),
                    (None, Some(_)) => true,
                    _ => false,
                };
                if ctx_changed {
                    prev.context = current_ctx;
                    prev.updated_at = now;
                    prev.compiled_ok |= compiled_ok;
                    action.hot_update_current = Some(prev.clone());
                } else if compiled_ok && !prev.compiled_ok {
                    prev.compiled_ok = true;
                    prev.updated_at = now;
                    action.hot_update_current = Some(prev.clone());
                }
                return action;
            }

            let sim = similarity::similarity(&prev.text, &text, lang);
            let ctx_switched = match (&prev.context, &current_ctx) {
                (Some(a), Some(b)) => !a.same_problem(b),
                _ => false,
            };

            if sim < 0.50 || ctx_switched {
                action.archive_previous = Some(prev.clone());
            }
        }

        // Preserve previous context if no browser problem is currently detected?
        // Only if the code is the same problem (sim >= 0.50 and current_ctx is None).
        let effective_ctx = match (current_ctx, self.snapshots.get(path)) {
            (Some(c), _) => Some(c),
            (None, Some(prev)) if similarity::similarity(&prev.text, &text, lang) >= 0.50 => {
                prev.context.clone()
            }
            (None, _) => None,
        };

        let snap = Snapshot {
            source_path: path.to_path_buf(),
            content: raw_bytes,
            text,
            norm_hash,
            lang,
            context: effective_ctx,
            compiled_ok,
            updated_at: now,
        };
        self.snapshots.insert(path.to_path_buf(), snap.clone());
        action.hot_update_current = Some(snap);
        action
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::context::{ContextSource, Platform};

    fn ctx(code: &str, title: &str) -> ProblemContext {
        ProblemContext {
            platform: Platform::Ptit,
            code: code.into(),
            title: title.into(),
            url: format!("https://code.ptit.edu.vn/student/question/{code}"),
            source: ContextSource::Manual,
            seen_at: 100,
        }
    }

    const CODE_A: &str = "#include <stdio.h>\nint main(){\n\tint a,b;\n\tscanf(\"%d%d\",&a,&b);\n\tprintf(\"%d\",a+b);\n\treturn 0;\n}\n";
    const CODE_A_MOD: &str = "#include <stdio.h>\nint main(){\n\tint a,b;\n\tscanf(\"%d%d\",&a,&b);\n\tprintf(\"%d\\n\",a+b);\n\treturn 0;\n}\n";
    const CODE_B: &str = "#include <stdio.h>\nint main(){\n\tchar s[105];\n\tfgets(s, sizeof(s), stdin);\n\tint n = 0;\n\twhile(s[n]) n++;\n\tprintf(\"%d\", n);\n\treturn 0;\n}\n";

    #[test]
    fn scratch_filename_detection() {
        assert!(is_scratch_filename(Path::new(r"C:\Users\Duc\OneDrive\Documents\Untitled1.cpp")));
        assert!(is_scratch_filename(Path::new(r"C:\Users\Duc\OneDrive\Documents\Untitled14.cpp")));
        assert!(is_scratch_filename(Path::new(r"C:\Users\Duc\OneDrive\Documents\Untitled1 - Copy.cpp")));
        assert!(is_scratch_filename(Path::new(r"C:\Users\Duc\OneDrive\Documents\11111.cpp")));
        assert!(is_scratch_filename(Path::new(r"C:\Users\Duc\OneDrive\Documents\123 - Copy.cpp")));
        assert!(is_scratch_filename(Path::new(r"C:\Users\Duc\OneDrive\Documents\7.cpp")));
        assert!(is_scratch_filename(Path::new(r"C:\Users\Duc\OneDrive\Documents\main.c")));
        assert!(is_scratch_filename(Path::new(r"C:\Users\Duc\OneDrive\Documents\test.py")));

        assert!(!is_scratch_filename(Path::new(r"C:\Users\Duc\OneDrive\Documents\C04015_Dem.c")));
        assert!(!is_scratch_filename(Path::new(r"C:\Users\Duc\OneDrive\Documents\CodePTIT_Ready\Untitled1.cpp")));
        assert!(!is_scratch_filename(Path::new(r"C:\Users\Duc\OneDrive\Documents\CodeArchive\Untitled1.cpp")));
        assert!(!is_scratch_filename(Path::new(r"C:\Users\Duc\OneDrive\Documents\notes.txt")));
    }

    #[test]
    fn snapshot_lifecycle_overwrite_and_clear() {
        let mut engine = SnapshotEngine::new();
        let path = PathBuf::from(r"C:\Users\Duc\OneDrive\Documents\Untitled1.cpp");
        let c1 = ctx("C01001", "Gap doi");
        let c2 = ctx("C04015", "Dem so lan xuat hien 2");

        // 1. Initial save -> hot_update_current
        let a1 = engine.on_file_saved(&path, CODE_A.as_bytes().to_vec(), Some(c1.clone()), 1);
        assert!(a1.archive_previous.is_none());
        assert_eq!(a1.hot_update_current.unwrap().context.unwrap().code, "C01001");

        // 2. Minor edit of same problem -> hot_update_current without archive_previous
        let a2 = engine.on_file_saved(&path, CODE_A_MOD.as_bytes().to_vec(), Some(c1.clone()), 2);
        assert!(a2.archive_previous.is_none());
        assert!(a2.hot_update_current.is_some());

        // 3. Same hash -> no-op
        let a_noop = engine.on_file_saved(&path, CODE_A_MOD.as_bytes().to_vec(), Some(c1), 3);
        assert!(a_noop.archive_previous.is_none());
        assert!(a_noop.hot_update_current.is_none());

        // 4. Overwrite with completely different problem (<50% similarity) -> archives previous & updates current
        let a3 = engine.on_file_saved(&path, CODE_B.as_bytes().to_vec(), Some(c2), 4);
        let prev = a3.archive_previous.expect("must archive overwritten problem");
        assert_eq!(prev.context.unwrap().code, "C01001");
        let curr = a3.hot_update_current.expect("must hot update new problem");
        assert_eq!(curr.context.unwrap().code, "C04015");

        // 5. Clear file back to empty template -> archives previous completed program
        let template = b"#include <stdio.h>\nint main() {\n    return 0;\n}\n".to_vec();
        let a4 = engine.on_file_saved(&path, template, None, 5);
        let prev2 = a4.archive_previous.expect("must archive when cleared to boilerplate");
        assert_eq!(prev2.context.unwrap().code, "C04015");
        assert!(a4.hot_update_current.is_none());
    }
}
