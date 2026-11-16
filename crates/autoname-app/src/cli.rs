use autoname_app::cli_runner;

fn main() {
    let args: Vec<String> = std::env::args().collect();
    if args.len() <= 1 {
        let help = vec![args[0].clone(), "--help".to_string()];
        let code = cli_runner::handle_cli_args(&help).unwrap_or(0);
        std::process::exit(code);
    }
    match cli_runner::handle_cli_args(&args) {
        Some(code) => std::process::exit(code),
        None => {
            eprintln!("Unknown arguments. Use --help for usage.");
            std::process::exit(2);
        }
    }
}
