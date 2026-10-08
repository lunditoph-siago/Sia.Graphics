mod spirv_scan;

use std::{mem::size_of, ptr};

use naga::{
    AddressSpace, StorageAccess,
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
    let mut module = spv::parse_u8_slice(spirv, &spv::Options::default())
        .map_err(|error| format!("SPIR-V parsing failed: {error}"))?;
    apply_storage_access_decorations(spirv, &mut module);
    let capabilities = Capabilities::default() | Capabilities::SHADER_FLOAT16_IN_FLOAT32;
    let info = Validator::new(ValidationFlags::all(), capabilities)
        .validate(&module)
        .map_err(|error| format!("SPIR-V validation failed: {error}"))?;
    wgsl::write_string(&module, &info, WriterFlags::empty())
        .map_err(|error| format!("WGSL generation failed: {error}"))
}

/// naga's SPIR-V front end drops the `NonWritable` decoration when it
/// lowers a binding into [`naga::AddressSpace::Storage`], so a
/// `ReadOnlyStorageBuffer<T>` round-trips through naga as a writable
/// `var<storage, read_write>` in the emitted WGSL. wgpu then rejects
/// binding a `read-only-storage` buffer to it. This recovers which
/// (descriptor set, binding) pairs were declared `NonWritable` in the
/// raw module (see [`spirv_scan`]) and reapplies that onto naga's own
/// module before it gets written out as WGSL.
fn apply_storage_access_decorations(spirv: &[u8], module: &mut naga::Module) {
    // naga already accepted this module, so `decode_words`/`scan`
    // should never actually return `None` here; the early-outs just
    // mean this stays a no-op instead of panicking if that changes.
    let Some(words) = spirv_scan::decode_words(spirv) else {
        return;
    };
    let Some(annotations) = spirv_scan::scan(&words) else {
        return;
    };
    let read_only_bindings = spirv_scan::read_only_bindings(&annotations);

    for (_, variable) in module.global_variables.iter_mut() {
        let Some(binding) = &variable.binding else {
            continue;
        };
        if !read_only_bindings.contains(&(binding.group, binding.binding)) {
            continue;
        }
        if let AddressSpace::Storage { access } = &mut variable.space {
            *access &= !StorageAccess::STORE;
        }
    }
}

pub fn validate_wgsl(source: &str) -> Result<(), String> {
    let module = naga::front::wgsl::parse_str(source)
        .map_err(|error| format!("WGSL parsing failed: {error}"))?;
    let capabilities = Capabilities::default() | Capabilities::SHADER_FLOAT16_IN_FLOAT32;
    Validator::new(ValidationFlags::all(), capabilities)
        .validate(&module)
        .map_err(|error| format!("WGSL validation failed: {error}"))?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A minimal Vulkan 1.2 (SPIR-V 1.5) compute module with a single
    /// storage buffer resource at (set 0, binding 0) whose sole member
    /// is decorated `NonWritable` — the shape the LLVM SPIR-V backend
    /// emits for `Sia.Spirv.ReadOnlyStorageBuffer<T>`. Assembled with
    /// `spirv-as --target-env spv1.5` from:
    ///
    /// ```text
    /// OpCapability Shader
    /// OpMemoryModel Logical GLSL450
    /// OpEntryPoint GLCompute %main "main"
    /// OpExecutionMode %main LocalSize 1 1 1
    /// OpDecorate %runtimearr_uint ArrayStride 4
    /// OpDecorate %block Block
    /// OpMemberDecorate %block 0 Offset 0
    /// OpMemberDecorate %block 0 NonWritable
    /// OpDecorate %var DescriptorSet 0
    /// OpDecorate %var Binding 0
    /// %void = OpTypeVoid
    /// %func_void = OpTypeFunction %void
    /// %uint = OpTypeInt 32 0
    /// %runtimearr_uint = OpTypeRuntimeArray %uint
    /// %block = OpTypeStruct %runtimearr_uint
    /// %ptr_storage_block = OpTypePointer StorageBuffer %block
    /// %var = OpVariable %ptr_storage_block StorageBuffer
    /// %main = OpFunction %void None %func_void
    /// %entry = OpLabel
    /// OpReturn
    /// OpFunctionEnd
    /// ```
    const READONLY_STORAGE_BUFFER_SPIRV_WORDS: &[u32] = &[
        0x07230203, 0x00010500, 0x00070000, 0x0000000a, 0x00000000, 0x00020011, 0x00000001,
        0x0003000e, 0x00000000, 0x00000001, 0x0005000f, 0x00000005, 0x00000001, 0x6e69616d,
        0x00000000, 0x00060010, 0x00000001, 0x00000011, 0x00000001, 0x00000001, 0x00000001,
        0x00040047, 0x00000002, 0x00000006, 0x00000004, 0x00030047, 0x00000003, 0x00000002,
        0x00050048, 0x00000003, 0x00000000, 0x00000023, 0x00000000, 0x00040048, 0x00000003,
        0x00000000, 0x00000018, 0x00040047, 0x00000004, 0x00000022, 0x00000000, 0x00040047,
        0x00000004, 0x00000021, 0x00000000, 0x00020013, 0x00000005, 0x00030021, 0x00000006,
        0x00000005, 0x00040015, 0x00000007, 0x00000020, 0x00000000, 0x0003001d, 0x00000002,
        0x00000007, 0x0003001e, 0x00000003, 0x00000002, 0x00040020, 0x00000008, 0x0000000c,
        0x00000003, 0x0004003b, 0x00000008, 0x00000004, 0x0000000c, 0x00050036, 0x00000005,
        0x00000001, 0x00000000, 0x00000006, 0x000200f8, 0x00000009, 0x000100fd, 0x00010038,
    ];

    fn readonly_storage_buffer_spirv() -> Vec<u8> {
        READONLY_STORAGE_BUFFER_SPIRV_WORDS
            .iter()
            .flat_map(|word| word.to_le_bytes())
            .collect()
    }

    #[test]
    fn naga_drops_non_writable_on_its_own() {
        // Documents the naga behavior `apply_storage_access_decorations`
        // exists to work around: parsing this fixture with no patching
        // yields a writable `AddressSpace::Storage`, even though the
        // module decorates the buffer's only member `NonWritable`. If
        // this assertion ever fails, naga started preserving the
        // decoration itself and the workaround can be deleted.
        let spirv = readonly_storage_buffer_spirv();
        let module = spv::parse_u8_slice(&spirv, &spv::Options::default())
            .expect("fixture module should parse");
        let (_, variable) = module
            .global_variables
            .iter()
            .next()
            .expect("fixture declares exactly one global variable");
        let AddressSpace::Storage { access } = variable.space else {
            panic!("fixture variable should be in the Storage address space");
        };
        assert!(
            access.contains(StorageAccess::STORE),
            "expected naga to (incorrectly) report the buffer as writable"
        );
    }

    #[test]
    fn translate_spirv_to_wgsl_marks_non_writable_buffer_read_only() {
        // naga's WGSL writer never spells out `read` explicitly for a
        // storage buffer: a bare `var<storage>` (no access qualifier)
        // *is* its read-only form, per the WGSL default; it only ever
        // writes out `read_write` or `atomic` explicitly. So proving
        // the fix worked means proving neither of those appears.
        let spirv = readonly_storage_buffer_spirv();
        let wgsl = translate_spirv_to_wgsl(&spirv).expect("fixture module should translate");
        assert!(
            wgsl.contains("var<storage>"),
            "expected a bare (read-only) storage buffer declaration in:\n{wgsl}"
        );
        assert!(
            !wgsl.contains("read_write") && !wgsl.contains("atomic"),
            "expected no writable storage buffer declaration in:\n{wgsl}"
        );
    }

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
