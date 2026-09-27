//! The VS Code sidecar.
//!
//! The extension host starts one of these per window and talks to it over
//! stdin/stdout. It owns no UI logic; it is the same engine the desktop app
//! calls directly, wrapped in a line reader.

mod session;

use std::process::ExitCode;

fn main() -> ExitCode {
    let mut state_dir: Option<std::path::PathBuf> = None;
    let mut args = std::env::args().skip(1);

    while let Some(arg) = args.next() {
        match arg.as_str() {
            // VS Code passes its global storage directory here, so the client
            // keeps its data outside any workspace.
            "--state-dir" => match args.next() {
                Some(dir) => state_dir = Some(std::path::PathBuf::from(dir)),
                None => {
                    eprintln!("--state-dir needs a path");
                    return ExitCode::FAILURE;
                }
            },
            "--version" | "-V" => {
                println!("{}", env!("CARGO_PKG_VERSION"));
                return ExitCode::SUCCESS;
            }
            "--protocol-version" => {
                println!("{}", api_client_protocol::PROTOCOL_VERSION);
                return ExitCode::SUCCESS;
            }
            "--help" | "-h" => {
                print_help();
                return ExitCode::SUCCESS;
            }
            other => {
                eprintln!("unknown argument: {other}");
                print_help();
                return ExitCode::FAILURE;
            }
        }
    }

    init_tracing();

    let runtime = match tokio::runtime::Builder::new_multi_thread()
        .enable_all()
        .build()
    {
        Ok(runtime) => runtime,
        Err(err) => {
            eprintln!("could not start the async runtime: {err}");
            return ExitCode::FAILURE;
        }
    };

    let store = match state_dir {
        Some(dir) => api_client_storage::Store::in_dir(dir),
        None => api_client_storage::Store::in_dir(api_client_storage::default_data_dir()),
    }
    .with_system_keyring();

    match runtime.block_on(session::run(store)) {
        Ok(()) => ExitCode::SUCCESS,
        Err(err) => {
            tracing::error!(error = %err, "sidecar exiting");
            ExitCode::FAILURE
        }
    }
}

fn print_help() {
    eprintln!(
        "api-client-sidecar {}\n\n\
         Reads newline-delimited JSON protocol messages on stdin and writes them on stdout.\n\n\
         Options:\n      \
           --state-dir <path>      directory for the state file\n  \
           -V, --version           print the binary version\n      \
           --protocol-version      print the supported protocol version\n  \
           -h, --help              print this message",
        env!("CARGO_PKG_VERSION")
    );
}

/// Logs go to stderr only; stdout belongs to the protocol.
fn init_tracing() {
    use tracing_subscriber::EnvFilter;

    let filter = EnvFilter::try_from_env("API_CLIENT_LOG")
        .unwrap_or_else(|_| EnvFilter::new("api_client_sidecar=info,api_client_http_engine=info"));

    tracing_subscriber::fmt()
        .with_env_filter(filter)
        .with_writer(std::io::stderr)
        .with_target(false)
        .init();
}
