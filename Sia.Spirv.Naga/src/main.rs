use std::{env, fs, path::Path, process::ExitCode};

use sia_spirv_naga::{NAGA_VERSION, translate_spirv_to_wgsl, validate_wgsl};

fn main() -> ExitCode {
    match run() {
        Ok(()) => ExitCode::SUCCESS,
        Err(error) => {
            eprintln!("{error}");
            ExitCode::FAILURE
        }
    }
}

fn run() -> Result<(), String> {
    let arguments = env::args().skip(1).collect::<Vec<_>>();
    match arguments.as_slice() {
        [argument] if argument == "--version" || argument == "-V" => {
            println!("{NAGA_VERSION}");
            Ok(())
        }
        [input] => validate(Path::new(input)),
        [input, output] => translate(Path::new(input), Path::new(output)),
        _ => {
            Err("Usage: naga <input.spv> [output.wgsl] | naga <input.wgsl> | naga --version".into())
        }
    }
}

fn validate(input: &Path) -> Result<(), String> {
    match input.extension().and_then(|extension| extension.to_str()) {
        Some("wgsl") => {
            let source = fs::read_to_string(input)
                .map_err(|error| format!("Reading '{}' failed: {error}", input.display()))?;
            validate_wgsl(&source)
        }
        Some("spv") => {
            let spirv = fs::read(input)
                .map_err(|error| format!("Reading '{}' failed: {error}", input.display()))?;
            translate_spirv_to_wgsl(&spirv).map(|_| ())
        }
        _ => Err(format!("Unsupported shader input '{}'.", input.display())),
    }
}

fn translate(input: &Path, output: &Path) -> Result<(), String> {
    let spirv = fs::read(input)
        .map_err(|error| format!("Reading '{}' failed: {error}", input.display()))?;
    let wgsl = translate_spirv_to_wgsl(&spirv)?;
    fs::write(output, wgsl)
        .map_err(|error| format!("Writing '{}' failed: {error}", output.display()))
}
