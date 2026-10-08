use std::{env, fs, path::Path};
fn run() -> Result<(), Box<dyn std::error::Error>> {
    let args: Vec<String> = env::args().collect();
    let input = args.get(1).ok_or("input required")?;
    let module = if input.ends_with(".spv") {
        naga::front::spv::parse_u8_slice(&fs::read(input)?, &Default::default())?
    } else {
        naga::front::wgsl::parse_str(&fs::read_to_string(input)?)?
    };
    let info = naga::valid::Validator::new(naga::valid::ValidationFlags::all(), naga::valid::Capabilities::all()).validate(&module)?;
    let values = args.get(3).map(|text| -> Result<naga::back::PipelineConstants, Box<dyn std::error::Error>> {
        text.split(';').filter(|p| !p.is_empty()).map(|p| {
            let (key, value) = p.split_once('=').ok_or("key=value required")?;
            Ok((key.to_string(), value.parse::<f64>()?))
        }).collect()
    }).transpose()?;
    let processed = values.as_ref().map(|v| naga::back::pipeline_constants::process_overrides(&module, &info, None, v)).transpose()?;
    let (module, info) = processed.as_ref().map(|(m,i)| (m.as_ref(), i.as_ref())).unwrap_or((&module, &info));
    if let Some(output) = args.get(2) {
        if Path::new(output).extension().is_some_and(|e| e == "spv") {
            let mesh = module.entry_points.iter().any(|entry| matches!(entry.stage, naga::ShaderStage::Task | naga::ShaderStage::Mesh));
            let options = naga::back::spv::Options { lang_version: (1, if mesh { 4 } else { 3 }), ..Default::default() };
            let words = naga::back::spv::write_vec(module, info, &options, None)?;
            fs::write(output, words.iter().flat_map(|w| w.to_le_bytes()).collect::<Vec<_>>())?;
        } else {
            fs::write(output, naga::back::wgsl::write_string(module, info, naga::back::wgsl::WriterFlags::empty())?)?;
        }
    }
    Ok(())
}
fn main() {
    if let Err(error) = run() { eprintln!("{error:?}"); std::process::exit(1); }
}
