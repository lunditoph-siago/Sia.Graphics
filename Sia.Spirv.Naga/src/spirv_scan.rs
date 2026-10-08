//! A minimal, standalone reader for the handful of SPIR-V opcodes
//! [`crate::apply_storage_access_decorations`] needs: enough to resolve
//! which (descriptor set, binding) pairs were declared `NonWritable`.
//!
//! This intentionally does not depend on `naga` — it is a pure
//! byte-in, data-out pipeline (`decode_words` -> `scan` ->
//! `read_only_bindings`) so each stage can be unit-tested with small,
//! hand-built inputs instead of a full assembled SPIR-V module.
//!
//! Opcodes and decorations referenced below are from the SPIR-V
//! specification, section 3 ("Binary Form") and the `Decoration` enum:
//! <https://registry.khronos.org/SPIR-V/specs/unified1/SPIRV.html>

use std::collections::{HashMap, HashSet};
use std::mem::size_of;

const SPIRV_MAGIC_LE: u32 = 0x0723_0203;
const SPIRV_MAGIC_BE: u32 = 0x0302_2307;
pub(crate) const HEADER_WORDS: usize = 5;

const OP_TYPE_POINTER: u16 = 32;
const OP_VARIABLE: u16 = 59;
const OP_DECORATE: u16 = 71;
const OP_MEMBER_DECORATE: u16 = 72;
const DECORATION_NON_WRITABLE: u32 = 24;
const DECORATION_BINDING: u32 = 33;
const DECORATION_DESCRIPTOR_SET: u32 = 34;

/// Decodes a raw SPIR-V module into 32-bit words, honoring whichever
/// byte order the header magic indicates. Returns `None` for anything
/// that is too short, misaligned, or doesn't start with a recognizable
/// SPIR-V magic number in either byte order.
pub(crate) fn decode_words(spirv: &[u8]) -> Option<Vec<u32>> {
    if spirv.len() < HEADER_WORDS * size_of::<u32>()
        || !spirv.len().is_multiple_of(size_of::<u32>())
    {
        return None;
    }
    // `SPIRV_MAGIC_BE` is what the magic looks like when a big-endian
    // module's bytes are misread as little-endian; seeing it tells us
    // to flip the byte order for every other word too.
    let big_endian = match u32::from_le_bytes(spirv[..size_of::<u32>()].try_into().unwrap()) {
        SPIRV_MAGIC_LE => false,
        SPIRV_MAGIC_BE => true,
        _ => return None,
    };
    Some(
        spirv
            .chunks_exact(size_of::<u32>())
            .map(|bytes| {
                let word: [u8; 4] = bytes.try_into().unwrap();
                if big_endian {
                    u32::from_be_bytes(word)
                } else {
                    u32::from_le_bytes(word)
                }
            })
            .collect(),
    )
}

#[derive(Default)]
struct ResourceDecorations {
    descriptor_set: Option<u32>,
    binding: Option<u32>,
    non_writable: bool,
}

/// Everything [`read_only_bindings`] needs, gathered from a single
/// linear pass over a module's instruction stream.
#[derive(Default)]
pub(crate) struct Annotations {
    decorations: HashMap<u32, ResourceDecorations>,
    non_writable_types: HashSet<u32>,
    pointer_types: HashMap<u32, u32>,
    variable_types: HashMap<u32, u32>,
}

/// Walks every instruction in `words` (a full module, header included)
/// and records the handful of opcodes relevant to resolving which
/// storage buffers were declared `NonWritable`. Returns `None` if the
/// instruction stream is truncated or self-inconsistent — a defensive
/// bailout; a module naga itself already accepted should never hit it.
pub(crate) fn scan(words: &[u32]) -> Option<Annotations> {
    let mut annotations = Annotations::default();
    let mut offset = HEADER_WORDS;
    while offset < words.len() {
        let instruction = words[offset];
        let word_count = (instruction >> 16) as usize;
        let opcode = instruction as u16;
        if word_count == 0 || offset + word_count > words.len() {
            return None;
        }
        let operands = &words[offset + 1..offset + word_count];
        match opcode {
            OP_TYPE_POINTER if operands.len() >= 3 => {
                annotations.pointer_types.insert(operands[0], operands[2]);
            }
            OP_VARIABLE if operands.len() >= 3 => {
                annotations.variable_types.insert(operands[1], operands[0]);
            }
            OP_DECORATE if operands.len() >= 2 => {
                let decoration = annotations.decorations.entry(operands[0]).or_default();
                match operands[1] {
                    DECORATION_NON_WRITABLE => decoration.non_writable = true,
                    DECORATION_BINDING if operands.len() >= 3 => {
                        decoration.binding = Some(operands[2]);
                    }
                    DECORATION_DESCRIPTOR_SET if operands.len() >= 3 => {
                        decoration.descriptor_set = Some(operands[2]);
                    }
                    _ => {}
                }
            }
            OP_MEMBER_DECORATE if operands.len() >= 3 && operands[2] == DECORATION_NON_WRITABLE => {
                annotations.non_writable_types.insert(operands[0]);
            }
            _ => {}
        }
        offset += word_count;
    }
    Some(annotations)
}

/// Resolves `annotations` down to the (descriptor set, binding) pairs
/// that were declared `NonWritable`, either directly on the resource
/// variable or on its pointee type (the common case: a `Block`-
/// decorated struct with a `NonWritable` member).
pub(crate) fn read_only_bindings(annotations: &Annotations) -> HashSet<(u32, u32)> {
    annotations
        .variable_types
        .iter()
        .filter_map(|(variable, pointer)| {
            let decoration = annotations.decorations.get(variable)?;
            let non_writable = decoration.non_writable
                || annotations
                    .pointer_types
                    .get(pointer)
                    .is_some_and(|target| annotations.non_writable_types.contains(target));
            non_writable.then_some((decoration.descriptor_set?, decoration.binding?))
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn header_words(magic_bytes: impl Fn(u32) -> [u8; 4]) -> Vec<u8> {
        [SPIRV_MAGIC_LE, 0x0001_0500, 0, 1, 0]
            .into_iter()
            .flat_map(magic_bytes)
            .collect()
    }

    #[test]
    fn decode_words_reads_little_endian() {
        let bytes = header_words(u32::to_le_bytes);
        let words = decode_words(&bytes).expect("valid little-endian header");
        assert_eq!(words, vec![SPIRV_MAGIC_LE, 0x0001_0500, 0, 1, 0]);
    }

    #[test]
    fn decode_words_reads_big_endian() {
        let bytes = header_words(u32::to_be_bytes);
        let words = decode_words(&bytes).expect("valid big-endian header");
        assert_eq!(words, vec![SPIRV_MAGIC_LE, 0x0001_0500, 0, 1, 0]);
    }

    #[test]
    fn decode_words_rejects_short_input() {
        assert!(decode_words(&[]).is_none());
        assert!(decode_words(&[0, 1, 2]).is_none());
    }

    #[test]
    fn decode_words_rejects_misaligned_input() {
        let mut bytes = header_words(u32::to_le_bytes);
        bytes.push(0); // 21 bytes: not a multiple of 4
        assert!(decode_words(&bytes).is_none());
    }

    #[test]
    fn decode_words_rejects_unrecognized_magic() {
        let bytes = 0xdead_beefu32.to_le_bytes().repeat(HEADER_WORDS);
        assert!(decode_words(&bytes).is_none());
    }

    /// Packs an opcode and its operands into a SPIR-V instruction,
    /// including the leading word-count/opcode word.
    fn instruction(opcode: u16, operands: &[u32]) -> Vec<u32> {
        let word_count = (operands.len() + 1) as u32;
        let mut words = vec![(word_count << 16) | opcode as u32];
        words.extend_from_slice(operands);
        words
    }

    fn module_words(instructions: &[Vec<u32>]) -> Vec<u32> {
        let mut words = vec![SPIRV_MAGIC_LE, 0, 0, 0, 0];
        for instruction in instructions {
            words.extend_from_slice(instruction);
        }
        words
    }

    const STORAGE_BUFFER_STORAGE_CLASS: u32 = 12;

    #[test]
    fn read_only_bindings_follows_member_non_writable() {
        // %block(3): OpMemberDecorate 0 NonWritable
        // %ptr(8) = OpTypePointer StorageBuffer %block(3)
        // %var(4) = OpVariable %ptr(8) StorageBuffer, at set 0 binding 2
        let words = module_words(&[
            instruction(OP_MEMBER_DECORATE, &[3, 0, DECORATION_NON_WRITABLE]),
            instruction(OP_DECORATE, &[4, DECORATION_DESCRIPTOR_SET, 0]),
            instruction(OP_DECORATE, &[4, DECORATION_BINDING, 2]),
            instruction(OP_TYPE_POINTER, &[8, STORAGE_BUFFER_STORAGE_CLASS, 3]),
            instruction(OP_VARIABLE, &[8, 4, STORAGE_BUFFER_STORAGE_CLASS]),
        ]);
        let annotations = scan(&words).expect("well-formed instruction stream");
        assert_eq!(read_only_bindings(&annotations), HashSet::from([(0, 2)]));
    }

    #[test]
    fn read_only_bindings_follows_direct_non_writable() {
        // %var(4), decorated NonWritable directly, at set 1 binding 0.
        let words = module_words(&[
            instruction(OP_DECORATE, &[4, DECORATION_NON_WRITABLE]),
            instruction(OP_DECORATE, &[4, DECORATION_DESCRIPTOR_SET, 1]),
            instruction(OP_DECORATE, &[4, DECORATION_BINDING, 0]),
            instruction(OP_TYPE_POINTER, &[8, STORAGE_BUFFER_STORAGE_CLASS, 3]),
            instruction(OP_VARIABLE, &[8, 4, STORAGE_BUFFER_STORAGE_CLASS]),
        ]);
        let annotations = scan(&words).expect("well-formed instruction stream");
        assert_eq!(read_only_bindings(&annotations), HashSet::from([(1, 0)]));
    }

    #[test]
    fn read_only_bindings_excludes_writable_buffers() {
        // %var(4) carries neither a direct nor a member NonWritable.
        let words = module_words(&[
            instruction(OP_DECORATE, &[4, DECORATION_DESCRIPTOR_SET, 0]),
            instruction(OP_DECORATE, &[4, DECORATION_BINDING, 0]),
            instruction(OP_TYPE_POINTER, &[8, STORAGE_BUFFER_STORAGE_CLASS, 3]),
            instruction(OP_VARIABLE, &[8, 4, STORAGE_BUFFER_STORAGE_CLASS]),
        ]);
        let annotations = scan(&words).expect("well-formed instruction stream");
        assert!(read_only_bindings(&annotations).is_empty());
    }

    #[test]
    fn read_only_bindings_ignores_a_variable_missing_set_or_binding() {
        // NonWritable, but no DescriptorSet/Binding decoration at all.
        let words = module_words(&[
            instruction(OP_DECORATE, &[4, DECORATION_NON_WRITABLE]),
            instruction(OP_TYPE_POINTER, &[8, STORAGE_BUFFER_STORAGE_CLASS, 3]),
            instruction(OP_VARIABLE, &[8, 4, STORAGE_BUFFER_STORAGE_CLASS]),
        ]);
        let annotations = scan(&words).expect("well-formed instruction stream");
        assert!(read_only_bindings(&annotations).is_empty());
    }

    #[test]
    fn scan_rejects_truncated_instruction() {
        // Claims a 5-word instruction but only supplies one more word.
        let mut words = vec![SPIRV_MAGIC_LE, 0, 0, 0, 0];
        words.push((5u32 << 16) | OP_DECORATE as u32);
        words.push(4);
        assert!(scan(&words).is_none());
    }

    #[test]
    fn scan_rejects_a_zero_length_instruction() {
        // A word count of zero would otherwise loop forever.
        let mut words = vec![SPIRV_MAGIC_LE, 0, 0, 0, 0];
        words.push(OP_DECORATE as u32); // word_count == 0
        assert!(scan(&words).is_none());
    }
}
