//! Problem context: which OJ problem the user is currently looking at.

use serde::{Deserialize, Serialize};

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum Platform {
    Ptit,
    Codeforces,
    Vnoj,
    LeetCode,
    HackerRank,
    Other,
}

/// How the user submits on this platform.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub enum SubmitMode {
    /// "Chọn tệp" — a file with a proper name must exist on disk.
    UploadFile,
    /// Copy-paste the source text.
    PasteText,
}

impl Platform {
    pub fn submit_mode(self) -> SubmitMode {
        match self {
            Platform::Ptit => SubmitMode::UploadFile,
            _ => SubmitMode::PasteText,
        }
    }

    /// Display / folder name.
    pub fn label(self) -> &'static str {
        match self {
            Platform::Ptit => "PTIT",
            Platform::Codeforces => "Codeforces",
            Platform::Vnoj => "VNOJ",
            Platform::LeetCode => "LeetCode",
            Platform::HackerRank => "HackerRank",
            Platform::Other => "Other",
        }
    }

    pub fn parse(s: &str) -> Platform {
        match s.trim().to_ascii_lowercase().as_str() {
            "ptit" | "codeptit" | "code.ptit.edu.vn" => Platform::Ptit,
            "codeforces" | "cf" => Platform::Codeforces,
            "vnoj" | "vnoi" | "oj.vnoi.info" => Platform::Vnoj,
            "leetcode" | "lc" => Platform::LeetCode,
            "hackerrank" | "hr" => Platform::HackerRank,
            _ => Platform::Other,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub enum ContextSource {
    Extension,
    WindowTitle,
    Manual,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct ProblemContext {
    pub platform: Platform,
    /// Sanitised problem code, e.g. `C04015`, `CF_1900A`.
    pub code: String,
    /// Human title as shown on the site (may contain Vietnamese diacritics).
    pub title: String,
    pub url: String,
    pub source: ContextSource,
    /// Unix seconds when this context was last confirmed.
    pub seen_at: i64,
}

impl ProblemContext {
    pub fn same_problem(&self, other: &ProblemContext) -> bool {
        self.code.eq_ignore_ascii_case(&other.code)
    }
}

/// JSON payload POSTed by the browser extension.
#[derive(Clone, Debug, Deserialize, Serialize, Default)]
pub struct ExtensionPayload {
    #[serde(default)]
    pub problem_code: String,
    #[serde(default)]
    pub problem_title: String,
    #[serde(default)]
    pub platform: String,
    #[serde(default)]
    pub url: String,
}

impl ExtensionPayload {
    pub fn into_context(self, now: i64) -> Option<ProblemContext> {
        // Prefer URL-derived code (authoritative), fall back to the payload code.
        let from_url = if self.url.is_empty() {
            None
        } else {
            from_url(&self.url, Some(&self.problem_title), now)
        };
        if let Some(mut ctx) = from_url {
            ctx.source = ContextSource::Extension;
            if ctx.title.is_empty() {
                ctx.title = clean_title(&self.problem_title, &ctx.code);
            }
            return Some(ctx);
        }
        let code = sanitize_code(&self.problem_code)?;
        let platform = if self.platform.is_empty() {
            guess_platform_from_code(&code)
        } else {
            Platform::parse(&self.platform)
        };
        Some(ProblemContext {
            platform,
            title: clean_title(&self.problem_title, &code),
            code,
            url: self.url,
            source: ContextSource::Extension,
            seen_at: now,
        })
    }
}

/// Keep only `[A-Za-z0-9_-]`, max 48 chars. Returns None if empty.
pub fn sanitize_code(raw: &str) -> Option<String> {
    let s: String = raw
        .trim()
        .chars()
        .map(|c| if c == ' ' || c == '/' || c == '.' { '_' } else { c })
        .filter(|c| c.is_ascii_alphanumeric() || *c == '_' || *c == '-')
        .take(48)
        .collect();
    let s = s.trim_matches(|c| c == '_' || c == '-').to_string();
    if s.is_empty() {
        None
    } else {
        Some(s)
    }
}

/// Remove a leading "C04015 - " / "A. " style prefix that duplicates the code.
pub fn clean_title(raw: &str, code: &str) -> String {
    let mut t = raw.trim().to_string();
    // Strip leading code if repeated in title.
    if !code.is_empty() {
        let short = code.rsplit('_').next().unwrap_or(code);
        for prefix in [code, short] {
            let head_matches = t.len() > prefix.len()
                && t.get(..prefix.len()).map(|h| h.eq_ignore_ascii_case(prefix)) == Some(true);
            if head_matches {
                let rest = t[prefix.len()..].trim_start();
                if let Some(r) = rest.strip_prefix(['-', ':', '.', '–', '—']) {
                    t = r.trim().to_string();
                    break;
                }
            }
        }
    }
    // Codeforces "A. Title"
    let bytes = t.as_bytes();
    if bytes.len() > 3 && bytes[0].is_ascii_uppercase() && (bytes[1] == b'.' || (bytes[1].is_ascii_digit() && bytes.get(2) == Some(&b'.'))) {
        if let Some(pos) = t.find(". ") {
            if pos <= 2 {
                t = t[pos + 2..].trim().to_string();
            }
        }
    }
    t.split_whitespace().collect::<Vec<_>>().join(" ")
}

/// Classic PTIT codes look like `C04015`, `CPP0101`, `PY01001`, `DSA01001`.
pub fn looks_like_ptit_code(code: &str) -> bool {
    let letters = code.chars().take_while(|c| c.is_ascii_alphabetic()).count();
    let rest = &code[letters..];
    (1..=4).contains(&letters) && rest.len() >= 3 && rest.len() <= 7 && rest.chars().all(|c| c.is_ascii_digit())
}

pub fn guess_platform_from_code(code: &str) -> Platform {
    let up = code.to_ascii_uppercase();
    if up.starts_with("CF_") {
        Platform::Codeforces
    } else if up.starts_with("VNOJ_") {
        Platform::Vnoj
    } else if up.starts_with("LC_") {
        Platform::LeetCode
    } else if up.starts_with("HR_") {
        Platform::HackerRank
    } else if looks_like_ptit_code(&up) {
        Platform::Ptit
    } else {
        Platform::Other
    }
}

fn split_url(url: &str) -> Option<(String, Vec<String>)> {
    let rest = url.trim();
    let rest = rest
        .strip_prefix("https://")
        .or_else(|| rest.strip_prefix("http://"))
        .unwrap_or(rest);
    let (host, path) = match rest.find('/') {
        Some(i) => (&rest[..i], &rest[i..]),
        None => (rest, "/"),
    };
    let path = path.split(['?', '#']).next().unwrap_or("");
    let host = host.to_ascii_lowercase();
    let host = host.trim_start_matches("www.").to_string();
    if host.is_empty() {
        return None;
    }
    let segs = path
        .split('/')
        .filter(|s| !s.is_empty())
        .map(|s| s.to_string())
        .collect();
    Some((host, segs))
}

/// Derive a context from a problem page URL (+ optional title from the DOM).
pub fn from_url(url: &str, title_hint: Option<&str>, now: i64) -> Option<ProblemContext> {
    let (host, segs) = split_url(url)?;
    let seg = |i: usize| segs.get(i).map(|s| s.as_str());
    let (platform, code) = if host.ends_with("code.ptit.edu.vn") {
        let pos = segs.iter().position(|s| s == "question" || s == "problem" || s == "questions")?;
        let code = sanitize_code(seg(pos + 1)?)?.to_ascii_uppercase();
        (Platform::Ptit, code)
    } else if host.ends_with("codeforces.com") {
        let code = if seg(0) == Some("problemset") && seg(1) == Some("acmsguru") && seg(2) == Some("problem") {
            let prob = seg(4)?;
            format!("SGU{prob}")
        } else if seg(0) == Some("problemset") && seg(1) == Some("problem") {
            match (seg(2), seg(3)) {
                (Some(c), Some(i)) => format!("{c}{i}"),
                (Some(ci), None) => ci.to_string(),
                _ => return None,
            }
        } else if let Some(pos) = segs.windows(4).position(|w| {
            (w[0] == "contest" || w[0] == "gym") && w[2] == "problem"
        }) {
            format!("{}{}", segs[pos + 1], segs[pos + 3])
        } else {
            return None;
        };
        (Platform::Codeforces, format!("CF_{}", sanitize_code(&code)?.to_ascii_uppercase()))
    } else if host.ends_with("oj.vnoi.info") {
        if seg(0) != Some("problem") {
            return None;
        }
        (Platform::Vnoj, format!("VNOJ_{}", sanitize_code(seg(1)?)?))
    } else if host.ends_with("leetcode.com") || host.ends_with("leetcode.cn") {
        if seg(0) != Some("problems") {
            return None;
        }
        (Platform::LeetCode, format!("LC_{}", sanitize_code(seg(1)?)?))
    } else if host.ends_with("hackerrank.com") {
        let pos = segs.iter().position(|s| s == "challenges")?;
        (Platform::HackerRank, format!("HR_{}", sanitize_code(seg(pos + 1)?)?))
    } else {
        return None;
    };
    let title = title_hint.map(|t| clean_title(t, &code)).unwrap_or_default();
    Some(ProblemContext {
        platform,
        code,
        title,
        url: url.trim().to_string(),
        source: ContextSource::Extension,
        seen_at: now,
    })
}

fn strip_edge_multitab_suffix(t: &mut String) {
    let lower = t.to_ascii_lowercase();
    for (needle, tail_markers) in [
        (" and ", &[" more page", " more pages"][..]),
        (" và ", &[" trang nữa", " trang nua"][..]),
    ] {
        if let Some(pos) = lower.rfind(needle) {
            let after = lower[pos + needle.len()..].trim();
            let digits_end = after.find(|c: char| !c.is_ascii_digit()).unwrap_or(after.len());
            if digits_end > 0 {
                let rest = &after[digits_end..];
                if tail_markers.iter().any(|m| rest == *m) {
                    // Truncate on original string at matching char boundary (both markers are ASCII/checked via byte match on lower? Note 'à' is 2 bytes in UTF-8, same in lower and t!).
                    t.truncate(pos);
                    return;
                }
            }
        }
    }
}

/// Strip browser suffixes like " - Microsoft​ Edge", " - Thorium", " - Personal",
/// and Edge multi-tab counts like " and 4 more pages" / " và 3 trang nữa".
pub fn strip_browser_suffix(title: &str) -> String {
    let mut t = title.replace('\u{200b}', "");
    loop {
        let Some(idx) = t.rfind(" - ") else { break };
        let tail = t[idx + 3..].to_ascii_lowercase();
        let is_suffix = tail.contains("edge")
            || tail.contains("thorium")
            || tail.contains("chrom")
            || tail.contains("brave")
            || tail.contains("cốc cốc")
            || tail.contains("coc coc")
            || tail.contains("firefox")
            || tail.starts_with("profile")
            || tail.starts_with("personal")
            || tail.starts_with("work")
            || tail.starts_with("inprivate")
            || tail.starts_with("hồ sơ")
            || tail.starts_with("cá nhân")
            || tail == "code ptit"
            || tail == "codeptit";
        if is_suffix {
            t.truncate(idx);
        } else {
            break;
        }
    }
    strip_edge_multitab_suffix(&mut t);
    t.trim().to_string()
}

/// Parse a browser window title. Supports `[CODE] Title`, raw Codeforces titles
/// ending in `- Codeforces`, and raw PTIT titles starting with a PTIT code.
pub fn from_window_title(window_title: &str, now: i64) -> Option<ProblemContext> {
    let t = strip_browser_suffix(window_title);
    if let Some(rest) = t.strip_prefix('[') {
        let end = rest.find(']')?;
        let code = sanitize_code(&rest[..end])?;
        let title = clean_title(&rest[end + 1..], &code);
        return Some(ProblemContext {
            platform: guess_platform_from_code(&code),
            code,
            title,
            url: String::new(),
            source: ContextSource::WindowTitle,
            seen_at: now,
        });
    }

    // Raw Codeforces titles ending in " - Codeforces"
    let parts: Vec<&str> = t.split(" - ").collect();
    if parts.len() >= 2 && parts.last().map(|s| s.trim().eq_ignore_ascii_case("codeforces")) == Some(true) {
        let head = parts[0].trim();
        if parts.len() >= 3 && head.eq_ignore_ascii_case("problem") {
            let code = sanitize_code(parts[1])?.to_ascii_uppercase();
            return Some(ProblemContext {
                platform: Platform::Codeforces,
                code: format!("CF_{code}"),
                title: String::new(),
                url: String::new(),
                source: ContextSource::WindowTitle,
                seen_at: now,
            });
        }
        // "1900A - Cover in Water - Codeforces"
        if parts.len() >= 3 && head.chars().all(|c| c.is_ascii_alphanumeric()) && head.len() <= 10 {
            let code = sanitize_code(head)?.to_ascii_uppercase();
            let title = parts[1..parts.len() - 1].join(" - ");
            return Some(ProblemContext {
                platform: Platform::Codeforces,
                code: format!("CF_{code}"),
                title: clean_title(&title, &code),
                url: String::new(),
                source: ContextSource::WindowTitle,
                seen_at: now,
            });
        }
        // "A. Cover in Water - Codeforces"
        if let Some(dot) = head.find(". ") {
            if dot <= 2 && head[..dot].chars().all(|c| c.is_ascii_alphanumeric()) {
                let code = sanitize_code(&head[..dot])?.to_ascii_uppercase();
                let title = clean_title(&head[dot + 2..], &code);
                return Some(ProblemContext {
                    platform: Platform::Codeforces,
                    code: format!("CF_{code}"),
                    title,
                    url: String::new(),
                    source: ContextSource::WindowTitle,
                    seen_at: now,
                });
            }
        }
    }

    // Raw PTIT title: "C04015 - ĐẾM SỐ LẦN XUẤT HIỆN 2"
    if let Some(first_tok) = t.split(|c: char| c.is_whitespace() || c == '-' || c == ':').find(|s| !s.is_empty()) {
        if looks_like_ptit_code(first_tok) {
            let code = first_tok.to_ascii_uppercase();
            return Some(ProblemContext {
                platform: Platform::Ptit,
                title: clean_title(&t, &code),
                code,
                url: String::new(),
                source: ContextSource::WindowTitle,
                seen_at: now,
            });
        }
    }

    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ptit_url() {
        let c = from_url(
            "https://code.ptit.edu.vn/student/question/C04015",
            Some("C04015 - ĐẾM SỐ LẦN XUẤT HIỆN 2"),
            1,
        )
        .unwrap();
        assert_eq!(c.platform, Platform::Ptit);
        assert_eq!(c.code, "C04015");
        assert_eq!(c.title, "ĐẾM SỐ LẦN XUẤT HIỆN 2");
        assert_eq!(c.platform.submit_mode(), SubmitMode::UploadFile);
    }

    #[test]
    fn ptit_url_lowercase_and_query() {
        let c = from_url("code.ptit.edu.vn/student/question/cpp0101?x=1", None, 1).unwrap();
        assert_eq!(c.code, "CPP0101");
        assert_eq!(c.title, "");
    }

    #[test]
    fn codeforces_urls() {
        let a = from_url("https://codeforces.com/problemset/problem/1900/A", Some("A. Cover in Water"), 1).unwrap();
        assert_eq!(a.code, "CF_1900A");
        assert_eq!(a.title, "Cover in Water");
        assert_eq!(a.platform.submit_mode(), SubmitMode::PasteText);
        let b = from_url("https://codeforces.com/contest/1900/problem/B", None, 1).unwrap();
        assert_eq!(b.code, "CF_1900B");
        assert!(from_url("https://codeforces.com/blog/entry/1", None, 1).is_none());
    }

    #[test]
    fn other_ojs() {
        assert_eq!(from_url("https://oj.vnoi.info/problem/vmrr", None, 1).unwrap().code, "VNOJ_vmrr");
        assert_eq!(from_url("https://leetcode.com/problems/two-sum/description/", None, 1).unwrap().code, "LC_two-sum");
        assert_eq!(
            from_url("https://www.hackerrank.com/challenges/solve-me-first/problem", None, 1).unwrap().code,
            "HR_solve-me-first"
        );
        assert!(from_url("https://google.com/search?q=x", None, 1).is_none());
    }

    #[test]
    fn window_title_tagged() {
        let c = from_window_title("[C04015] ĐẾM SỐ LẦN XUẤT HIỆN 2 - Personal - Microsoft\u{200b} Edge", 5).unwrap();
        assert_eq!(c.code, "C04015");
        assert_eq!(c.title, "ĐẾM SỐ LẦN XUẤT HIỆN 2");
        assert_eq!(c.platform, Platform::Ptit);
        let d = from_window_title("[CF_1900A] Cover in Water - Thorium", 5).unwrap();
        assert_eq!(d.platform, Platform::Codeforces);
        assert_eq!(d.title, "Cover in Water");
    }

    #[test]
    fn window_title_raw_codeforces_and_edge_multitab() {
        let c = from_window_title("Problem - 1900A - Codeforces - Thorium", 5).unwrap();
        assert_eq!(c.code, "CF_1900A");
        let e1 = from_window_title(
            "Problem - 1900A - Codeforces and 4 more pages - Personal - Microsoft\u{200b} Edge",
            5,
        )
        .unwrap();
        assert_eq!(e1.code, "CF_1900A");
        assert_eq!(e1.platform, Platform::Codeforces);

        let e2 = from_window_title(
            "Problem - 1900A - Codeforces và 3 trang nữa - Cá nhân - Microsoft Edge",
            5,
        )
        .unwrap();
        assert_eq!(e2.code, "CF_1900A");

        let e3 = from_window_title("Problem - A - Codeforces - Thorium", 5).unwrap();
        assert_eq!(e3.code, "CF_A");

        let e4 = from_window_title("A. Cover in Water - Codeforces - Microsoft Edge", 5).unwrap();
        assert_eq!(e4.code, "CF_A");
        assert_eq!(e4.title, "Cover in Water");

        let ptit = from_window_title("C04015 - ĐẾM SỐ LẦN XUẤT HIỆN 2 - Microsoft Edge", 5).unwrap();
        assert_eq!(ptit.platform, Platform::Ptit);
        assert_eq!(ptit.code, "C04015");
        assert_eq!(ptit.title, "ĐẾM SỐ LẦN XUẤT HIỆN 2");

        assert!(from_window_title("YouTube - Microsoft Edge", 5).is_none());
    }

    #[test]
    fn codeforces_group_and_edu_urls() {
        let g = from_url("https://codeforces.com/group/abc123/contest/1900/problem/B", None, 1).unwrap();
        assert_eq!(g.code, "CF_1900B");
        assert_eq!(g.platform, Platform::Codeforces);
        let edu = from_url(
            "https://codeforces.com/edu/course/2/lesson/1/1/practice/contest/283911/problem/A",
            Some("A. Binary Search"),
            1,
        )
        .unwrap();
        assert_eq!(edu.code, "CF_283911A");
        assert_eq!(edu.title, "Binary Search");
    }

    #[test]
    fn payload_fallback_without_url() {
        let p = ExtensionPayload {
            problem_code: "C04015".into(),
            problem_title: "C04015 - ĐẾM SỐ LẦN XUẤT HIỆN 2".into(),
            platform: "".into(),
            url: "".into(),
        };
        let c = p.into_context(1).unwrap();
        assert_eq!(c.platform, Platform::Ptit);
        assert_eq!(c.title, "ĐẾM SỐ LẦN XUẤT HIỆN 2");
    }

    #[test]
    fn sanitize() {
        assert_eq!(sanitize_code(" c04015 ").unwrap(), "c04015");
        assert_eq!(sanitize_code("a/b.c").unwrap(), "a_b_c");
        assert!(sanitize_code("  ").is_none());
        assert!(looks_like_ptit_code("C04015"));
        assert!(looks_like_ptit_code("CPP0101"));
        assert!(!looks_like_ptit_code("CF_1900A"));
    }
}
