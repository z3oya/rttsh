//! rttsh-elf: the Rust home of rttsh's ELF domain (parse, symbol lookup, RTT
//! control-block location). Layer split: this file is the FFI surface (handle
//! lifecycle, the materialization blob, panic discipline); `parse` is the
//! hand-written ELF32 reader; `lookup`/`locate` are the semantics ports; `model`
//! holds the domain types. The C# facade (`ElfImage`, RttSh.Core.Rtt.Elf) owns
//! files, IO errors and the InvalidImage guard; this crate owns everything about
//! the bytes themselves. Conventions from the rttsh-mcp FFI discipline:
//!
//! - every export is `extern "C"`, returns a status code, and never lets a panic
//!   escape (`catch_unwind` is mandatory — this also means `panic = "abort"` must
//!   never be set for this crate)
//! - a negative return is the status ladder, a positive one a count — never mixed
//!   in one function; all domain results travel through out-params
//! - buffers are caller-allocated and NUL-terminated (reasons copy what fits);
//!   the materialization blob is the one Rust allocation — handed over by pointer,
//!   copied by C#, returned through `rttsh_elf_free` (same shape as the MCP
//!   dispatch response)
//! - all text is UTF-8; handles are pointer-sized (`Box::into_raw` of `ParsedElf`),
//!   single-owner, not thread-shared: the C# `SafeHandle` vouches for lifetime

use std::panic::{catch_unwind, AssertUnwindSafe};

mod locate;
mod lookup;
mod model;
mod parse;

pub use locate::{locate, Locate, LocateStatus};
pub use lookup::{lookup, Lookup, LookupStatus};
pub use model::{LoadStatus, ParsedElf, Section, Symbol, SymbolBinding, SymbolKind};
pub use parse::parse;

/// Status ladder, mirrored by the C# side in `ElfNative.cs`.
pub const OK: i32 = 0;
pub const ERR_INVALID_HANDLE: i32 = -1;
pub const ERR_BAD_ARGUMENT: i32 = -2;
pub const ERR_INTERNAL: i32 = -3;

/// Bumped when the FFI surface changes shape; the C# facade refuses to run against
/// a mismatched DLL (same fail-fast spirit as JLinkLibrary's version probe).
pub const ABI_VERSION: i32 = 1;

/// Every export body runs under `guard`: a panic anywhere becomes [`ERR_INTERNAL`]
/// instead of crossing the FFI boundary. The panic text goes to stderr.
fn guard(f: impl FnOnce() -> i32) -> i32 {
    match catch_unwind(AssertUnwindSafe(f)) {
        Ok(rc) => rc,
        Err(payload) => {
            let message = payload
                .downcast_ref::<&str>()
                .copied()
                .or_else(|| payload.downcast_ref::<String>().map(String::as_str))
                .unwrap_or("<non-string panic payload>");
            eprintln!("rttsh_elf_native: panic caught: {message}");
            ERR_INTERNAL
        }
    }
}

/// A cap without a buffer is a caller bug worth rejecting; anything else is
/// accepted (a non-null buffer with cap 0 is what an empty C# array marshals to,
/// and simply means "nothing to copy").
fn check_buffer(buf: *const u8, cap: u32) -> Result<(), i32> {
    if buf.is_null() && cap != 0 {
        Err(ERR_BAD_ARGUMENT)
    } else {
        Ok(())
    }
}

/// Copies `reason` into the caller's buffer, NUL-terminated, truncated to fit.
/// Preconditions: [`check_buffer`] passed for this buffer; `cap > 0`.
///
/// # Safety
/// `buf` must be writable for `cap` bytes when `cap > 0`.
unsafe fn write_reason(buf: *mut u8, cap: u32, reason: &str) {
    if cap == 0 {
        return;
    }
    let cap = cap as usize;
    let len = reason.len().min(cap - 1);
    unsafe {
        std::ptr::copy_nonoverlapping(reason.as_ptr(), buf, len);
        *buf.add(len) = 0;
    }
}

/// Runs `f` with the parsed image behind `handle`; `None` when the handle is null
/// (reported as [`ERR_INVALID_HANDLE`]). Use-after-free or a foreign pointer is
/// caller UB, per the standard `Box::into_raw` contract — the C# `SafeHandle` makes
/// double-free and use-after-free unreachable from managed code.
///
/// # Safety
/// `handle` must be a value produced by `rttsh_elf_parse` and not yet freed.
unsafe fn with_image<T>(handle: usize, f: impl FnOnce(&ParsedElf) -> T) -> Option<T> {
    if handle == 0 {
        return None;
    }
    Some(f(unsafe { &*(handle as *const ParsedElf) }))
}

/// Materialization blob handed to C# by `rttsh_elf_parse`. Layout (all integers
/// little-endian; the host is x64 so no byte-order negotiation):
///
/// ```text
/// u32 symbol_count
/// u32 section_count
/// u32 names_len
/// symbol_count × 32B: u32 name_off, u32 name_len, u64 address, u64 size,
///                     u8 kind, u8 binding, 6× zero
/// section_count × 40B: u32 name_off, u32 name_len, u64 address, u64 size,
///                      i64 file_offset, u8 has_contents, 7× zero
/// names_len bytes: concatenated UTF-8 names (name_off indexes from here)
/// ```
///
/// The blob exists only on the success path and only to cross the boundary once;
/// C# decodes it into its own records immediately and calls `rttsh_elf_free`.
/// The boxed slice is exactly sized (`into_boxed_slice` drops any excess capacity),
/// which is what makes the `rttsh_elf_free` reconstruction layout-correct.
/// Appends `name` to the pool and answers the (offset, length) pair the entry
/// fields record. Entries carry the pair, the pool carries the bytes.
fn add_name(names: &mut Vec<u8>, name: &str) -> (u32, u32) {
    let offset = names.len() as u32;
    names.extend_from_slice(name.as_bytes());
    (offset, name.len() as u32)
}

fn build_blob(image: &ParsedElf) -> Box<[u8]> {
    let mut names: Vec<u8> = Vec::new();
    let mut out = Vec::new();
    out.extend_from_slice(&0u32.to_le_bytes()); // symbol_count, backfilled
    out.extend_from_slice(&0u32.to_le_bytes()); // section_count, backfilled
    out.extend_from_slice(&0u32.to_le_bytes()); // names_len, backfilled

    for symbol in &image.symbols {
        let (name_off, name_len) = add_name(&mut names, &symbol.name);
        out.extend_from_slice(&name_off.to_le_bytes());
        out.extend_from_slice(&name_len.to_le_bytes());
        out.extend_from_slice(&symbol.address.to_le_bytes());
        out.extend_from_slice(&symbol.size.to_le_bytes());
        out.push(symbol.kind as u8);
        out.push(symbol.binding as u8);
        out.extend_from_slice(&[0u8; 6]);
    }
    for section in &image.sections {
        let (name_off, name_len) = add_name(&mut names, &section.name);
        out.extend_from_slice(&name_off.to_le_bytes());
        out.extend_from_slice(&name_len.to_le_bytes());
        out.extend_from_slice(&section.address.to_le_bytes());
        out.extend_from_slice(&section.size.to_le_bytes());
        out.extend_from_slice(&section.file_offset.to_le_bytes());
        out.push(u8::from(section.has_contents));
        out.extend_from_slice(&[0u8; 7]);
    }
    out.extend_from_slice(&names);

    let (sym_count, sect_count) = (image.symbols.len() as u32, image.sections.len() as u32);
    out[0..4].copy_from_slice(&sym_count.to_le_bytes());
    out[4..8].copy_from_slice(&sect_count.to_le_bytes());
    out[8..12].copy_from_slice(&(names.len() as u32).to_le_bytes());
    out.into_boxed_slice()
}

#[no_mangle]
pub extern "C" fn rttsh_elf_abi_version() -> i32 {
    ABI_VERSION
}

/// Parses an in-memory image and, on success, hands out both the model handle and
/// its materialization blob. Never fails with a panic; every image problem lands
/// on the [`LoadStatus`] ladder through `status_out` + `reason` (no blob, no
/// handle). IO never happens here - the bytes arrive in memory.
///
/// # Safety
/// `bytes` must be readable for `len` bytes; the out-pointers must be writable.
#[no_mangle]
pub unsafe extern "C" fn rttsh_elf_parse(
    bytes: *const u8,
    len: u32,
    status_out: *mut i32,
    is_little_endian_out: *mut u8,
    reason_buf: *mut u8,
    reason_cap: u32,
    blob_out: *mut *mut u8,
    blob_len_out: *mut u32,
    handle_out: *mut usize,
) -> i32 {
    guard(|| unsafe {
        if status_out.is_null()
            || is_little_endian_out.is_null()
            || blob_out.is_null()
            || blob_len_out.is_null()
            || handle_out.is_null()
            || (bytes.is_null() && len != 0)
        {
            return ERR_BAD_ARGUMENT;
        }
        if let Err(rc) = check_buffer(reason_buf, reason_cap) {
            return rc;
        }

        let image = if len == 0 {
            &[][..]
        } else {
            std::slice::from_raw_parts(bytes, len as usize)
        };
        *status_out = LoadStatus::ParseFailed as i32;
        *is_little_endian_out = 1; // the C# failed-image default
        *blob_out = std::ptr::null_mut();
        *blob_len_out = 0;
        *handle_out = 0;

        match parse(image) {
            Ok(model) => {
                let is_little_endian = model.little_endian;
                let blob = build_blob(&model);
                *status_out = LoadStatus::Ok as i32;
                *is_little_endian_out = u8::from(is_little_endian);
                *blob_out = blob.as_ptr() as *mut u8;
                *blob_len_out = blob.len() as u32;
                *handle_out = Box::into_raw(Box::new(model)) as usize;
                std::mem::forget(blob); // ownership handed over; freed via rttsh_elf_free
                write_reason(reason_buf, reason_cap, "");
                OK
            }
            Err((status, reason)) => {
                *status_out = status as i32;
                write_reason(reason_buf, reason_cap, &reason);
                OK
            }
        }
    })
}

/// Returns a blob produced by `rttsh_elf_parse` to this crate's allocator. Null is
/// a no-op; a freed or foreign pointer is caller UB. The blob is an exactly-sized
/// boxed slice (see [`build_blob`]), so reconstructing it with the reported length
/// reproduces the allocation layout precisely.
#[no_mangle]
pub extern "C" fn rttsh_elf_free(blob: *mut u8, len: u32) {
    let _ = guard(|| {
        if !blob.is_null() {
            // SAFETY: produced by `build_blob` and handed out exactly once; the
            // boxed slice guarantees capacity == len, so the dealloc layout matches.
            unsafe {
                drop(Box::from_raw(std::ptr::slice_from_raw_parts_mut(
                    blob,
                    len as usize,
                )))
            };
        }
        OK
    });
}

/// Looks a symbol up by exact name (see [`lookup`] for semantics). The kind filter
/// is the wire code of [`SymbolKind`], or [`SymbolKind::ANY`] (-1) for no filter.
/// On Found, the symbol fields are filled; on Ambiguous, candidates drain into
/// `cand_buf` (fixed 24-byte entries: u64 address, u64 size, u8 kind, u8 binding,
/// 6× zero; `cand_cap` is the number of entry slots) and `cand_count_out` reports
/// the ACTUAL count, so a truncated fill is visible (count > cap).
///
/// # Safety
/// `name` must be readable for `name_len` bytes; the out-pointers must be writable.
#[no_mangle]
pub unsafe extern "C" fn rttsh_elf_lookup(
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
) -> i32 {
    guard(|| unsafe {
        if status_out.is_null()
            || address_out.is_null()
            || size_out.is_null()
            || kind_out.is_null()
            || binding_out.is_null()
            || cand_count_out.is_null()
            || (name.is_null() && name_len != 0)
        {
            return ERR_BAD_ARGUMENT;
        }
        if let Err(rc) = check_buffer(cand_buf, cand_cap) {
            return rc;
        }
        let name = match std::str::from_utf8(if name_len == 0 {
            &[]
        } else {
            std::slice::from_raw_parts(name, name_len as usize)
        }) {
            Ok(text) => text,
            Err(_) => return ERR_BAD_ARGUMENT,
        };
        let kind = match SymbolKind::from_code(kind) {
            Some(wanted) => wanted,
            None => return ERR_BAD_ARGUMENT,
        };

        let result = match with_image(handle, |image| lookup(image, name, kind)) {
            Some(result) => result,
            None => return ERR_INVALID_HANDLE,
        };

        *status_out = result.status as i32;
        if let Some(symbol) = &result.found {
            *address_out = symbol.address;
            *size_out = symbol.size;
            *kind_out = symbol.kind as u8;
            *binding_out = symbol.binding as u8;
        }
        if result.status == LookupStatus::Ambiguous {
            let slots = cand_cap as usize;
            for (index, symbol) in result.candidates.iter().enumerate().take(slots) {
                let entry = cand_buf.add(index * 24);
                std::ptr::copy_nonoverlapping(candidate_entry(symbol).as_ptr(), entry, 24);
            }
        }
        *cand_count_out = result.candidates.len() as u32;
        OK
    })
}

fn candidate_entry(symbol: &Symbol) -> [u8; 24] {
    let mut entry = [0u8; 24];
    entry[0..8].copy_from_slice(&symbol.address.to_le_bytes());
    entry[8..16].copy_from_slice(&symbol.size.to_le_bytes());
    entry[16] = symbol.kind as u8;
    entry[17] = symbol.binding as u8;
    entry
}

/// Resolves the RTT control block (see [`locate`] for the policy and the verbatim
/// reason strings).
///
/// # Safety
/// The out-pointers must be writable; `reason_buf` follows [`check_buffer`].
#[no_mangle]
pub unsafe extern "C" fn rttsh_elf_locate(
    handle: usize,
    status_out: *mut i32,
    address_out: *mut u32,
    reason_buf: *mut u8,
    reason_cap: u32,
) -> i32 {
    guard(|| unsafe {
        if status_out.is_null() || address_out.is_null() {
            return ERR_BAD_ARGUMENT;
        }
        if let Err(rc) = check_buffer(reason_buf, reason_cap) {
            return rc;
        }
        let result = match with_image(handle, locate) {
            Some(result) => result,
            None => return ERR_INVALID_HANDLE,
        };
        *status_out = result.status as i32;
        *address_out = result.address;
        write_reason(reason_buf, reason_cap, &result.reason);
        OK
    })
}

/// Destroys the model behind `handle`; the handle is invalid afterwards. Null is
/// rejected ([`ERR_BAD_ARGUMENT`]) - a `SafeHandle` never holds null when
/// `ReleaseHandle` runs, so zero here means the caller desynchronized.
#[no_mangle]
pub extern "C" fn rttsh_elf_free_handle(handle: usize) -> i32 {
    guard(|| {
        if handle == 0 {
            return ERR_BAD_ARGUMENT;
        }
        // SAFETY: produced by `rttsh_elf_parse`; called exactly once per handle
        // (the C# SafeHandle owns the single release).
        unsafe { drop(Box::from_raw(handle as *mut ParsedElf)) };
        OK
    })
}

/// Test path for the panic discipline: panics inside the guard, must answer
/// [`ERR_INTERNAL`] (same shape as rttsh-mcp's panic_probe).
#[no_mangle]
pub extern "C" fn rttsh_elf_panic_probe() -> i32 {
    guard(|| panic!("panic_probe"))
}
