use std::{mem::size_of, ptr};

use naga::{
    back::wgsl::{self, WriterFlags},
    front::spv,
    valid::{Capabilities, ValidationFlags, Validator},
};

pub const SIA_SPIRV_ABI_VERSION: u32 = 1;
pub const NAGA_VERSION: &str = "30.0.0";

pub const SIA_SPIRV_STATUS_SUCCESS: i32 = 0;
pub const SIA_SPIRV_STATUS_INVALID_ARGUMENT: i32 = 1;
pub const SIA_SPIRV_STATUS_TRANSLATION_FAILED: i32 = 2;

#[repr(C)]
pub struct SiaSpirvAbi {
    pub version: u32,
    pub struct_size: u32,
    pub status: i32,
    pub flags: u32,
    pub input_pointer: *mut u8,
    pub input_length: usize,
    pub output_pointer: *const u8,
    pub output_length: usize,
}

pub struct SiaSpirvContext {
    abi: SiaSpirvAbi,
    input: Vec<u8>,
    output: Vec<u8>,
}

impl SiaSpirvContext {
    fn new() -> Self {
        let mut context = Self {
            abi: SiaSpirvAbi {
                version: SIA_SPIRV_ABI_VERSION,
                struct_size: size_of::<SiaSpirvAbi>() as u32,
                status: SIA_SPIRV_STATUS_SUCCESS,
                flags: 0,
                input_pointer: ptr::null_mut(),
                input_length: 0,
                output_pointer: ptr::null(),
                output_length: 0,
            },
            input: Vec::new(),
            output: Vec::new(),
        };
        context.refresh_abi();
        context
    }

    fn refresh_abi(&mut self) {
        self.abi.input_pointer = if self.input.is_empty() {
            ptr::null_mut()
        } else {
            self.input.as_mut_ptr()
        };
        self.abi.input_length = self.input.len();
        self.abi.output_pointer = if self.output.is_empty() {
            ptr::null()
        } else {
            self.output.as_ptr()
        };
        self.abi.output_length = self.output.len();
    }

    fn set_output(&mut self, status: i32, value: &str) {
        self.abi.status = status;
        self.output.clear();
        self.output.extend_from_slice(value.as_bytes());
        self.refresh_abi();
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn sia_spirv_abi_version() -> u32 {
    SIA_SPIRV_ABI_VERSION
}

#[unsafe(no_mangle)]
pub extern "C" fn sia_spirv_context_create() -> *mut SiaSpirvContext {
    Box::into_raw(Box::new(SiaSpirvContext::new()))
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn sia_spirv_context_destroy(context: *mut SiaSpirvContext) {
    if !context.is_null() {
        drop(unsafe { Box::from_raw(context) });
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn sia_spirv_context_abi(
    context: *mut SiaSpirvContext,
) -> *const SiaSpirvAbi {
    let Some(context) = (unsafe { context.as_mut() }) else {
        return ptr::null();
    };
    context.refresh_abi();
    &context.abi
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn sia_spirv_context_resize_input(
    context: *mut SiaSpirvContext,
    length: usize,
) -> i32 {
    let Some(context) = (unsafe { context.as_mut() }) else {
        return SIA_SPIRV_STATUS_INVALID_ARGUMENT;
    };
    context.input.clear();
    if let Err(error) = context.input.try_reserve_exact(length) {
        context.set_output(
            SIA_SPIRV_STATUS_INVALID_ARGUMENT,
            &format!("Allocating the SPIR-V input buffer failed: {error}"),
        );
        return SIA_SPIRV_STATUS_INVALID_ARGUMENT;
    }
    context.input.resize(length, 0);
    context.abi.status = SIA_SPIRV_STATUS_SUCCESS;
    context.output.clear();
    context.refresh_abi();
    SIA_SPIRV_STATUS_SUCCESS
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn sia_spirv_context_translate(context: *mut SiaSpirvContext) -> i32 {
    let Some(context) = (unsafe { context.as_mut() }) else {
        return SIA_SPIRV_STATUS_INVALID_ARGUMENT;
    };
    match translate_spirv_to_wgsl(&context.input) {
        Ok(wgsl) => context.set_output(SIA_SPIRV_STATUS_SUCCESS, &wgsl),
        Err(error) => context.set_output(SIA_SPIRV_STATUS_TRANSLATION_FAILED, &error),
    }
    context.abi.status
}

pub fn translate_spirv_to_wgsl(spirv: &[u8]) -> Result<String, String> {
    let module = spv::parse_u8_slice(spirv, &spv::Options::default())
        .map_err(|error| format!("SPIR-V parsing failed: {error}"))?;
    let info = Validator::new(ValidationFlags::all(), Capabilities::default())
        .validate(&module)
        .map_err(|error| format!("SPIR-V validation failed: {error}"))?;
    wgsl::write_string(&module, &info, WriterFlags::empty())
        .map_err(|error| format!("WGSL generation failed: {error}"))
}

pub fn validate_wgsl(source: &str) -> Result<(), String> {
    let module = naga::front::wgsl::parse_str(source)
        .map_err(|error| format!("WGSL parsing failed: {error}"))?;
    Validator::new(ValidationFlags::all(), Capabilities::default())
        .validate(&module)
        .map_err(|error| format!("WGSL validation failed: {error}"))?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn abi_reports_translation_errors() {
        let context = sia_spirv_context_create();
        assert_eq!(
            unsafe { sia_spirv_context_resize_input(context, 4) },
            SIA_SPIRV_STATUS_SUCCESS
        );
        let abi = unsafe { &*sia_spirv_context_abi(context) };
        assert_eq!(abi.version, SIA_SPIRV_ABI_VERSION);
        assert_eq!(abi.struct_size as usize, size_of::<SiaSpirvAbi>());
        unsafe { ptr::copy_nonoverlapping([0u8; 4].as_ptr(), abi.input_pointer, 4) };
        assert_eq!(
            unsafe { sia_spirv_context_translate(context) },
            SIA_SPIRV_STATUS_TRANSLATION_FAILED
        );
        let abi = unsafe { &*sia_spirv_context_abi(context) };
        assert!(!abi.output_pointer.is_null());
        assert!(abi.output_length > 0);
        unsafe { sia_spirv_context_destroy(context) };
    }
}
