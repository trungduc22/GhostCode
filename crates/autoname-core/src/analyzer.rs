//! Offline heuristic analyzer: names a program from what it does when no
//! OJ problem is known (free coding, legacy `Untitled*.cpp` files).

use crate::namer::Lang;

/// Extract string literals ("...") from source.
fn string_literals(src: &str) -> Vec<String> {
    let mut out = Vec::new();
    let mut chars = src.chars().peekable();
    while let Some(c) = chars.next() {
        if c == '"' {
            let mut lit = String::new();
            while let Some(d) = chars.next() {
                if d == '\\' {
                    if let Some(e) = chars.next() {
                        lit.push('\\');
                        lit.push(e);
                    }
                    continue;
                }
                if d == '"' || d == '\n' {
                    break;
                }
                lit.push(d);
            }
            out.push(lit);
        } else if c == '\'' {
            // skip char literal like '"'
            let _ = chars.next();
            if chars.peek() == Some(&'\'') {
                chars.next();
            }
        }
    }
    out
}

/// Words printed by the program (YES/NO/FIRST/...): uppercase literal words
/// that are not format specifiers.
fn output_words(src: &str) -> Vec<String> {
    let mut words = Vec::new();
    for lit in string_literals(src) {
        if lit.contains('%') && lit.chars().filter(|c| c.is_alphabetic()).count() <= 3 {
            continue;
        }
        let cleaned = lit.replace("\\n", " ").replace("\\t", " ");
        for w in cleaned.split(|c: char| !c.is_ascii_alphabetic()) {
            if w.len() >= 2 && w.len() <= 12 && w.chars().all(|c| c.is_ascii_uppercase()) && !words.contains(&w.to_string()) {
                words.push(w.to_string());
            }
        }
    }
    words
}

fn has_any(hay: &str, needles: &[&str]) -> bool {
    needles.iter().any(|n| hay.contains(n))
}

/// Detect a PTIT-style code mentioned in a comment (e.g. `// C04015`).
pub fn code_in_comments(src: &str) -> Option<String> {
    for line in src.lines().take(15) {
        let t = line.trim();
        let comment = if let Some(r) = t.strip_prefix("//") {
            r
        } else if let Some(r) = t.strip_prefix('#') {
            if r.starts_with("include") || r.starts_with("define") {
                continue;
            }
            r
        } else if let Some(r) = t.strip_prefix("/*") {
            r
        } else {
            continue;
        };
        if comment.contains(crate::namer::HEADER_TAG) {
            continue;
        }
        for tok in comment.split(|c: char| !c.is_ascii_alphanumeric()) {
            let up = tok.to_ascii_uppercase();
            if crate::context::looks_like_ptit_code(&up) && up.chars().any(|c| c.is_ascii_digit()) {
                return Some(up);
            }
        }
    }
    None
}

/// Returns up to three tags joined by `_`, e.g. `String_Case_Convert`.
pub fn describe(src: &str, lang: Lang) -> String {
    let lower = src.to_ascii_lowercase();
    let compact: String = lower.chars().filter(|c| !c.is_whitespace()).collect();
    let mut tags: Vec<&'static str> = Vec::new();
    let push = |t: &'static str, tags: &mut Vec<&'static str>| {
        if !tags.contains(&t) {
            tags.push(t);
        }
    };

    let is_string = has_any(&compact, &["strlen(", "fgets(", "gets(", "strcmp(", "strcpy(", "strcat(", "strcspn(", "strrev(", "getline(", "std::string", "string", "char s[", "chars["]);
    let case_conv = has_any(&compact, &["toupper(", "tolower(", "-=32", "+=32", "-='a'-'a'", ".upper()", ".lower()", ".title()", ".capitalize()"]);
    let prime = compact.contains("prime") || compact.contains("nguyento") || ((compact.contains("%i==0") || compact.contains("%j==0")) && (compact.contains("sqrt(") || compact.contains("i*i<=")));
    let gcd = has_any(&compact, &["gcd(", "ucln", "lcm(", "bcnn"]);
    let fib = has_any(&compact, &["fib", "fibonacci"]);
    let fact = has_any(&compact, &["factorial", "giaithua", "fact("]);
    let sort = has_any(&compact, &["qsort(", "sort(", "sorted(", ".sort("]) || (compact.contains("temp=a[") && compact.matches("for(").count() >= 2);
    let bsearch = compact.contains("mid=") && (compact.contains("l<=r") || compact.contains("left<=right") || compact.contains("low<=high"));
    let matrix = compact.contains("][j]") || compact.contains("[i][");
    let sorted_check = has_any(&compact, &["a[j]<a[j-1]", "a[i]>a[i+1]", "a[i]<a[i-1]", "a[j]>a[j+1]", "a[i]>a[i-1]", "a[j-1]>a[j]"]);
    let digits = compact.contains("%10") && (compact.contains("/=10") || compact.contains("/10"));
    let parity = compact.contains("%2");
    let float_fmt = has_any(&compact, &["%.1f", "%.2f", "%.3f", "%.4f", "%lf", "%f\""]) || (lang == Lang::Python && compact.contains(":.2f"));
    let palindrome = compact.contains("palin") || compact.contains("doixung");
    let reverse = compact.contains("reverse") || compact.contains("strrev(") || compact.contains("[::-1]");
    let recursion = detect_recursion(src);
    let structs = compact.contains("struct") || compact.contains("typedef");
    let file_io = compact.contains("fopen(") || compact.contains("freopen(") || compact.contains("open(");
    let dynmem = has_any(&compact, &["malloc(", "calloc(", "realloc(", "new int"]);
    let sum = compact.contains("sum") || compact.contains("tong");
    let maxmin = has_any(&compact, &["max", "min"]);
    let count = has_any(&compact, &["cnt", "count", "dem"]);
    let array = compact.contains("a[n]") || compact.contains("[i]") || compact.contains("[j]");

    if prime { push("Prime", &mut tags) }
    if gcd { push("Gcd_Lcm", &mut tags) }
    if fib { push("Fibonacci", &mut tags) }
    if fact { push("Factorial", &mut tags) }
    if palindrome { push("Palindrome", &mut tags) }
    if bsearch { push("Binary_Search", &mut tags) }
    if is_string { push("String", &mut tags) }
    if case_conv { push("Case_Convert", &mut tags) }
    if reverse { push("Reverse", &mut tags) }
    if matrix { push("Matrix", &mut tags) }
    if sorted_check { push("Sorted_Check", &mut tags) }
    if sort { push("Sort", &mut tags) }
    if digits { push("Digits", &mut tags) }
    if recursion { push("Recursion", &mut tags) }
    if structs { push("Struct", &mut tags) }
    if dynmem { push("Dynamic_Memory", &mut tags) }
    if file_io { push("File_IO", &mut tags) }
    if tags.is_empty() && array { push("Array", &mut tags) }
    if tags.len() < 2 && sum { push("Sum", &mut tags) }
    if tags.len() < 2 && maxmin { push("Max_Min", &mut tags) }
    if tags.len() < 2 && count { push("Count", &mut tags) }
    if tags.len() < 2 && parity { push("Parity", &mut tags) }
    if tags.is_empty() && float_fmt { push("Float_Format", &mut tags) }

    let mut parts: Vec<String> = tags.iter().take(3).map(|s| s.to_string()).collect();
    // Printed verdict words give strong hints (YES/NO, FIRST/SECOND).
    let words = output_words(src);
    if !words.is_empty() && parts.len() < 3 {
        let joined: Vec<String> = words
            .iter()
            .take(2)
            .map(|w| {
                let l = w.to_ascii_lowercase();
                let mut c = l.chars();
                c.next().map(|f| f.to_ascii_uppercase().to_string() + c.as_str()).unwrap_or_default()
            })
            .collect();
        parts.push(joined.join("_"));
    }
    if parts.is_empty() {
        parts.push("Basic_IO".into());
    }
    parts.join("_")
}

fn detect_recursion(src: &str) -> bool {
    // A function `name(` defined with a type prefix and called inside its own body.
    for line in src.lines() {
        let t = line.trim();
        for ty in ["int ", "long long ", "long ", "void ", "double ", "bool ", "def "] {
            if let Some(rest) = t.strip_prefix(ty) {
                if let Some(paren) = rest.find('(') {
                    let name = rest[..paren].trim();
                    if name.is_empty() || name == "main" || !name.chars().all(|c| c.is_alphanumeric() || c == '_') {
                        continue;
                    }
                    let needle = format!("{name}(");
                    if src.matches(&needle).count() >= 3 {
                        return true;
                    }
                }
            }
        }
    }
    false
}

#[cfg(test)]
mod tests {
    use super::*;

    // Real files from the user's Documents folder.
    const UNTITLED1: &str = r#"#include <stdio.h>
#include <string.h>
int main(){
	int t;
	scanf("%d",&t);
	getchar();
	char name[t];
	for (int i = 0;i<t;i++){
		char s[105];
		fgets(s,sizeof(s),stdin);
		int ok = 1;
		s[strcspn(s,"\n")] = '\0';
		int len = strlen(s);
		for (int j = 0;j<len;j++){
			if (s[j] == ' ') ok = 1;
			if (ok == 1 && (s[j] >= 'a'&& s[j] <= 'z')){
				s[j]-=32;
				ok = 0;
			}
		}
		printf("%s\n",s);
	}
}"#;

    const F11111: &str = r#"#include <stdio.h>
int main(){
	int t,n,dummy;
	scanf("%d",&t);
	for (int i = 1; i<=t;i++){
		int check = 0;
		scanf("%d",&n);
		int a[n];
		for (int j = 1;j<n;j++){
			scanf("%d",&dummy);
			a[j] = dummy;
			if (a[j] < a[j-1]){
				check = 1;
				break;
			}
		}
		if (check == 1) printf("NO\n");
		else printf("YES\n");
	}
}"#;

    const F123: &str = "#include <stdio.h>\nint main(){\n\tfloat a;\n\tscanf(\"%f\",&a);\n\tprintf(\"%.2f\",a);\n}\n";

    const UNTITLED14: &str = r#"#include <stdio.h>
int main(){
	int a,b;
	scanf("%lld%lld",&a,&b);
	if (a <= b){
		printf("FIRST");
		return 0;
	}
	int dead = a-b;
	if (dead % 2 != 0) printf("FIRST");
	else printf("SECOND");
}"#;

    #[test]
    fn real_files() {
        assert_eq!(describe(UNTITLED1, Lang::Cpp), "String_Case_Convert");
        assert_eq!(describe(F11111, Lang::Cpp), "Sorted_Check_No_Yes");
        assert_eq!(describe(F123, Lang::Cpp), "Float_Format");
        assert_eq!(describe(UNTITLED14, Lang::Cpp), "Parity_First_Second");
    }

    #[test]
    fn other_patterns() {
        let prime = "#include <stdio.h>\n#include <math.h>\nint main(){int n;scanf(\"%d\",&n);for(int i=2;i<=sqrt(n);i++) if(n%i==0){printf(\"NO\");return 0;} printf(\"YES\");}";
        assert!(describe(prime, Lang::C).starts_with("Prime"));
        let fib = "int fib(int n){ if(n<2) return n; return fib(n-1)+fib(n-2);}\nint main(){ printf(\"%d\", fib(10)); }";
        let d = describe(fib, Lang::C);
        assert!(d.contains("Fibonacci") && d.contains("Recursion"), "{d}");
        assert_eq!(describe("int main(){}", Lang::C), "Basic_IO");
    }

    #[test]
    fn code_comment() {
        assert_eq!(code_in_comments("// Bai C04015 dem so\n#include <stdio.h>"), Some("C04015".into()));
        assert_eq!(code_in_comments("#include <stdio.h>\nint main(){}"), None);
    }
}
