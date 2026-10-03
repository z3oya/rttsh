//! rttsh-lua: Lua 5.4 hosting behind a C ABI for rttsh's script subcommand.
//! mlua (lua54 + vendored) compiles the Lua 5.4 sources statically into
//! rttsh_lua_native.dll, replacing NLua + KeraLua + lua54.dll. The C# side
//! (`LuaNative.cs`, RttSh.Scripting) owns the rtt shim, argument validation and
//! error mapping; this crate owns the state, the HOST_* trampolines and the
//! string.find re-entrancy point. Conventions from the rttsh-mcp / rttsh-elf
//! FFI discipline:
//!
//! - every export is `extern "C"`, returns a status code, and never lets a
//!   panic escape (`catch_unwind` is mandatory — this also means
//!   `panic = "abort"` must never be set for this crate)
//! - a negative return is the status ladder, results travel through out-params
//! - all text is UTF-8; buffers handed over by pointer are exactly-sized
//!   allocations (see `alloc`/`free`); the state handle is pointer-sized
//!   (`Box::into_raw` of `Lua`), single-owner, not thread-shared: the C# host
//!   guarantees create, every do_string/find call and destroy stay on the one
//!   script thread
//! - host callbacks never throw across: each returns [`CB_OK`]/[`CB_ERR`] with
//!   the message (or a string value) in its out-pair, and the Lua shim raises
//!   script-side; rtt.exit unwinds as the `__rtt_exit=<code>` string sentinel

use std::ffi::c_void;
use std::panic::{catch_unwind, AssertUnwindSafe};

use mlua::{Error, Lua};

/// Status ladder, mirrored by the C# side in `LuaNative.cs`.
pub const OK: i32 = 0;
pub const ERR_INVALID_HANDLE: i32 = -1;
pub const ERR_BAD_ARGUMENT: i32 = -2;
pub const ERR_INTERNAL: i32 = -3;

/// Host-callback status: [`CB_OK`] = done, [`CB_ERR`] = failed with the message
/// in the entry's msg out-pair (or, for wait/wait_hex/expect, in the same
/// out-pair that would have carried the string value). The shim turns status 1
/// into a Lua error carrying that message.
pub const CB_OK: i32 = 0;
pub const CB_ERR: i32 = 1;

/// do_string's *kind_out: the chunk ran (KIND_OK), failed with a message
/// (KIND_ERROR), or unwound on the rtt.exit sentinel (KIND_EXIT, with the
/// parsed code). Only meaningful when the export returned OK.
pub const KIND_OK: i32 = 0;
pub const KIND_ERROR: i32 = 1;
pub const KIND_EXIT: i32 = 2;

/// The rtt.exit sentinel the shim raises as `error("__rtt_exit=<code>", 0)`;
/// classify parses it from the error's first line.
pub const EXIT_SENTINEL: &str = "__rtt_exit=";

/// Bumped when the FFI surface changes shape; the C# facade refuses to run
/// against a mismatched DLL.
pub const ABI_VERSION: i32 = 1;

/// The C# host callbacks behind the shim's HOST_* globals. Every entry returns
/// [`CB_OK`]/[`CB_ERR`] except `now` (pure computation, infallible). Out-pairs
/// carry either a value or, on failure, the error message — allocated by the
/// C# side through `rttsh_lua_alloc`, freed here after conversion. Numeric
/// args cross as i64 so the C# side re-validates ranges (neither layer trusts
/// the other's validation).
///
/// # Safety
/// `vtable` and everything reachable from it must outlive the handle created
/// with it and must not be mutated while the handle lives.
#[repr(C)]
pub struct HostVTable {
    /// Opaque context handed back to every callback (a GCHandle pointer C#-side).
    pub ctx: *mut c_void,
    /// Send text / hex bytes / a log line.
    pub send: unsafe extern "C" fn(ctx: *mut c_void, text: *const u8, len: usize, msg: *mut *mut u8, msg_len: *mut usize) -> i32,
    pub send_hex: unsafe extern "C" fn(ctx: *mut c_void, hex: *const u8, len: usize, msg: *mut *mut u8, msg_len: *mut usize) -> i32,
    pub log: unsafe extern "C" fn(ctx: *mut c_void, line: *const u8, len: usize, msg: *mut *mut u8, msg_len: *mut usize) -> i32,
    /// Wait passively up to timeout_ms; the out-pair returns the arrived text
    /// (ok) or the failure message (err).
    pub wait: unsafe extern "C" fn(ctx: *mut c_void, timeout_ms: i32, out: *mut *mut u8, out_len: *mut usize) -> i32,
    pub wait_hex: unsafe extern "C" fn(ctx: *mut c_void, timeout_ms: i32, out: *mut *mut u8, out_len: *mut usize) -> i32,
    /// Consume through the first match of pattern; the out-pair returns the
    /// matched text (ok) or the failure message (err).
    pub expect: unsafe extern "C" fn(ctx: *mut c_void, pattern: *const u8, pattern_len: usize, timeout_ms: i32, out: *mut *mut u8, out_len: *mut usize) -> i32,
    /// Milliseconds since the script started; cannot fail.
    pub now: unsafe extern "C" fn(ctx: *mut c_void) -> f64,
    pub sleep: unsafe extern "C" fn(ctx: *mut c_void, ms: i32, msg: *mut *mut u8, msg_len: *mut usize) -> i32,
    /// Record the exit code; always "succeeds" — the shim raises the
    /// `__rtt_exit=` sentinel right after (see the shim's exit entry).
    pub exit: unsafe extern "C" fn(ctx: *mut c_void, code: i64, msg: *mut *mut u8, msg_len: *mut usize) -> i32,
    /// Read count units of width bits; out-value pair returns the values as
    /// i64s (ok), msg pair the failure message (err).
    pub mem_read: unsafe extern "C" fn(ctx: *mut c_void, addr: i64, count: i64, width: i32, out: *mut *mut i64, out_len: *mut usize, msg: *mut *mut u8, msg_len: *mut usize) -> i32,
    pub mem_write_one: unsafe extern "C" fn(ctx: *mut c_void, addr: i64, value: i64, width: i32, msg: *mut *mut u8, msg_len: *mut usize) -> i32,
    /// `values` is `count` i64s the shim already extracted from its copy table.
    pub mem_write_table: unsafe extern "C" fn(ctx: *mut c_void, addr: i64, values: *const i64, count: usize, width: i32, msg: *mut *mut u8, msg_len: *mut usize) -> i32,
    /// Out-value: 0 or 1. msg pair on failure.
    pub is_halted: unsafe extern "C" fn(ctx: *mut c_void, out: *mut i32, msg: *mut *mut u8, msg_len: *mut usize) -> i32,
    pub halt: unsafe extern "C" fn(ctx: *mut c_void, msg: *mut *mut u8, msg_len: *mut usize) -> i32,
    pub resume: unsafe extern "C" fn(ctx: *mut c_void, msg: *mut *mut u8, msg_len: *mut usize) -> i32,
}

/// Every export body runs under `guard`: a panic anywhere becomes
/// [`ERR_INTERNAL`] instead of crossing the FFI boundary. The panic text goes
/// to stderr.
fn guard<T>(f: impl FnOnce() -> T) -> Result<T, i32> {
    match catch_unwind(AssertUnwindSafe(f)) {
        Ok(value) => Ok(value),
        Err(payload) => {
            let message = payload
                .downcast_ref::<&str>()
                .copied()
                .or_else(|| payload.downcast_ref::<String>().map(String::as_str))
                .unwrap_or("<non-string panic payload>");
            eprintln!("rttsh_lua_native: panic caught: {message}");
            Err(ERR_INTERNAL)
        }
    }
}

#[no_mangle]
pub extern "C" fn rttsh_lua_abi_version() -> i32 {
    ABI_VERSION
}

/// Turns an owned byte vector into an exactly-sized leaked allocation — the
/// discipline behind alloc/handover: capacity == len lets free reconstruct the
/// dealloc layout precisely (same shape as the elf blob).
fn leak_vec(bytes: Vec<u8>) -> *mut u8 {
    let boxed = bytes.into_boxed_slice();
    let ptr = boxed.as_ptr() as *mut u8;
    std::mem::forget(boxed);
    ptr
}

/// Allocates exactly `len` bytes with this crate's allocator, for the C# side
/// to hand strings/values over by pointer; the DLL frees after conversion.
/// Null means the allocation failed (treated as "no value" by the caller).
#[no_mangle]
pub extern "C" fn rttsh_lua_alloc(len: usize) -> *mut u8 {
    guard(|| leak_vec(vec![0u8; len])).unwrap_or(std::ptr::null_mut())
}

/// Frees an exactly-sized allocation from [`rttsh_lua_alloc`] (or a handover
/// received from an export). Null is a no-op.
///
/// # Safety
/// `ptr` must come from `rttsh_lua_alloc` or an export handover and must still
/// be owned by the caller.
#[no_mangle]
pub unsafe extern "C" fn rttsh_lua_free(ptr: *mut u8, len: usize) {
    if !ptr.is_null() {
        let _ = guard(|| {
            unsafe { drop(Box::from_raw(std::ptr::slice_from_raw_parts_mut(ptr, len))) };
            OK
        });
    }
}

/// Copies `len` bytes from `ptr` into an owned String (lossy UTF-8 — scripts
/// only ever produce valid UTF-8; lossy keeps a stray byte from aborting a run).
///
/// # Safety
/// `ptr` must be readable for `len` bytes and must not be mutated during the call.
unsafe fn host_string(ptr: *const u8, len: usize) -> String {
    if ptr.is_null() || len == 0 {
        return String::new();
    }
    unsafe { String::from_utf8_lossy(std::slice::from_raw_parts(ptr, len)).into_owned() }
}

/// Copies `text` into a fresh exactly-sized allocation and hands it over by
/// pointer (the caller frees through [`rttsh_lua_free`]). An empty text hands
/// over null/0 — the C# side treats that as "no message".
///
/// # Safety
/// Both out-pointers must be writable.
unsafe fn handover(out: *mut *mut u8, out_len: *mut usize, text: &str) {
    if text.is_empty() {
        unsafe {
            *out = std::ptr::null_mut();
            *out_len = 0;
        }
        return;
    }
    let ptr = leak_vec(text.as_bytes().to_vec());
    unsafe {
        *out = ptr;
        *out_len = text.len();
    }
}

/// Flattens an mlua error into (kind, exit code, message). CallbackError is
/// mlua's wrapper around conversion failures inside Rust callbacks — the cause
/// carries the real message. mlua appends the Lua stack traceback to runtime
/// error messages, so the `__rtt_exit=` sentinel is parsed from the first
/// line; the traceback stays in the message for diagnostics (NLua's
/// LuaException also carried it, and the script-facing tests only pin
/// substrings).
fn classify(e: &Error) -> (i32, i64, String) {
    let (kind, code, msg) = match e {
        Error::SyntaxError { message, .. } => (KIND_ERROR, 0, message.clone()),
        Error::RuntimeError(msg) => (KIND_ERROR, 0, msg.clone()),
        Error::CallbackError { cause, .. } => return classify(cause),
        other => (KIND_ERROR, 0, other.to_string()),
    };
    let first = msg.lines().next().unwrap_or("");
    if let Some(rest) = first.strip_prefix(EXIT_SENTINEL) {
        if let Ok(code) = rest.parse::<i64>() {
            return (KIND_EXIT, code, msg);
        }
    }
    (kind, code, msg)
}

/// Loads and runs one chunk with a name (Lua conventions: "@path" renders
/// errors as "path:line", "=eval" as "eval:line"). On success *kind_out is
/// KIND_OK. A script failure yields KIND_ERROR with the message handed over
/// via msg_out (freed by the caller through [`rttsh_lua_free`]); the rtt.exit
/// sentinel yields KIND_EXIT with the code in *code_out. The out-params are
/// only meaningful when the return is OK.
///
/// # Safety
/// `src`/`chunk` must be readable for their lengths and the out-pointers
/// writable; `handle` must come from `rttsh_lua_create`, be live, and all
/// calls on it stay on the one script thread (single-owner handle).
#[no_mangle]
pub unsafe extern "C" fn rttsh_lua_do_string(
    handle: usize,
    src: *const u8,
    src_len: usize,
    chunk: *const u8,
    chunk_len: usize,
    kind_out: *mut i32,
    code_out: *mut i64,
    msg_out: *mut *mut u8,
    msg_len_out: *mut usize,
) -> i32 {
    if handle == 0
        || src.is_null()
        || chunk.is_null()
        || kind_out.is_null()
        || code_out.is_null()
        || msg_out.is_null()
        || msg_len_out.is_null()
    {
        return ERR_BAD_ARGUMENT;
    }
    unsafe {
        *kind_out = KIND_OK;
        *code_out = 0;
        *msg_out = std::ptr::null_mut();
        *msg_len_out = 0;
    }
    let source = unsafe { host_string(src, src_len) };
    let chunk_name = unsafe { host_string(chunk, chunk_len) };
    guard(|| {
        // SAFETY: checked above — handle is nonzero and owned by the caller
        // (the C# host vouches it is live and script-thread-confined).
        let lua = unsafe { &*(handle as *const Lua) };
        match lua.load(source.as_bytes()).set_name(chunk_name).exec() {
            Ok(()) => {
                unsafe { *kind_out = KIND_OK };
                OK
            }
            Err(e) => {
                let (kind, code, msg) = classify(&e);
                unsafe {
                    *kind_out = kind;
                    *code_out = code;
                    handover(msg_out, msg_len_out, &msg);
                }
                OK
            }
        }
    })
    .unwrap_or(ERR_INTERNAL)
}

/// Creates a Lua 5.4 state with the standard libraries and returns a nonzero
/// handle for do_string/find/destroy, or 0 on failure. The vtable is validated
/// here and wired to the HOST_* trampolines as they land.
///
/// # Safety
/// `vtable` must outlive the handle; `ctx` inside it is dereferenced only by
/// the C#-owned callbacks.
#[no_mangle]
pub unsafe extern "C" fn rttsh_lua_create(_ctx: *mut c_void, vtable: *const HostVTable) -> usize {
    if vtable.is_null() {
        return 0;
    }
    guard(|| Box::into_raw(Box::new(Lua::new())) as usize).unwrap_or(0)
}

/// Destroys a handle from [`rttsh_lua_create`]; the handle is dead afterwards.
///
/// # Safety
/// `handle` must come from `rttsh_lua_create` and must be destroyed exactly once.
#[no_mangle]
pub unsafe extern "C" fn rttsh_lua_destroy(handle: usize) -> i32 {
    if handle == 0 {
        return ERR_BAD_ARGUMENT;
    }
    guard(|| {
        unsafe { drop(Box::from_raw(handle as *mut Lua)) };
        OK
    })
    .unwrap_or(ERR_INTERNAL)
}

#[cfg(test)]
mod tests {
    use super::*;

    // One stub per distinct vtable signature. They exist to prove the declared
    // field types compile; later rounds replace them with recording mocks.
    unsafe extern "C" fn s_str(_: *mut c_void, _: *const u8, _: usize, _: *mut *mut u8, _: *mut usize) -> i32 { CB_OK }
    unsafe extern "C" fn s_wait(_: *mut c_void, _: i32, _: *mut *mut u8, _: *mut usize) -> i32 { CB_OK }
    unsafe extern "C" fn s_expect(_: *mut c_void, _: *const u8, _: usize, _: i32, _: *mut *mut u8, _: *mut usize) -> i32 { CB_OK }
    unsafe extern "C" fn s_now(_: *mut c_void) -> f64 { 0.0 }
    unsafe extern "C" fn s_sleep(_: *mut c_void, _: i32, _: *mut *mut u8, _: *mut usize) -> i32 { CB_OK }
    unsafe extern "C" fn s_exit(_: *mut c_void, _: i64, _: *mut *mut u8, _: *mut usize) -> i32 { CB_OK }
    unsafe extern "C" fn s_mem_read(_: *mut c_void, _: i64, _: i64, _: i32, _: *mut *mut i64, _: *mut usize, _: *mut *mut u8, _: *mut usize) -> i32 { CB_OK }
    unsafe extern "C" fn s_mw_one(_: *mut c_void, _: i64, _: i64, _: i32, _: *mut *mut u8, _: *mut usize) -> i32 { CB_OK }
    unsafe extern "C" fn s_mw_table(_: *mut c_void, _: i64, _: *const i64, _: usize, _: i32, _: *mut *mut u8, _: *mut usize) -> i32 { CB_OK }
    unsafe extern "C" fn s_halted(_: *mut c_void, _: *mut i32, _: *mut *mut u8, _: *mut usize) -> i32 { CB_OK }
    unsafe extern "C" fn s_void(_: *mut c_void, _: *mut *mut u8, _: *mut usize) -> i32 { CB_OK }

    fn stub_vtable() -> Box<HostVTable> {
        Box::new(HostVTable {
            ctx: std::ptr::null_mut(),
            send: s_str,
            send_hex: s_str,
            log: s_str,
            wait: s_wait,
            wait_hex: s_wait,
            expect: s_expect,
            now: s_now,
            sleep: s_sleep,
            exit: s_exit,
            mem_read: s_mem_read,
            mem_write_one: s_mw_one,
            mem_write_table: s_mw_table,
            is_halted: s_halted,
            halt: s_void,
            resume: s_void,
        })
    }

    #[test]
    fn abi_version_matches() {
        assert_eq!(rttsh_lua_abi_version(), ABI_VERSION);
    }

    #[test]
    fn create_rejects_a_null_vtable() {
        assert_eq!(unsafe { rttsh_lua_create(std::ptr::null_mut(), std::ptr::null()) }, 0);
    }

    #[test]
    fn create_destroy_roundtrip() {
        let vtable = stub_vtable();
        let handle = unsafe { rttsh_lua_create(vtable.ctx, &*vtable) };
        assert_ne!(handle, 0);
        assert_eq!(unsafe { rttsh_lua_destroy(handle) }, OK);
    }

    #[test]
    fn destroy_rejects_zero() {
        assert_eq!(unsafe { rttsh_lua_destroy(0) }, ERR_BAD_ARGUMENT);
    }

    /// Runs one do_string round and flattens (rc, kind, code, message), freeing
    /// the handed-over message the way the C# side will.
    fn run_chunk(handle: usize, source: &str, chunk: &str) -> (i32, i32, i64, String) {
        let (src, name) = (source.as_bytes(), chunk.as_bytes());
        let mut kind = 0i32;
        let mut code = 0i64;
        let mut msg: *mut u8 = std::ptr::null_mut();
        let mut msg_len = 0usize;
        let rc = unsafe {
            rttsh_lua_do_string(
                handle,
                src.as_ptr(),
                src.len(),
                name.as_ptr(),
                name.len(),
                &mut kind,
                &mut code,
                &mut msg,
                &mut msg_len,
            )
        };
        let message = if msg.is_null() {
            String::new()
        } else {
            let text = unsafe { String::from_utf8_lossy(std::slice::from_raw_parts(msg, msg_len)).into_owned() };
            unsafe { rttsh_lua_free(msg, msg_len) };
            text
        };
        (rc, kind, code, message)
    }

    fn with_state(f: impl FnOnce(usize)) {
        let vtable = stub_vtable();
        let handle = unsafe { rttsh_lua_create(vtable.ctx, &*vtable) };
        assert_ne!(handle, 0);
        f(handle);
        assert_eq!(unsafe { rttsh_lua_destroy(handle) }, OK);
    }

    #[test]
    fn successful_chunk_yields_kind_ok_and_no_message() {
        with_state(|h| {
            let (rc, kind, code, msg) = run_chunk(h, "return 1", "=t");
            assert_eq!(rc, OK);
            assert_eq!(kind, KIND_OK);
            assert_eq!(code, 0);
            assert!(msg.is_empty());
        });
    }

    #[test]
    fn syntax_error_carries_the_chunk_name() {
        with_state(|h| {
            let (rc, kind, code, msg) = run_chunk(h, "this is not lua", "@script.lua");
            assert_eq!(rc, OK);
            assert_eq!(kind, KIND_ERROR);
            assert_eq!(code, 0);
            assert!(msg.contains("script.lua"), "got: {msg}");
        });
    }

    #[test]
    fn runtime_error_carries_position_and_message() {
        with_state(|h| {
            let (rc, kind, _, msg) = run_chunk(h, "error('boom')", "@script.lua");
            assert_eq!(rc, OK);
            assert_eq!(kind, KIND_ERROR);
            assert!(msg.contains("script.lua:1:"), "got: {msg}");
            assert!(msg.contains("boom"), "got: {msg}");
        });
    }

    #[test]
    fn exit_sentinel_unwinds_as_kind_exit_with_the_code() {
        with_state(|h| {
            let (rc, kind, code, msg) = run_chunk(h, "error('__rtt_exit=42', 0)", "@script.lua");
            assert_eq!(rc, OK);
            assert_eq!(kind, KIND_EXIT);
            assert_eq!(code, 42);
            assert!(msg.contains("__rtt_exit=42"), "got: {msg}"); // kept for diagnostics
        });
    }

    #[test]
    fn sentinel_text_mid_message_stays_an_error() {
        with_state(|h| {
            // position prefix lands before the sentinel text, so only a
            // first-line sentinel counts as an exit
            let (rc, kind, code, msg) = run_chunk(h, "error('boom __rtt_exit=9', 1)", "@script.lua");
            assert_eq!(rc, OK);
            assert_eq!(kind, KIND_ERROR);
            assert_eq!(code, 0);
            assert!(msg.contains("boom"), "got: {msg}");
        });
    }

    #[test]
    fn do_string_rejects_null_arguments() {
        with_state(|h| {
            let src = b"return 1";
            let mut kind = 0i32;
            let mut code = 0i64;
            let mut msg: *mut u8 = std::ptr::null_mut();
            let mut msg_len = 0usize;
            unsafe {
                assert_eq!(
                    rttsh_lua_do_string(h, std::ptr::null(), src.len(), src.as_ptr(), src.len(), &mut kind, &mut code, &mut msg, &mut msg_len),
                    ERR_BAD_ARGUMENT
                );
                assert_eq!(
                    rttsh_lua_do_string(h, src.as_ptr(), src.len(), src.as_ptr(), src.len(), std::ptr::null_mut(), &mut code, &mut msg, &mut msg_len),
                    ERR_BAD_ARGUMENT
                );
            }
        });
    }
}
