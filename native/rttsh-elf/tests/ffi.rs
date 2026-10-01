//! FFI glue: the ABI contract C# depends on. Status ladder discipline (domain
//! results via out-params, negative rc = errors), the blob layout round trip,
//! drain semantics on the candidate buffer, handle lifecycle, and the panic guard.
//! The extern declarations resolve against the crate's own rlib - the same symbols
//! the cdylib exports for the C# facade.

mod common;

use common::*;

const OK: i32 = 0;
const ERR_INVALID_HANDLE: i32 = -1;
const ERR_BAD_ARGUMENT: i32 = -2;
const ERR_INTERNAL: i32 = -3;

const STATUS_NOT_ELF: i32 = 1;
const STATUS_UNSUPPORTED_CLASS: i32 = 2;
const STATUS_PARSE_FAILED: i32 = 3;

const LOOKUP_FOUND: i32 = 0;
const LOOKUP_NOT_FOUND: i32 = 1;
const LOOKUP_AMBIGUOUS: i32 = 2;

const LOCATE_RESOLVED: i32 = 0;

const KIND_ANY: i32 = -1;

extern "C" {
    fn rttsh_elf_abi_version() -> i32;
    fn rttsh_elf_parse(
        bytes: *const u8,
        len: u32,
        status_out: *mut i32,
        is_little_endian_out: *mut u8,
        reason_buf: *mut u8,
        reason_cap: u32,
        blob_out: *mut *mut u8,
        blob_len_out: *mut u32,
        handle_out: *mut usize,
    ) -> i32;
    fn rttsh_elf_free(blob: *mut u8, len: u32);
    fn rttsh_elf_lookup(
        handle: usize,
        name: *const u8,
        name_len: u32,
        kind: i32,
        status_out: *mut i32,
        address_out: *mut u64,
        size_out: *mut u64,
        kind_out: *mut u8,
        binding_out: *mut u8,
        cand_buf: *mut u8,
        cand_cap: u32,
        cand_count_out: *mut u32,
    ) -> i32;
    fn rttsh_elf_locate(
        handle: usize,
        status_out: *mut i32,
        address_out: *mut u32,
        reason_buf: *mut u8,
        reason_cap: u32,
    ) -> i32;
    fn rttsh_elf_free_handle(handle: usize) -> i32;
    fn rttsh_elf_panic_probe() -> i32;
}

/// Keeps the export objects on the link line: an `extern "C"` declaration alone
/// does not make the linker pull the defining object out of the rlib archive, so
/// every export is also named through the Rust path here. The extern block above
/// is still what every call goes through - the same C ABI the C# facade uses.
#[used]
static KEEP_EXPORTS: (
    extern "C" fn() -> i32,
    unsafe extern "C" fn(
        *const u8,
        u32,
        *mut i32,
        *mut u8,
        *mut u8,
        u32,
        *mut *mut u8,
        *mut u32,
        *mut usize,
    ) -> i32,
    extern "C" fn(*mut u8, u32),
    unsafe extern "C" fn(
        usize,
        *const u8,
        u32,
        i32,
        *mut i32,
        *mut u64,
        *mut u64,
        *mut u8,
        *mut u8,
        *mut u8,
        u32,
        *mut u32,
    ) -> i32,
    unsafe extern "C" fn(usize, *mut i32, *mut u32, *mut u8, u32) -> i32,
    extern "C" fn(usize) -> i32,
    extern "C" fn() -> i32,
) = (
    rttsh_elf_native::rttsh_elf_abi_version,
    rttsh_elf_native::rttsh_elf_parse,
    rttsh_elf_native::rttsh_elf_free,
    rttsh_elf_native::rttsh_elf_lookup,
    rttsh_elf_native::rttsh_elf_locate,
    rttsh_elf_native::rttsh_elf_free_handle,
    rttsh_elf_native::rttsh_elf_panic_probe,
);

/// Reads the NUL-terminated reason out of a caller buffer.
fn read_reason(buf: &[u8]) -> String {
    let end = buf.iter().position(|&b| b == 0).expect("NUL-terminated");
    String::from_utf8_lossy(&buf[..end]).into_owned()
}

struct ParseOutcome {
    rc: i32,
    status: i32,
    little_endian: bool,
    reason: String,
    blob: Vec<u8>,
    handle: usize,
}

fn call_parse(bytes: &[u8], reason_cap: u32) -> ParseOutcome {
    unsafe {
        let mut status: i32 = 0;
        let mut little_endian: u8 = 0;
        let mut reason = vec![0u8; reason_cap as usize];
        let mut blob: *mut u8 = std::ptr::null_mut();
        let mut blob_len: u32 = 0;
        let mut handle: usize = 0;
        let rc = rttsh_elf_parse(
            bytes.as_ptr(),
            bytes.len() as u32,
            &mut status,
            &mut little_endian,
            reason.as_mut_ptr(),
            reason_cap,
            &mut blob,
            &mut blob_len,
            &mut handle,
        );
        let blob_vec = if blob.is_null() {
            Vec::new()
        } else {
            std::slice::from_raw_parts(blob, blob_len as usize).to_vec()
        };
        if !blob.is_null() {
            rttsh_elf_free(blob, blob_len);
        }
        ParseOutcome {
            rc,
            status,
            little_endian: little_endian != 0,
            reason: read_reason(&reason),
            blob: blob_vec,
            handle,
        }
    }
}

/// A successfully parsed image whose handle is released on drop.
struct Image {
    handle: usize,
}

impl Image {
    fn open(bytes: &[u8]) -> Image {
        let outcome = call_parse(bytes, 256);
        assert_eq!(outcome.rc, OK);
        assert_eq!(outcome.status, 0, "reason: {}", outcome.reason);
        assert!(outcome.handle != 0);
        Image {
            handle: outcome.handle,
        }
    }

    /// Returns (rc, found fields on Found, drained candidates, actual count, status).
    fn lookup(
        &self,
        name: &str,
        kind: i32,
        cand_cap: u32,
    ) -> (
        i32,
        Option<(u64, u64, u8, u8)>,
        Vec<(u64, u64, u8, u8)>,
        u32,
        i32,
    ) {
        unsafe {
            let mut status: i32 = 0;
            let mut address: u64 = 0;
            let mut size: u64 = 0;
            let mut kind_out: u8 = 0;
            let mut binding_out: u8 = 0;
            let mut candidates = vec![0u8; (cand_cap * 24) as usize];
            let mut candidate_count: u32 = 0;
            let rc = rttsh_elf_lookup(
                self.handle,
                name.as_ptr(),
                name.len() as u32,
                kind,
                &mut status,
                &mut address,
                &mut size,
                &mut kind_out,
                &mut binding_out,
                candidates.as_mut_ptr(),
                cand_cap,
                &mut candidate_count,
            );
            let mut entries = Vec::new();
            for slot in candidates.chunks_exact(24) {
                if entries.len() == candidate_count as usize {
                    break;
                }
                entries.push((
                    u64::from_le_bytes(slot[0..8].try_into().unwrap()),
                    u64::from_le_bytes(slot[8..16].try_into().unwrap()),
                    slot[16],
                    slot[17],
                ));
            }
            let found = (status == LOOKUP_FOUND).then_some((address, size, kind_out, binding_out));
            (rc, found, entries, candidate_count, status)
        }
    }

    fn locate(&self) -> (i32, i32, u32, String) {
        unsafe {
            let mut status: i32 = 0;
            let mut address: u32 = 0;
            let mut reason = [0u8; 256];
            let rc = rttsh_elf_locate(
                self.handle,
                &mut status,
                &mut address,
                reason.as_mut_ptr(),
                256,
            );
            (rc, status, address, read_reason(&reason))
        }
    }
}

impl Drop for Image {
    fn drop(&mut self) {
        unsafe {
            assert_eq!(rttsh_elf_free_handle(self.handle), OK);
        }
    }
}

fn image_with_symbols() -> Vec<u8> {
    ElfBuilder {
        data_sections: vec![DataSection {
            name: ".text".into(),
            address: 0x0800_0000,
            contents: vec![0x55; 12],
        }],
        symbols: vec![
            Sym::new("dup", 0x2400_1000, 4, TYPE_FUNC, BIND_GLOBAL),
            Sym::new("dup", 0x2400_0000, 8, TYPE_OBJECT, BIND_LOCAL),
            Sym::new("single", 0x2400_2000, 12, TYPE_OBJECT, BIND_GLOBAL),
        ],
        ..Default::default()
    }
    .build()
}

#[test]
fn abi_version_is_one() {
    unsafe {
        assert_eq!(rttsh_elf_abi_version(), 1);
    }
}

#[test]
fn parse_hands_out_handle_and_a_decodable_blob() {
    let bytes = ElfBuilder {
        symbols: vec![Sym::new(
            "_SEGGER_RTT",
            0x2400_0070,
            168,
            TYPE_OBJECT,
            BIND_GLOBAL,
        )],
        data_sections: vec![DataSection {
            name: ".text".into(),
            address: 0x0800_0000,
            contents: vec![0x55; 12],
        }],
        ..Default::default()
    }
    .build();
    unsafe {
        let outcome = call_parse(&bytes, 256);
        assert_eq!(outcome.rc, OK);
        assert_eq!(outcome.status, 0);
        assert!(outcome.little_endian);
        assert_eq!(outcome.reason, "");

        let blob = decode_blob(&outcome.blob);
        assert_eq!(blob.symbols.len(), 1);
        assert_eq!(blob.symbols[0].name, "_SEGGER_RTT");
        assert_eq!(blob.symbols[0].address, 0x2400_0070);
        assert_eq!(blob.symbols[0].size, 168);
        assert_eq!(blob.symbols[0].kind, 1); // Object
        assert_eq!(blob.symbols[0].binding, 1); // Global
                                                // .symtab/.strtab are named sections too - they belong to the list.
        assert_eq!(blob.sections.len(), 4);
        assert_eq!(blob.sections[0].name, ".text");
        assert!(blob.sections[0].has_contents);
        assert_eq!(blob.sections[0].address, 0x0800_0000);
        assert_eq!(blob.sections[0].size, 12);
        assert!(blob.sections[0].file_offset > 0);

        assert_eq!(rttsh_elf_free_handle(outcome.handle), OK);
    }
}

#[test]
fn big_endian_parse_reports_its_endianness() {
    let bytes = ElfBuilder {
        big_endian: true,
        symbols: vec![Sym::new("be", 4, 4, TYPE_OBJECT, BIND_LOCAL)],
        ..ElfBuilder::default()
    }
    .build();
    unsafe {
        let outcome = call_parse(&bytes, 64);
        assert_eq!(outcome.status, 0);
        assert!(!outcome.little_endian);
        assert_eq!(rttsh_elf_free_handle(outcome.handle), OK);
    }
}

#[test]
fn parse_failures_travel_through_status_and_reason_only() {
    let junk = call_parse(b"not an elf", 256);
    assert_eq!(junk.rc, OK);
    assert_eq!(junk.status, STATUS_NOT_ELF);
    assert_eq!(junk.reason, r"not an ELF image (bad \x7fELF magic)");
    assert!(junk.blob.is_empty());
    assert_eq!(junk.handle, 0);
    assert!(junk.little_endian); // the C# failed-image default

    let elf64 = ElfBuilder {
        elf64_header: true,
        ..ElfBuilder::default()
    }
    .build();
    assert_eq!(call_parse(&elf64, 256).status, STATUS_UNSUPPORTED_CLASS);

    let truncated: Vec<u8> = vec![0x7f, b'E', b'L', b'F', 1, 0, 0];
    let cut = call_parse(&truncated, 256);
    assert_eq!(cut.status, STATUS_PARSE_FAILED);
    assert_eq!(
        cut.reason,
        "malformed ELF: truncated before the class field"
    );
}

#[test]
fn a_too_small_reason_buffer_truncates_but_stays_terminated() {
    let tiny = call_parse(b"not an elf", 8);
    assert_eq!(tiny.reason, "not an ");
}

#[test]
fn lookup_ffi_semantics_and_drain() {
    let bytes = image_with_symbols();
    let image = Image::open(&bytes);

    let (rc, found, entries, count, status) = image.lookup("single", 1, 0);
    assert_eq!(rc, OK);
    assert_eq!(status, LOOKUP_FOUND);
    assert_eq!(count, 0);
    assert!(entries.is_empty());
    assert_eq!(found, Some((0x2400_2000, 12, 1, 1))); // Object / Global

    let (rc, found, entries, count, status) = image.lookup("dup", KIND_ANY, 8);
    assert_eq!(rc, OK);
    assert_eq!(status, LOOKUP_AMBIGUOUS);
    assert_eq!(count, 2);
    assert_eq!(found, None);
    assert_eq!(
        entries,
        vec![(0x2400_0000, 8, 1, 0), (0x2400_1000, 4, 2, 1)] // address order
    );

    // Drain: cap 1 slot with 2 candidates - the buffer holds the first, and the
    // actual count still reports 2 so the truncation is visible.
    let (rc, _, entries, count, _) = image.lookup("dup", KIND_ANY, 1);
    assert_eq!(rc, OK);
    assert_eq!(count, 2);
    assert_eq!(entries, vec![(0x2400_0000, 8, 1, 0)]);

    let (_, _, _, _, status) = image.lookup("absent", KIND_ANY, 0);
    assert_eq!(status, LOOKUP_NOT_FOUND);

    let (_, _, _, _, status) = image.lookup("", KIND_ANY, 0);
    assert_eq!(status, LOOKUP_NOT_FOUND);

    // Kind filter before uniqueness: the OBJECT query resolves despite the FUNC twin.
    let (_, found, _, count, status) = image.lookup("dup", 1, 0);
    assert_eq!(status, LOOKUP_FOUND);
    assert_eq!(count, 0);
    assert_eq!(found, Some((0x2400_0000, 8, 1, 0)));
}

#[test]
fn locate_ffi_carries_status_address_and_reason() {
    let bytes = ElfBuilder {
        symbols: vec![Sym::new(
            "_SEGGER_RTT",
            0x2400_0070,
            168,
            TYPE_OBJECT,
            BIND_GLOBAL,
        )],
        ..ElfBuilder::default()
    }
    .build();
    let image = Image::open(&bytes);
    let (rc, status, address, reason) = image.locate();
    assert_eq!(rc, OK);
    assert_eq!(status, LOCATE_RESOLVED);
    assert_eq!(address, 0x2400_0070);
    assert_eq!(
        reason,
        "resolved _SEGGER_RTT at 0x24000070 (size 168); verify the \"SEGGER RTT\" ID on target before trusting it"
    );
}

#[test]
fn bad_arguments_rejected_without_touching_outs() {
    unsafe {
        let image = Image::open(&image_with_symbols());

        let mut status: i32 = 0x7f;
        let rc = rttsh_elf_lookup(
            image.handle,
            b"dup".as_ptr(),
            3,
            7, // no such kind code
            &mut status,
            &mut 0u64,
            &mut 0u64,
            &mut 0u8,
            &mut 0u8,
            std::ptr::null_mut(),
            0,
            &mut 0u32,
        );
        assert_eq!(rc, ERR_BAD_ARGUMENT);
        assert_eq!(status, 0x7f); // untouched

        let mut count: u32 = 9;
        let rc = rttsh_elf_lookup(
            image.handle,
            b"dup".as_ptr(),
            3,
            KIND_ANY,
            &mut 0i32,
            &mut 0u64,
            &mut 0u64,
            &mut 0u8,
            &mut 0u8,
            std::ptr::null_mut(),
            4, // a cap without a buffer
            &mut count,
        );
        assert_eq!(rc, ERR_BAD_ARGUMENT);
        assert_eq!(count, 9); // untouched

        let rc = rttsh_elf_lookup(
            image.handle,
            b"\xff\xfe invalid utf8".as_ptr(),
            15,
            KIND_ANY,
            &mut 0i32,
            &mut 0u64,
            &mut 0u64,
            &mut 0u8,
            &mut 0u8,
            std::ptr::null_mut(),
            0,
            &mut 0u32,
        );
        assert_eq!(rc, ERR_BAD_ARGUMENT);
    }
}

#[test]
fn a_null_handle_is_invalid_everywhere() {
    unsafe {
        let mut status: i32 = 0;
        let rc = rttsh_elf_lookup(
            0,
            b"x".as_ptr(),
            1,
            KIND_ANY,
            &mut status,
            &mut 0u64,
            &mut 0u64,
            &mut 0u8,
            &mut 0u8,
            std::ptr::null_mut(),
            0,
            &mut 0u32,
        );
        assert_eq!(rc, ERR_INVALID_HANDLE);

        let rc = rttsh_elf_locate(0, &mut status, &mut 0u32, std::ptr::null_mut(), 0);
        assert_eq!(rc, ERR_INVALID_HANDLE);

        assert_eq!(rttsh_elf_free_handle(0), ERR_BAD_ARGUMENT);
    }
}

#[test]
fn null_bytes_with_a_length_is_rejected_but_zero_length_parses_as_not_elf() {
    unsafe {
        let mut status: i32 = 0;
        let mut handle: usize = 0;
        let rc = rttsh_elf_parse(
            std::ptr::null(),
            16,
            &mut status,
            &mut 0u8,
            std::ptr::null_mut(),
            0,
            &mut std::ptr::null_mut(),
            &mut 0u32,
            &mut handle,
        );
        assert_eq!(rc, ERR_BAD_ARGUMENT);

        let rc = rttsh_elf_parse(
            std::ptr::null(),
            0,
            &mut status,
            &mut 0u8,
            std::ptr::null_mut(),
            0,
            &mut std::ptr::null_mut(),
            &mut 0u32,
            &mut handle,
        );
        assert_eq!(rc, OK);
        assert_eq!(status, STATUS_NOT_ELF); // zero bytes: bad magic
        assert_eq!(handle, 0);
    }
}

#[test]
fn the_panic_guard_answers_err_internal() {
    unsafe {
        assert_eq!(rttsh_elf_panic_probe(), ERR_INTERNAL);
    }
}

#[test]
fn freeing_a_null_blob_is_a_no_op() {
    unsafe {
        rttsh_elf_free(std::ptr::null_mut(), 0);
    }
}
