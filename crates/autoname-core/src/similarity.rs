//! Content comparison: line-based similarity that ignores boilerplate.

use crate::namer::Lang;

const BOILERPLATE_PREFIXES: [&str; 8] = [
    "#include",
    "using namespace",
    "int main",
    "void main",
    "return 0;",
    "import ",
    "from ",
    "if __name__",
];

fn strip_block_comments(src: &str) -> String {
    let mut out = String::with_capacity(src.len());
    let mut rest = src;
    while let Some(start) = rest.find("/*") {
        out.push_str(&rest[..start]);
        match rest[start + 2..].find("*/") {
            Some(end) => rest = &rest[start + 2 + end + 2..],
            None => {
                rest = "";
            }
        }
    }
    out.push_str(rest);
    out
}

/// Lines that carry the actual solution: trimmed, whitespace-collapsed,
/// without comments, blank lines, braces or include/main boilerplate.
pub fn meaningful_lines(content: &str, lang: Lang) -> Vec<String> {
    let src = if lang == Lang::Python { content.to_string() } else { strip_block_comments(content) };
    let mut out = Vec::new();
    for raw in src.lines() {
        let mut line = raw.trim();
        // strip trailing line comments
        if lang == Lang::Python {
            if line.starts_with('#') {
                continue;
            }
        } else if let Some(idx) = line.find("//") {
            line = line[..idx].trim();
        }
        if line.is_empty() {
            continue;
        }
        if line.chars().all(|c| matches!(c, '{' | '}' | ';' | '(' | ')')) {
            continue;
        }
        if BOILERPLATE_PREFIXES.iter().any(|p| line.starts_with(p)) && line.len() < 40 {
            continue;
        }
        let collapsed: String = line.split_whitespace().collect::<Vec<_>>().join(" ");
        out.push(collapsed);
    }
    out
}

/// Empty file or only a template (`#include`, `int main(){ return 0; }`).
pub fn is_trivial(content: &str, lang: Lang) -> bool {
    meaningful_lines(content, lang).is_empty()
}

fn lcs_len(a: &[String], b: &[String]) -> usize {
    let (a, b) = if a.len() < b.len() { (b, a) } else { (a, b) };
    let mut prev = vec![0usize; b.len() + 1];
    let mut cur = vec![0usize; b.len() + 1];
    for x in a {
        for (j, y) in b.iter().enumerate() {
            cur[j + 1] = if x == y { prev[j] + 1 } else { cur[j].max(prev[j + 1]) };
        }
        std::mem::swap(&mut prev, &mut cur);
    }
    prev[b.len()]
}

/// 0.0 (unrelated) .. 1.0 (same solution).
pub fn similarity(a: &str, b: &str, lang: Lang) -> f64 {
    const CAP: usize = 3000;
    let la: Vec<String> = meaningful_lines(a, lang).into_iter().take(CAP).collect();
    let lb: Vec<String> = meaningful_lines(b, lang).into_iter().take(CAP).collect();
    if la.is_empty() && lb.is_empty() {
        return 1.0;
    }
    if la.is_empty() || lb.is_empty() {
        return 0.0;
    }
    2.0 * lcs_len(&la, &lb) as f64 / (la.len() + lb.len()) as f64
}

/// FNV-1a 64-bit — stable across runs (unlike `DefaultHasher`).
pub fn content_hash(bytes: &[u8]) -> u64 {
    let mut h: u64 = 0xcbf29ce484222325;
    for &b in bytes {
        h ^= b as u64;
        h = h.wrapping_mul(0x100000001b3);
    }
    h
}

/// Hash that ignores CRLF/LF and trailing whitespace differences.
pub fn normalized_hash(text: &str) -> u64 {
    let mut s = String::with_capacity(text.len());
    for line in text.lines() {
        s.push_str(line.trim_end());
        s.push('\n');
    }
    content_hash(s.trim_end().as_bytes())
}

/// Heuristic: is this clipboard text a whole program worth archiving?
pub fn guess_code_lang(text: &str) -> Option<Lang> {
    let t = text.trim();
    if t.len() < 30 || t.lines().count() < 3 {
        return None;
    }
    let has_c = t.contains("#include") && (t.contains("main(") || t.contains("main ("));
    if has_c {
        return Some(if crate::namer::is_cpp_only(t) { Lang::Cpp } else { Lang::C });
    }
    let py_score = ["def ", "print(", "input(", "import ", "for ", " in range("]
        .iter()
        .filter(|m| t.contains(*m))
        .count();
    if py_score >= 2 && !t.contains(';') || py_score >= 3 {
        return Some(Lang::Python);
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    const A: &str = "#include <stdio.h>\nint main(){\n\tint a,b;\n\tscanf(\"%d%d\",&a,&b);\n\tprintf(\"%d\",a+b);\n\treturn 0;\n}\n";
    const A_EDIT: &str = "#include <stdio.h>\nint main(){\n\tint a,b;\n\tscanf(\"%d%d\",&a,&b);\n\tprintf(\"%d\\n\",a+b);\n\treturn 0;\n}\n";
    const B: &str = "#include <stdio.h>\nint main(){\n\tfloat x;\n\tscanf(\"%f\",&x);\n\tif (x > 0) printf(\"POS\");\n\telse printf(\"NEG\");\n}\n";

    #[test]
    fn trivial_templates() {
        assert!(is_trivial("", Lang::C));
        assert!(is_trivial("#include <stdio.h>\n\nint main() {\n    \n    return 0;\n}\n", Lang::C));
        assert!(is_trivial("// only a comment\n/* block */\n", Lang::Cpp));
        assert!(!is_trivial(A, Lang::C));
        assert!(is_trivial("# comment\n\n", Lang::Python));
    }

    #[test]
    fn similarity_ranges() {
        assert!(similarity(A, A_EDIT, Lang::C) >= 0.6);
        assert!(similarity(A, B, Lang::C) < 0.5);
        assert_eq!(similarity(A, A, Lang::C), 1.0);
        assert_eq!(similarity("", A, Lang::C), 0.0);
    }

    #[test]
    fn hashing() {
        assert_eq!(normalized_hash("a\r\nb  \r\n"), normalized_hash("a\nb\n"));
        assert_ne!(content_hash(b"a"), content_hash(b"b"));
    }

    #[test]
    fn clipboard_guess() {
        assert_eq!(guess_code_lang(A), Some(Lang::C));
        assert_eq!(guess_code_lang("#include <iostream>\nusing namespace std;\nint main(){ cout << 1; }\n"), Some(Lang::Cpp));
        assert_eq!(guess_code_lang("n = int(input())\nfor i in range(n):\n    print(i)\n"), Some(Lang::Python));
        assert_eq!(guess_code_lang("5\n1 2 3 4 5\n"), None);
        assert_eq!(guess_code_lang("hello world this is some text\nline\nline"), None);
    }
}
