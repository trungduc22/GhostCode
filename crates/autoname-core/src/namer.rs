//! File naming: `<Code>_<Normalized_Title>.<ext>` plus header comments.

use crate::context::{Platform, ProblemContext};
use unicode_normalization::char::is_combining_mark;
use unicode_normalization::UnicodeNormalization;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Lang {
    C,
    Cpp,
    Python,
}

impl Lang {
    pub fn from_ext(ext: &str) -> Option<Lang> {
        match ext.to_ascii_lowercase().as_str() {
            "c" => Some(Lang::C),
            "cpp" | "cc" | "cxx" | "c++" => Some(Lang::Cpp),
            "py" | "pyw" => Some(Lang::Python),
            _ => None,
        }
    }
    pub fn ext(self) -> &'static str {
        match self {
            Lang::C => "c",
            Lang::Cpp => "cpp",
            Lang::Python => "py",
        }
    }
    pub fn comment_prefix(self) -> &'static str {
        match self {
            Lang::Python => "#",
            _ => "//",
        }
    }
}

/// "ĐẾM SỐ LẦN" -> "DEM SO LAN".
pub fn strip_diacritics(s: &str) -> String {
    s.chars()
        .map(|c| match c {
            'đ' => 'd',
            'Đ' => 'D',
            _ => c,
        })
        .collect::<String>()
        .nfd()
        .filter(|c| !is_combining_mark(*c))
        .collect()
}

/// "ĐẾM SỐ LẦN XUẤT HIỆN 2" -> "Dem_So_Lan_Xuat_Hien_2" (max ~60 chars, word boundary).
pub fn normalize_title(title: &str) -> String {
    let plain = strip_diacritics(title);
    let mut words: Vec<String> = Vec::new();
    for w in plain.split(|c: char| !c.is_ascii_alphanumeric()) {
        if w.is_empty() {
            continue;
        }
        let lower = w.to_ascii_lowercase();
        let mut cs = lower.chars();
        let word = match cs.next() {
            Some(f) => f.to_ascii_uppercase().to_string() + cs.as_str(),
            None => continue,
        };
        words.push(word);
    }
    let mut out = String::new();
    for w in words {
        if !out.is_empty() && out.len() + 1 + w.len() > 60 {
            break;
        }
        if !out.is_empty() {
            out.push('_');
        }
        out.push_str(&w);
    }
    out
}

/// `C04015_Dem_So_Lan_Xuat_Hien_2` (or just the code when no title is known).
pub fn base_name(ctx: &ProblemContext) -> String {
    let t = normalize_title(&ctx.title);
    if t.is_empty() {
        ctx.code.clone()
    } else {
        format!("{}_{}", ctx.code, t)
    }
}

/// True if the source clearly uses C++-only features.
pub fn is_cpp_only(content: &str) -> bool {
    const MARKERS: [&str; 9] = [
        "#include <iostream>",
        "#include<iostream>",
        "bits/stdc++.h",
        "using namespace std",
        "std::",
        "cout",
        "cin >>",
        "cin>>",
        "#include <vector>",
    ];
    MARKERS.iter().any(|m| content.contains(m))
}

/// Pick the output extension. PTIT course prefix wins: `C0*`→.c, `CPP*`→.cpp,
/// `PY*`→.py — but never force `.c` onto real C++ code.
pub fn target_ext(ctx: Option<&ProblemContext>, source_lang: Lang, content: &str) -> &'static str {
    if source_lang == Lang::Python {
        return "py";
    }
    if let Some(ctx) = ctx {
        if ctx.platform == Platform::Ptit {
            let up = ctx.code.to_ascii_uppercase();
            if up.starts_with("CPP") {
                return "cpp";
            }
            if up.starts_with("PY") {
                return "py";
            }
            if up.starts_with('C') && up[1..].starts_with(|c: char| c.is_ascii_digit()) {
                return if is_cpp_only(content) { "cpp" } else { "c" };
            }
        }
    }
    source_lang.ext()
}

/// PTIT course folder: leading letters of the code (`C`, `CPP`, `PY`, `DSA`).
pub fn ptit_course(code: &str) -> String {
    let letters: String = code.chars().take_while(|c| c.is_ascii_alphabetic()).collect();
    if letters.is_empty() {
        "Other".into()
    } else {
        letters.to_ascii_uppercase()
    }
}

/// Relative archive folder for a context.
pub fn archive_rel_dir(ctx: &ProblemContext) -> String {
    match ctx.platform {
        Platform::Ptit => format!("PTIT/{}", ptit_course(&ctx.code)),
        p => p.label().to_string(),
    }
}

pub const HEADER_TAG: &str = "[AutoName]";

/// One-line header, e.g. `// [PTIT] C04015 - ĐẾM SỐ LẦN XUẤT HIỆN 2 | https://...`.
/// When `ascii_only` is set (non-UTF-8 sources), diacritics are stripped.
pub fn context_header(ctx: &ProblemContext, lang: Lang, ascii_only: bool) -> String {
    let title = if ascii_only { strip_diacritics(&ctx.title) } else { ctx.title.clone() };
    let mut h = format!("{} [{}] {}", lang.comment_prefix(), ctx.platform.label(), ctx.code);
    if !title.is_empty() {
        h.push_str(" - ");
        h.push_str(&title);
    }
    if !ctx.url.is_empty() {
        h.push_str(" | ");
        h.push_str(&ctx.url);
    }
    h.push(' ');
    h.push_str(HEADER_TAG);
    h
}

pub fn free_header(source_name: &str, when: &str, lang: Lang) -> String {
    format!("{} {} from {} @ {}", lang.comment_prefix(), HEADER_TAG, source_name, when)
}

/// Prepend `header` to `body`, replacing an existing AutoName header line.
pub fn with_header(body: &[u8], header: &str) -> Vec<u8> {
    let newline: &[u8] = if body.windows(2).any(|w| w == b"\r\n") { b"\r\n" } else { b"\n" };
    let mut rest = body;
    // Skip UTF-8 BOM
    let bom = rest.starts_with(&[0xEF, 0xBB, 0xBF]);
    if bom {
        rest = &rest[3..];
    }
    // Drop an existing header line we generated earlier.
    let first_end = rest.iter().position(|&b| b == b'\n').map(|i| i + 1).unwrap_or(rest.len());
    if String::from_utf8_lossy(&rest[..first_end]).contains(HEADER_TAG) {
        rest = &rest[first_end..];
    }
    let mut out = Vec::with_capacity(body.len() + header.len() + 4);
    if bom {
        out.extend_from_slice(&[0xEF, 0xBB, 0xBF]);
    }
    out.extend_from_slice(header.as_bytes());
    out.extend_from_slice(newline);
    out.extend_from_slice(rest);
    out
}

/// Remove our header line (used when comparing archived files with new content).
pub fn strip_header(body: &[u8]) -> &[u8] {
    let rest = if body.starts_with(&[0xEF, 0xBB, 0xBF]) { &body[3..] } else { body };
    let first_end = rest.iter().position(|&b| b == b'\n').map(|i| i + 1).unwrap_or(rest.len());
    if String::from_utf8_lossy(&rest[..first_end]).contains(HEADER_TAG) {
        &rest[first_end..]
    } else {
        body
    }
}

/// Make a string safe as a Windows file stem.
pub fn safe_stem(s: &str) -> String {
    let cleaned: String = s
        .chars()
        .map(|c| if c.is_ascii_alphanumeric() || c == '_' || c == '-' { c } else { '_' })
        .collect();
    let mut out = String::new();
    for c in cleaned.chars() {
        if c == '_' && out.ends_with('_') {
            continue;
        }
        out.push(c);
    }
    let out = out.trim_matches('_').to_string();
    if out.is_empty() {
        "untitled".into()
    } else {
        out.chars().take(100).collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::context::{from_url, ContextSource};

    fn ptit(code: &str, title: &str) -> ProblemContext {
        ProblemContext {
            platform: Platform::Ptit,
            code: code.into(),
            title: title.into(),
            url: format!("https://code.ptit.edu.vn/student/question/{code}"),
            source: ContextSource::Manual,
            seen_at: 0,
        }
    }

    #[test]
    fn diacritics() {
        assert_eq!(strip_diacritics("ĐẾM SỐ LẦN XUẤT HIỆN 2"), "DEM SO LAN XUAT HIEN 2");
        assert_eq!(strip_diacritics("đường đi ngắn nhất"), "duong di ngan nhat");
    }

    #[test]
    fn normalized() {
        assert_eq!(normalize_title("ĐẾM SỐ LẦN XUẤT HIỆN 2"), "Dem_So_Lan_Xuat_Hien_2");
        assert_eq!(normalize_title("  Cover in   Water!! "), "Cover_In_Water");
        assert_eq!(normalize_title(""), "");
        assert!(normalize_title(&"word ".repeat(40)).len() <= 60);
    }

    #[test]
    fn names_and_exts() {
        let c = ptit("C04015", "ĐẾM SỐ LẦN XUẤT HIỆN 2");
        assert_eq!(base_name(&c), "C04015_Dem_So_Lan_Xuat_Hien_2");
        let c_code = "#include <stdio.h>\nint main(){}";
        assert_eq!(target_ext(Some(&c), Lang::Cpp, c_code), "c");
        assert_eq!(target_ext(Some(&c), Lang::Cpp, "#include <iostream>\nusing namespace std;"), "cpp");
        assert_eq!(target_ext(Some(&ptit("CPP0101", "x")), Lang::C, c_code), "cpp");
        assert_eq!(target_ext(Some(&ptit("PY01001", "x")), Lang::C, c_code), "py");
        let cf = from_url("https://codeforces.com/contest/1900/problem/A", Some("A. Cover in Water"), 0).unwrap();
        assert_eq!(target_ext(Some(&cf), Lang::Cpp, c_code), "cpp");
        assert_eq!(base_name(&cf), "CF_1900A_Cover_In_Water");
        assert_eq!(archive_rel_dir(&c), "PTIT/C");
        assert_eq!(archive_rel_dir(&cf), "Codeforces");
    }

    #[test]
    fn header_roundtrip() {
        let c = ptit("C04015", "ĐẾM SỐ LẦN XUẤT HIỆN 2");
        let h = context_header(&c, Lang::C, false);
        assert!(h.starts_with("// [PTIT] C04015 - ĐẾM SỐ LẦN XUẤT HIỆN 2 | https://code.ptit.edu.vn/student/question/C04015"));
        let body = b"#include <stdio.h>\r\nint main(){}\r\n";
        let once = with_header(body, &h);
        let twice = with_header(&once, &h);
        assert_eq!(once, twice, "header must not be duplicated");
        assert!(once.windows(2).any(|w| w == b"\r\n"));
        assert_eq!(strip_header(&once), body);
        let ascii = context_header(&c, Lang::Python, true);
        assert!(ascii.starts_with("# [PTIT] C04015 - DEM SO LAN"));
    }

    #[test]
    fn stems() {
        assert_eq!(safe_stem("a b/c"), "a_b_c");
        assert_eq!(safe_stem("__"), "untitled");
    }
}
