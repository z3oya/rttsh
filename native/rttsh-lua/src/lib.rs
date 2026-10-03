//! rttsh-lua: Lua 5.4 hosting behind a C ABI for rttsh's script subcommand.
//! mlua (lua54 + vendored) compiles the Lua 5.4 sources statically into
//! rttsh_lua_native.dll. The C# side
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

use mlua::{Error, Function, Lua, MultiValue, Table, Value};

/// Status ladder, mirrored by the C# side in `LuaNative.cs`.
pub const OK: i32 = 0;
pub const ERR_INVALID_HANDLE: i32 = -1;
pub const ERR_BAD_ARGUMENT: i32 = -2;
pub const ERR_INTERNAL: i32 = -3;

/// Host-callback status: [`CB_OK`] = done, [`CB_ERR`] = failed with the message
/// in the entry's msg out-pair (or, for wait/wait_hex/expect, in the same
/// out-pair that would have carried the string value). The shim turns status 1
/// into a Lua error carrying that message. [`CB_TIMEOUT`] = the call completed
/// but produced "no result" - the soft expect timeout; the out-pair carries the
/// timeout message (what prefix + buffer tail) and the shim turns the pair into
/// (nil, msg). read_line rides the same soft path via expect(flags=1).
pub const CB_OK: i32 = 0;
pub const CB_ERR: i32 = 1;
pub const CB_TIMEOUT: i32 = 2;

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
pub const ABI_VERSION: i32 = 2;

/// One element of expect_any's pattern array — `{ ptr, len }` of a UTF-8
/// pattern, mirroring `NativeByteSlice` in LuaNative.cs. The strings stay
/// owned by the Rust Vec for the call's duration; nothing is freed here.
#[repr(C)]
pub struct ByteSlice {
    pub ptr: *const u8,
    pub len: usize,
}

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
    /// matched text (ok), the timeout message (CB_TIMEOUT, flags = 1) or the
    /// failure message (err). flags: 0 = hard timeout (CB_ERR), 1 = soft
    /// timeout (CB_TIMEOUT) - the shim's try_expect / read_line path.
    pub expect: unsafe extern "C" fn(ctx: *mut c_void, pattern: *const u8, pattern_len: usize, timeout_ms: i32, flags: i32, out: *mut *mut u8, out_len: *mut usize) -> i32,
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
    /// Consume and discard everything received and not yet matched.
    pub flush: unsafe extern "C" fn(ctx: *mut c_void, msg: *mut *mut u8, msg_len: *mut usize) -> i32,
    /// Succeed (CB_OK) only if pattern stays absent for the whole window; a hit
    /// consumes through the match end and fails with the evidence message.
    pub expect_absent: unsafe extern "C" fn(ctx: *mut c_void, pattern: *const u8, pattern_len: usize, timeout_ms: i32, msg: *mut *mut u8, msg_len: *mut usize) -> i32,
    /// `patterns` is `count` ByteSlices; index_out is the 0-based winner, the
    /// out-pair the consumed text (ok) or the failure message (err).
    pub expect_any: unsafe extern "C" fn(ctx: *mut c_void, patterns: *const ByteSlice, count: usize, timeout_ms: i32, index_out: *mut i64, out: *mut *mut u8, out_len: *mut usize, msg: *mut *mut u8, msg_len: *mut usize) -> i32,
}

/// Copyable view of the vtable handed to `rttsh_lua_create`, captured by every
/// HOST_* closure. The C# host guarantees single-thread use, matching mlua's
/// non-Send Lua.
#[derive(Clone, Copy)]
struct Host(*const HostVTable);

/// Converts and frees a buffer the C# callback handed over through its
/// out-pair (the failure message, or the string value for wait/expect).
///
/// # Safety
/// `ptr` must come from the callback's out-pair and still be owned by the caller.
unsafe fn take_handover(ptr: *mut u8, len: usize) -> String {
    if ptr.is_null() || len == 0 {
        return String::new();
    }
    let text = unsafe { String::from_utf8_lossy(std::slice::from_raw_parts(ptr, len)).into_owned() };
    unsafe { free(ptr, len) };
    text
}

/// The shim's second return for status-style entries: the message on failure,
/// nil on success.
fn msg_or_nil(lua: &Lua, st: i32, msg: &str) -> mlua::Result<Value> {
    if st == CB_ERR {
        Ok(Value::String(lua.create_string(msg)?))
    } else {
        Ok(Value::Nil)
    }
}

/// The state's own pattern engine (string.find) — expect's matcher runs here
/// so the script-visible pattern semantics are exactly Lua 5.4's.
fn string_find(lua: &Lua) -> mlua::Result<Function> {
    let string: Table = lua.globals().get("string")?;
    string.get("find")
}

/// Registers the shim's HOST_* globals backed by `host`. The shim (run by the
/// C# host right after create) wraps these into the rtt table and turns
/// CB_ERR statuses into script-facing errors.
fn register_host(lua: &Lua, host: Host) -> mlua::Result<()> {
    let globals = lua.globals();

    globals.set(
        "HOST_send",
        lua.create_function(move |lua, text: String| {
            let (st, ptr, len) = unsafe {
                let mut msg: *mut u8 = std::ptr::null_mut();
                let mut msg_len: usize = 0;
                let st = ((*host.0).send)((*host.0).ctx, text.as_ptr(), text.len(), &mut msg, &mut msg_len);
                (st, msg, msg_len)
            };
            let msg = unsafe { take_handover(ptr, len) };
            Ok((st, msg_or_nil(lua, st, &msg)?))
        })?,
    )?;

    globals.set(
        "HOST_send_hex",
        lua.create_function(move |lua, hex: String| {
            let (st, ptr, len) = unsafe {
                let mut msg: *mut u8 = std::ptr::null_mut();
                let mut msg_len: usize = 0;
                let st = ((*host.0).send_hex)((*host.0).ctx, hex.as_ptr(), hex.len(), &mut msg, &mut msg_len);
                (st, msg, msg_len)
            };
            let msg = unsafe { take_handover(ptr, len) };
            Ok((st, msg_or_nil(lua, st, &msg)?))
        })?,
    )?;

    globals.set(
        "HOST_log",
        lua.create_function(move |lua, line: String| {
            let (st, ptr, len) = unsafe {
                let mut msg: *mut u8 = std::ptr::null_mut();
                let mut msg_len: usize = 0;
                let st = ((*host.0).log)((*host.0).ctx, line.as_ptr(), line.len(), &mut msg, &mut msg_len);
                (st, msg, msg_len)
            };
            let msg = unsafe { take_handover(ptr, len) };
            Ok((st, msg_or_nil(lua, st, &msg)?))
        })?,
    )?;

    globals.set(
        "HOST_now",
        lua.create_function(move |_, ()| Ok(unsafe { ((*host.0).now)((*host.0).ctx) }))?,
    )?;

    globals.set(
        "HOST_sleep",
        lua.create_function(move |lua, ms: i32| {
            let (st, ptr, len) = unsafe {
                let mut msg: *mut u8 = std::ptr::null_mut();
                let mut msg_len: usize = 0;
                let st = ((*host.0).sleep)((*host.0).ctx, ms, &mut msg, &mut msg_len);
                (st, msg, msg_len)
            };
            let msg = unsafe { take_handover(ptr, len) };
            Ok((st, msg_or_nil(lua, st, &msg)?))
        })?,
    )?;

    globals.set(
        "HOST_is_halted",
        lua.create_function(move |lua, ()| {
            let (st, halted, ptr, len) = unsafe {
                let mut out: i32 = 0;
                let mut msg: *mut u8 = std::ptr::null_mut();
                let mut msg_len: usize = 0;
                let st = ((*host.0).is_halted)((*host.0).ctx, &mut out, &mut msg, &mut msg_len);
                (st, out, msg, msg_len)
            };
            if st == CB_OK {
                return Ok((st, Value::Boolean(halted != 0)));
            }
            let msg = unsafe { take_handover(ptr, len) };
            Ok((st, Value::String(lua.create_string(msg)?)))
        })?,
    )?;

    globals.set(
        "HOST_halt",
        lua.create_function(move |lua, ()| {
            let (st, ptr, len) = unsafe {
                let mut msg: *mut u8 = std::ptr::null_mut();
                let mut msg_len: usize = 0;
                let st = ((*host.0).halt)((*host.0).ctx, &mut msg, &mut msg_len);
                (st, msg, msg_len)
            };
            let msg = unsafe { take_handover(ptr, len) };
            Ok((st, msg_or_nil(lua, st, &msg)?))
        })?,
    )?;

    globals.set(
        "HOST_resume",
        lua.create_function(move |lua, ()| {
            let (st, ptr, len) = unsafe {
                let mut msg: *mut u8 = std::ptr::null_mut();
                let mut msg_len: usize = 0;
                let st = ((*host.0).resume)((*host.0).ctx, &mut msg, &mut msg_len);
                (st, msg, msg_len)
            };
            let msg = unsafe { take_handover(ptr, len) };
            Ok((st, msg_or_nil(lua, st, &msg)?))
        })?,
    )?;

    globals.set(
        "HOST_exit",
        lua.create_function(move |lua, code: i64| {
            let (st, ptr, len) = unsafe {
                let mut msg: *mut u8 = std::ptr::null_mut();
                let mut msg_len: usize = 0;
                let st = ((*host.0).exit)((*host.0).ctx, code, &mut msg, &mut msg_len);
                (st, msg, msg_len)
            };
            let msg = unsafe { take_handover(ptr, len) };
            Ok((st, msg_or_nil(lua, st, &msg)?))
        })?,
    )?;

    globals.set(
        "HOST_wait",
        lua.create_function(move |lua, ms: i32| {
            let (st, ptr, len) = unsafe {
                let mut out: *mut u8 = std::ptr::null_mut();
                let mut out_len: usize = 0;
                let st = ((*host.0).wait)((*host.0).ctx, ms, &mut out, &mut out_len);
                (st, out, out_len)
            };
            let text = unsafe { take_handover(ptr, len) };
            Ok((st, Value::String(lua.create_string(text)?)))
        })?,
    )?;

    globals.set(
        "HOST_wait_hex",
        lua.create_function(move |lua, ms: i32| {
            let (st, ptr, len) = unsafe {
                let mut out: *mut u8 = std::ptr::null_mut();
                let mut out_len: usize = 0;
                let st = ((*host.0).wait_hex)((*host.0).ctx, ms, &mut out, &mut out_len);
                (st, out, out_len)
            };
            let text = unsafe { take_handover(ptr, len) };
            Ok((st, Value::String(lua.create_string(text)?)))
        })?,
    )?;

    globals.set(
        "HOST_expect",
        lua.create_function(move |lua, (pattern, timeout, flags): (String, i32, i32)| {
            let (st, ptr, len) = unsafe {
                let mut out: *mut u8 = std::ptr::null_mut();
                let mut out_len: usize = 0;
                let st = ((*host.0).expect)((*host.0).ctx, pattern.as_ptr(), pattern.len(), timeout, flags, &mut out, &mut out_len);
                (st, out, out_len)
            };
            let text = unsafe { take_handover(ptr, len) };
            Ok((st, Value::String(lua.create_string(text)?)))
        })?,
    )?;

    globals.set(
        "HOST_flush",
        lua.create_function(move |lua, ()| {
            let (st, ptr, len) = unsafe {
                let mut msg: *mut u8 = std::ptr::null_mut();
                let mut msg_len: usize = 0;
                let st = ((*host.0).flush)((*host.0).ctx, &mut msg, &mut msg_len);
                (st, msg, msg_len)
            };
            let msg = unsafe { take_handover(ptr, len) };
            Ok((st, msg_or_nil(lua, st, &msg)?))
        })?,
    )?;

    globals.set(
        "HOST_expect_absent",
        lua.create_function(move |lua, (pattern, timeout): (String, i32)| {
            let (st, ptr, len) = unsafe {
                let mut msg: *mut u8 = std::ptr::null_mut();
                let mut msg_len: usize = 0;
                let st = ((*host.0).expect_absent)((*host.0).ctx, pattern.as_ptr(), pattern.len(), timeout, &mut msg, &mut msg_len);
                (st, msg, msg_len)
            };
            let msg = unsafe { take_handover(ptr, len) };
            Ok((st, msg_or_nil(lua, st, &msg)?))
        })?,
    )?;

    globals.set(
        "HOST_expect_any",
        lua.create_function(move |lua, (patterns, timeout): (Table, i32)| {
            // the extracted Vec owns the pattern strings for the call's duration
            let items: Vec<String> = patterns.sequence_values::<String>().collect::<mlua::Result<_>>()?;
            let slices: Vec<ByteSlice> = items
                .iter()
                .map(|s| ByteSlice { ptr: s.as_ptr(), len: s.len() })
                .collect();
            let (st, ptr, len, mptr, mlen, index) = unsafe {
                let mut index: i64 = -1;
                let mut out: *mut u8 = std::ptr::null_mut();
                let mut out_len: usize = 0;
                let mut msg: *mut u8 = std::ptr::null_mut();
                let mut msg_len: usize = 0;
                let st = ((*host.0).expect_any)(
                    (*host.0).ctx,
                    slices.as_ptr(),
                    slices.len(),
                    timeout,
                    &mut index,
                    &mut out,
                    &mut out_len,
                    &mut msg,
                    &mut msg_len,
                );
                (st, out, out_len, msg, msg_len, index)
            };
            if st == CB_OK {
                let text = unsafe { take_handover(ptr, len) };
                return Ok((st, Value::String(lua.create_string(text)?), index));
            }
            let msg = unsafe { take_handover(mptr, mlen) };
            Ok((st, Value::String(lua.create_string(msg)?), index))
        })?,
    )?;

    globals.set(
        "HOST_mem_read",
        lua.create_function(move |lua, (addr, count, width): (i64, i64, i32)| {
            let (st, ptr, len, mptr, mlen) = unsafe {
                let mut out: *mut i64 = std::ptr::null_mut();
                let mut out_len: usize = 0;
                let mut msg: *mut u8 = std::ptr::null_mut();
                let mut msg_len: usize = 0;
                let st = ((*host.0).mem_read)((*host.0).ctx, addr, count, width, &mut out, &mut out_len, &mut msg, &mut msg_len);
                (st, out, out_len, msg, msg_len)
            };
            if st != CB_OK {
                let msg = unsafe { take_handover(mptr, mlen) };
                return Ok((st, Value::String(lua.create_string(msg)?)));
            }
            let table = lua.create_table()?;
            // The C# side hands over exactly out_len i64s (nothing for count 0).
            if !ptr.is_null() && len > 0 {
                let values = unsafe { std::slice::from_raw_parts(ptr, len) };
                for (i, v) in values.iter().enumerate() {
                    table.raw_set(i as i64 + 1, *v)?;
                }
                unsafe { free(ptr as *mut u8, len * 8) };
            }
            Ok((st, Value::Table(table)))
        })?,
    )?;

    globals.set(
        "HOST_mem_write_one",
        lua.create_function(move |lua, (addr, value, width): (i64, i64, i32)| {
            let (st, ptr, len) = unsafe {
                let mut msg: *mut u8 = std::ptr::null_mut();
                let mut msg_len: usize = 0;
                let st = ((*host.0).mem_write_one)((*host.0).ctx, addr, value, width, &mut msg, &mut msg_len);
                (st, msg, msg_len)
            };
            let msg = unsafe { take_handover(ptr, len) };
            Ok((st, msg_or_nil(lua, st, &msg)?))
        })?,
    )?;

    globals.set(
        "HOST_mem_write_table",
        lua.create_function(move |lua, (addr, values, _count, width): (i64, Table, i64, i32)| {
            // The shim hands over its dense 1..n copy table; the extracted
            // Vec's length is the count the C# side sees (it re-validates).
            let numbers: Vec<i64> = values.sequence_values::<i64>().collect::<mlua::Result<_>>()?;
            let (st, ptr, len) = unsafe {
                let mut msg: *mut u8 = std::ptr::null_mut();
                let mut msg_len: usize = 0;
                let st = ((*host.0).mem_write_table)((*host.0).ctx, addr, numbers.as_ptr(), numbers.len(), width, &mut msg, &mut msg_len);
                (st, msg, msg_len)
            };
            let msg = unsafe { take_handover(ptr, len) };
            Ok((st, msg_or_nil(lua, st, &msg)?))
        })?,
    )?;

    Ok(())
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
        let _ = guard(|| unsafe { free(ptr, len) });
    }
}

/// # Safety
/// `ptr` must come from a `leak_vec` allocation and still be owned by the caller.
unsafe fn free(ptr: *mut u8, len: usize) {
    unsafe { drop(Box::from_raw(std::ptr::slice_from_raw_parts_mut(ptr, len))) };
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
/// line; the traceback stays in the message for diagnostics.
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

/// Calls string.find(region, pattern, 1, false) on the state — the
/// PatternMatcher entry point. On OK, *end_out is 0 for no match, else the
/// 1-based inclusive end (== the chars to consume). CB_ERR marks a malformed
/// pattern with the message handed over. Re-entrant by design: expect's host
/// callback calls this while do_string is still on the stack (standard Lua
/// re-entrancy — expect runs on the same thread that owns the state).
///
/// # Safety
/// `region`/`pattern` must be readable for their lengths and the out-pointers
/// writable; `handle` must come from `rttsh_lua_create`, be live, and all
/// calls on it stay on the one script thread.
#[no_mangle]
pub unsafe extern "C" fn rttsh_lua_find(
    handle: usize,
    region: *const u8,
    region_len: usize,
    pattern: *const u8,
    pattern_len: usize,
    end_out: *mut i32,
    msg_out: *mut *mut u8,
    msg_len_out: *mut usize,
) -> i32 {
    if handle == 0
        || region.is_null()
        || pattern.is_null()
        || end_out.is_null()
        || msg_out.is_null()
        || msg_len_out.is_null()
    {
        return ERR_BAD_ARGUMENT;
    }
    unsafe {
        *end_out = -1;
        *msg_out = std::ptr::null_mut();
        *msg_len_out = 0;
    }
    let region = unsafe { host_string(region, region_len) };
    let pattern = unsafe { host_string(pattern, pattern_len) };
    guard(|| {
        // SAFETY: checked above — handle is nonzero and owned by the caller.
        let lua = unsafe { &*(handle as *const Lua) };
        let find = match string_find(lua) {
            Ok(f) => f,
            Err(_) => return ERR_INTERNAL,
        };
        match find.call::<MultiValue>((region, pattern, 1, false)) {
            Ok(vals) => {
                let mut it = vals.into_iter();
                let end = match it.next() {
                    None | Some(Value::Nil) => 0,
                    Some(_) => match it.next() {
                        Some(Value::Integer(end)) => end as i32,
                        _ => 0,
                    },
                };
                unsafe { *end_out = end };
                OK
            }
            Err(e) => {
                let (_, _, msg) = classify(&e);
                unsafe { handover(msg_out, msg_len_out, &msg) };
                CB_ERR
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
    match guard(|| {
        let lua = Lua::new();
        register_host(&lua, Host(vtable)).map_err(|_| ERR_INTERNAL)?;
        Ok::<usize, i32>(Box::into_raw(Box::new(lua)) as usize)
    }) {
        Ok(Ok(handle)) => handle,
        _ => 0,
    }
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
    use std::cell::{Cell, RefCell};
    use std::sync::Mutex;

    use super::*;

    /// The exact shim the C# host installs (src/RttSh/Scripting/rtt_shim.lua,
    /// embedded there as an assembly resource). The include path escapes the
    /// package dir on purpose; cargo tracks the file via dep-info.
    const SHIM: &str = include_str!("../../../src/RttSh/Scripting/rtt_shim.lua");

    /// Recording mock the tests route callbacks through.
    struct MockCtx {
        sent: Mutex<Vec<String>>,
        log: Mutex<Vec<String>>,
        slept: Mutex<Vec<i32>>,
        control: Mutex<Vec<&'static str>>,
        /// (addr, values, width) the mem_write entries received.
        written: Mutex<Vec<(i64, Vec<i64>, i32)>>,
        now_ms: Cell<f64>,
        exit_code: Cell<i64>,
        halted: Cell<i32>,
        /// (addr, count, width) of the last mem_read.
        last_read: Cell<(i64, i64, i32)>,
        /// Handle back-reference so mock_expect can re-enter rttsh_lua_find
        /// (set right after create — the PatternMatcher shape).
        handle: Cell<usize>,
        /// Text wait/wait_hex hand over on success; the region expect matches.
        wait_text: RefCell<String>,
        region: RefCell<String>,
        /// Values mem_read returns on success.
        read_values: RefCell<Vec<i64>>,
        /// When armed, every status-style entry fails with this message —
        /// exercises the CB_ERR → shim error path.
        fail: Cell<Option<&'static str>>,
        /// Number of flush calls (flush carries no data itself).
        flushed: Cell<u32>,
        /// expect_absent: true = absence succeeds, false = the pattern "appeared".
        absent_ok: Cell<bool>,
        /// expect_any hands over (index, text) on success; None = CB_ERR no-match.
        any_result: RefCell<Option<(i64, String)>>,
        /// expect: armed = a failed find yields CB_TIMEOUT with this message
        /// (the soft path); disarmed = a failed find yields CB_OK with "".
        soft_timeout: Cell<Option<&'static str>>,
    }

    impl MockCtx {
        fn new() -> Self {
            Self {
                sent: Mutex::new(Vec::new()),
                log: Mutex::new(Vec::new()),
                slept: Mutex::new(Vec::new()),
                control: Mutex::new(Vec::new()),
                written: Mutex::new(Vec::new()),
                now_ms: Cell::new(12.5),
                exit_code: Cell::new(0),
                halted: Cell::new(0),
                last_read: Cell::new((0, 0, 0)),
                handle: Cell::new(0),
                wait_text: RefCell::new(String::new()),
                region: RefCell::new(String::new()),
                read_values: RefCell::new(Vec::new()),
                fail: Cell::new(None),
                flushed: Cell::new(0),
                absent_ok: Cell::new(true),
                any_result: RefCell::new(None),
                soft_timeout: Cell::new(None),
            }
        }

        /// Hands over the armed failure message, or reports success.
        fn fail_or(&self, msg: *mut *mut u8, msg_len: *mut usize) -> i32 {
            match self.fail.get() {
                Some(m) => {
                    unsafe { handover(msg, msg_len, m) };
                    CB_ERR
                }
                None => CB_OK,
            }
        }
    }

    unsafe extern "C" fn mock_send(ctx: *mut c_void, text: *const u8, len: usize, msg: *mut *mut u8, msg_len: *mut usize) -> i32 {
        let c = unsafe { &*(ctx as *const MockCtx) };
        c.sent.lock().unwrap().push(unsafe { host_string(text, len) });
        c.fail_or(msg, msg_len)
    }

    unsafe extern "C" fn mock_send_hex(ctx: *mut c_void, hex: *const u8, len: usize, msg: *mut *mut u8, msg_len: *mut usize) -> i32 {
        let c = unsafe { &*(ctx as *const MockCtx) };
        c.sent.lock().unwrap().push(unsafe { host_string(hex, len) });
        c.fail_or(msg, msg_len)
    }

    unsafe extern "C" fn mock_log(ctx: *mut c_void, line: *const u8, len: usize, msg: *mut *mut u8, msg_len: *mut usize) -> i32 {
        let c = unsafe { &*(ctx as *const MockCtx) };
        c.log.lock().unwrap().push(unsafe { host_string(line, len) });
        c.fail_or(msg, msg_len)
    }

    unsafe extern "C" fn mock_now(ctx: *mut c_void) -> f64 {
        unsafe { (*(ctx as *const MockCtx)).now_ms.get() }
    }

    unsafe extern "C" fn mock_sleep(ctx: *mut c_void, ms: i32, msg: *mut *mut u8, msg_len: *mut usize) -> i32 {
        let c = unsafe { &*(ctx as *const MockCtx) };
        c.slept.lock().unwrap().push(ms);
        c.fail_or(msg, msg_len)
    }

    unsafe extern "C" fn mock_exit(ctx: *mut c_void, code: i64, msg: *mut *mut u8, msg_len: *mut usize) -> i32 {
        let c = unsafe { &*(ctx as *const MockCtx) };
        c.exit_code.set(code);
        c.fail_or(msg, msg_len)
    }

    unsafe extern "C" fn mock_is_halted(ctx: *mut c_void, out: *mut i32, msg: *mut *mut u8, msg_len: *mut usize) -> i32 {
        let c = unsafe { &*(ctx as *const MockCtx) };
        let st = c.fail_or(msg, msg_len);
        if st == CB_OK {
            unsafe { *out = c.halted.get() };
        }
        st
    }

    unsafe extern "C" fn mock_halt(ctx: *mut c_void, msg: *mut *mut u8, msg_len: *mut usize) -> i32 {
        let c = unsafe { &*(ctx as *const MockCtx) };
        c.control.lock().unwrap().push("halt");
        c.fail_or(msg, msg_len)
    }

    unsafe extern "C" fn mock_resume(ctx: *mut c_void, msg: *mut *mut u8, msg_len: *mut usize) -> i32 {
        let c = unsafe { &*(ctx as *const MockCtx) };
        c.control.lock().unwrap().push("resume");
        c.fail_or(msg, msg_len)
    }

    /// wait/wait_hex share one shape: the out-pair carries the value (ok) or
    /// the failure message (err).
    unsafe extern "C" fn mock_wait(ctx: *mut c_void, _timeout: i32, out: *mut *mut u8, out_len: *mut usize) -> i32 {
        let c = unsafe { &*(ctx as *const MockCtx) };
        if let Some(m) = c.fail.get() {
            unsafe { handover(out, out_len, m) };
            return CB_ERR;
        }
        let text = c.wait_text.borrow().clone();
        unsafe { handover(out, out_len, &text) };
        CB_OK
    }

    /// The PatternMatcher shape: re-enters rttsh_lua_find on the same state
    /// while do_string is still on the stack. Hands over the matched prefix
    /// text (what the production Expect returns); with soft_timeout armed, a
    /// failed find yields CB_TIMEOUT.
    unsafe extern "C" fn mock_expect(
        ctx: *mut c_void, pattern: *const u8, pattern_len: usize, _timeout: i32, flags: i32, out: *mut *mut u8, out_len: *mut usize,
    ) -> i32 {
        let c = unsafe { &*(ctx as *const MockCtx) };
        let region = c.region.borrow().clone();
        let mut end = -1i32;
        let mut fmsg: *mut u8 = std::ptr::null_mut();
        let mut fmsg_len = 0usize;
        let rc = unsafe {
            rttsh_lua_find(
                c.handle.get(),
                region.as_ptr(),
                region.len(),
                pattern,
                pattern_len,
                &mut end,
                &mut fmsg,
                &mut fmsg_len,
            )
        };
        if rc == OK {
            if end <= 0 {
                if let Some(m) = c.soft_timeout.get() {
                    unsafe { handover(out, out_len, m) };
                    return if flags == 0 { CB_ERR } else { CB_TIMEOUT };
                }
                unsafe { handover(out, out_len, "") };
                return CB_OK;
            }
            let text = region[..end as usize].to_string();
            unsafe { handover(out, out_len, &text) };
            return CB_OK;
        }
        if rc == CB_ERR {
            let msg = unsafe { take_handover(fmsg, fmsg_len) };
            unsafe { handover(out, out_len, &msg) };
            return CB_ERR;
        }
        unsafe { handover(out, out_len, "expect: find infra failure") };
        CB_ERR
    }

    unsafe extern "C" fn mock_flush(ctx: *mut c_void, msg: *mut *mut u8, msg_len: *mut usize) -> i32 {
        let c = unsafe { &*(ctx as *const MockCtx) };
        c.flushed.set(c.flushed.get() + 1);
        c.fail_or(msg, msg_len)
    }

    unsafe extern "C" fn mock_expect_absent(
        ctx: *mut c_void, _pattern: *const u8, _pattern_len: usize, _timeout: i32, msg: *mut *mut u8, msg_len: *mut usize,
    ) -> i32 {
        let c = unsafe { &*(ctx as *const MockCtx) };
        if let Some(m) = c.fail.get() {
            unsafe { handover(msg, msg_len, m) };
            return CB_ERR;
        }
        if c.absent_ok.get() {
            return CB_OK;
        }
        unsafe { handover(msg, msg_len, "expect_absent: 'busy' appeared: \"busy\"") };
        CB_ERR
    }

    unsafe extern "C" fn mock_expect_any(
        ctx: *mut c_void, _patterns: *const ByteSlice, _count: usize, _timeout: i32, index_out: *mut i64,
        out: *mut *mut u8, out_len: *mut usize, msg: *mut *mut u8, msg_len: *mut usize,
    ) -> i32 {
        let c = unsafe { &*(ctx as *const MockCtx) };
        if let Some(m) = c.fail.get() {
            unsafe { *index_out = -1 };
            unsafe { handover(msg, msg_len, m) };
            return CB_ERR;
        }
        match c.any_result.borrow().clone() {
            Some((index, text)) => {
                unsafe { *index_out = index };
                unsafe { handover(out, out_len, &text) };
                CB_OK
            }
            None => {
                unsafe { *index_out = -1 };
                unsafe { handover(msg, msg_len, "expect_any: none matched") };
                CB_ERR
            }
        }
    }

    unsafe extern "C" fn mock_mem_read(
        ctx: *mut c_void, addr: i64, count: i64, width: i32, out: *mut *mut i64, out_len: *mut usize, msg: *mut *mut u8, msg_len: *mut usize,
    ) -> i32 {
        let c = unsafe { &*(ctx as *const MockCtx) };
        if let Some(m) = c.fail.get() {
            unsafe { handover(msg, msg_len, m) };
            return CB_ERR;
        }
        c.last_read.set((addr, count, width));
        let requested: Vec<i64> = c.read_values.borrow().iter().take(count.max(0) as usize).copied().collect();
        if requested.is_empty() {
            unsafe {
                *out = std::ptr::null_mut();
                *out_len = 0;
            }
            return CB_OK;
        }
        let bytes: Vec<u8> = requested.iter().flat_map(|v| v.to_le_bytes()).collect();
        let ptr = leak_vec(bytes);
        unsafe {
            *out = ptr as *mut i64;
            *out_len = requested.len();
        }
        CB_OK
    }

    unsafe extern "C" fn mock_mem_write_one(ctx: *mut c_void, addr: i64, value: i64, width: i32, msg: *mut *mut u8, msg_len: *mut usize) -> i32 {
        let c = unsafe { &*(ctx as *const MockCtx) };
        c.written.lock().unwrap().push((addr, vec![value], width));
        c.fail_or(msg, msg_len)
    }

    unsafe extern "C" fn mock_mem_write_table(
        ctx: *mut c_void, addr: i64, values: *const i64, count: usize, width: i32, msg: *mut *mut u8, msg_len: *mut usize,
    ) -> i32 {
        let c = unsafe { &*(ctx as *const MockCtx) };
        let copy = if count > 0 && !values.is_null() {
            unsafe { std::slice::from_raw_parts(values, count) }.to_vec()
        } else {
            Vec::new()
        };
        c.written.lock().unwrap().push((addr, copy, width));
        c.fail_or(msg, msg_len)
    }

    fn mock_vtable(ctx: &MockCtx) -> Box<HostVTable> {
        Box::new(HostVTable {
            ctx: ctx as *const MockCtx as *mut c_void,
            send: mock_send,
            send_hex: mock_send_hex,
            log: mock_log,
            wait: mock_wait,
            wait_hex: mock_wait,
            expect: mock_expect,
            now: mock_now,
            sleep: mock_sleep,
            exit: mock_exit,
            mem_read: mock_mem_read,
            mem_write_one: mock_mem_write_one,
            mem_write_table: mock_mem_write_table,
            is_halted: mock_is_halted,
            halt: mock_halt,
            resume: mock_resume,
            flush: mock_flush,
            expect_absent: mock_expect_absent,
            expect_any: mock_expect_any,
        })
    }

    fn with_mock(f: impl FnOnce(&MockCtx, usize)) {
        let ctx = MockCtx::new();
        let vtable = mock_vtable(&ctx);
        let handle = unsafe { rttsh_lua_create(vtable.ctx, &*vtable) };
        assert_ne!(handle, 0);
        f(&ctx, handle);
        assert_eq!(unsafe { rttsh_lua_destroy(handle) }, OK);
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
        with_mock(|_, handle| assert_ne!(handle, 0));
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
        with_mock(|_, handle| f(handle));
    }

    /// Installs the shim then runs `script` as @script.lua (the host's Run shape).
    fn run_script(handle: usize, script: &str) -> (i32, i32, i64, String) {
        let (rc, kind, _, msg) = run_chunk(handle, SHIM, "=shim");
        assert_eq!(rc, OK, "shim do_string infra failure");
        assert_eq!(kind, KIND_OK, "shim failed: {msg}");
        run_chunk(handle, script, "@script.lua")
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

    #[test]
    fn simple_apis_roundtrip_through_the_shim() {
        with_mock(|ctx, h| {
            ctx.halted.set(1);
            let (rc, kind, _, msg) = run_script(
                h,
                "rtt.send('x') rtt.log('y') rtt.sleep(5) rtt.halt() \
                 local halted = rtt.is_halted() rtt.resume() \
                 rtt.log(tostring(halted)) rtt.log(rtt.now())",
            );
            assert_eq!(rc, OK, "{msg}");
            assert_eq!(kind, KIND_OK, "{msg}");
            assert_eq!(*ctx.sent.lock().unwrap(), vec!["x"]);
            assert_eq!(*ctx.log.lock().unwrap(), vec!["y", "true", "12.5"]);
            assert_eq!(*ctx.slept.lock().unwrap(), vec![5]);
            assert_eq!(*ctx.control.lock().unwrap(), vec!["halt", "resume"]);
        });
    }

    #[test]
    fn exit_unwinds_with_the_code_and_stops_the_script() {
        with_mock(|ctx, h| {
            let (rc, kind, code, msg) = run_script(h, "rtt.send('a') rtt.exit(42) rtt.send('b')");
            assert_eq!(rc, OK);
            assert_eq!(kind, KIND_EXIT, "{msg}");
            assert_eq!(code, 42);
            assert_eq!(ctx.exit_code.get(), 42);
            assert_eq!(*ctx.sent.lock().unwrap(), vec!["a"]); // 'b' never runs
        });
    }

    #[test]
    fn pcalld_exit_is_swallowed_but_still_recorded() {
        with_mock(|ctx, h| {
            let (rc, kind, _, msg) = run_script(h, "pcall(rtt.exit, 7) rtt.send('after')");
            assert_eq!(rc, OK);
            assert_eq!(kind, KIND_OK, "{msg}");
            assert_eq!(ctx.exit_code.get(), 7);
            assert_eq!(*ctx.sent.lock().unwrap(), vec!["after"]);
        });
    }

    #[test]
    fn host_failure_status_surfaces_as_a_script_error() {
        with_mock(|ctx, h| {
            ctx.fail.set(Some("mock failure"));
            // caught by pcall through protect: message + caller position survive;
            // re-raised with error() (not a status-style callback) for observation
            let (rc, kind, _, msg) = run_script(h, "local ok, err = pcall(function() rtt.send('x') end) error(err, 0)");
            assert_eq!(rc, OK);
            assert_eq!(kind, KIND_ERROR);
            assert!(msg.contains("mock failure"), "got: {msg}");
            assert!(msg.contains("script.lua:1:"), "got: {msg}");
            // uncaught direct call: the same classification at the top level
            let (rc, kind, _, msg) = run_script(h, "rtt.send('x')");
            assert_eq!(rc, OK);
            assert_eq!(kind, KIND_ERROR);
            assert!(msg.contains("mock failure"), "got: {msg}");
            assert!(msg.contains("script.lua:1:"), "got: {msg}");
        });
    }

    #[test]
    fn wait_wait_hex_and_expect_hand_strings_over() {
        with_mock(|ctx, h| {
            ctx.handle.set(h);
            ctx.region.replace("off end".into());
            ctx.wait_text.replace("v=1\n".into());
            let (rc, kind, _, msg) = run_script(
                h,
                "rtt.log('[' .. rtt.wait(100) .. ']') rtt.log(rtt.wait_hex(100)) rtt.log(rtt.expect('o[f][f]'))",
            );
            assert_eq!(rc, OK, "{msg}");
            assert_eq!(kind, KIND_OK, "{msg}");
            assert_eq!(*ctx.log.lock().unwrap(), vec!["[v=1\n]", "v=1\n", "off"]);
        });
    }

    #[test]
    fn mem_read_builds_a_table_and_mem_write_extracts_values() {
        with_mock(|ctx, h| {
            ctx.read_values.replace(vec![1_000_000_000, 2_000_000_000, 3_000_000_000]);
            let (rc, kind, _, msg) = run_script(
                h,
                "rtt.log(#rtt.mem_read(0, 0, 32)) \
                 local t = rtt.mem_read(0x40000000, 3, 32) \
                 local sum = 0 for i = 1, #t do sum = sum + t[i] end rtt.log(sum) \
                 rtt.mem_write(0x10, {10, 20, 30}, 8) rtt.mem_write(0x20, 42, 16)",
            );
            assert_eq!(rc, OK, "{msg}");
            assert_eq!(kind, KIND_OK, "{msg}");
            assert_eq!(*ctx.log.lock().unwrap(), vec!["0", "6000000000"]); // i64-exact, > 2^32
            assert_eq!(ctx.last_read.get(), (0x40000000, 3, 32));
            assert_eq!(*ctx.written.lock().unwrap(), vec![(0x10, vec![10, 20, 30], 8), (0x20, vec![42], 16)]);
        });
    }

    #[test]
    fn expect_reenters_find_on_the_same_state() {
        with_mock(|ctx, h| {
            ctx.handle.set(h);
            ctx.region.replace("off end".into());
            let (rc, kind, _, msg) = run_script(h, "rtt.log(rtt.expect('o[f][f]'))");
            assert_eq!(rc, OK, "{msg}");
            assert_eq!(kind, KIND_OK, "{msg}");
            assert_eq!(*ctx.log.lock().unwrap(), vec!["off"]); // the matched prefix text
        });
    }

    #[test]
    fn expect_hands_match_and_captures_as_multiple_values() {
        with_mock(|ctx, h| {
            ctx.handle.set(h);
            ctx.region.replace("async1 #18 accepted".into());
            let (rc, kind, _, msg) = run_script(
                h,
                "local text, m, id = rtt.expect('async1 #(%d+) accepted') \
                 rtt.log(text) rtt.log(m) rtt.log(id)",
            );
            assert_eq!(rc, OK, "{msg}");
            assert_eq!(kind, KIND_OK, "{msg}");
            assert_eq!(*ctx.log.lock().unwrap(), vec!["async1 #18 accepted", "async1 #18 accepted", "18"]);
        });
    }

    #[test]
    fn expect_rejects_non_string_patterns_with_its_own_name() {
        with_mock(|_, h| {
            let (rc, kind, _, msg) = run_script(h, "rtt.expect(42)");
            assert_eq!(rc, OK);
            assert_eq!(kind, KIND_ERROR);
            assert!(msg.contains("expect: pattern must be a string"), "got: {msg}");
        });
    }

    #[test]
    fn mem_read_without_count_returns_a_scalar() {
        with_mock(|ctx, h| {
            ctx.read_values.replace(vec![0x1234_5678]);
            let (rc, kind, _, msg) = run_script(h, "rtt.log(rtt.mem_read(0x40000000))");
            assert_eq!(rc, OK, "{msg}");
            assert_eq!(kind, KIND_OK, "{msg}");
            assert_eq!(*ctx.log.lock().unwrap(), vec!["305419896"]); // 0x12345678
            assert_eq!(ctx.last_read.get(), (0x40000000, 1, 32));
        });
    }

    #[test]
    fn set_timeout_reports_a_missing_argument_by_name() {
        with_mock(|_, h| {
            let (rc, kind, _, msg) = run_script(h, "rtt.set_timeout()");
            assert_eq!(rc, OK);
            assert_eq!(kind, KIND_ERROR);
            assert!(msg.contains("set_timeout: missing timeout in ms"), "got: {msg}");
        });
    }

    #[test]
    fn malformed_pattern_surfaces_through_expect() {
        with_mock(|ctx, h| {
            ctx.handle.set(h);
            ctx.region.replace("x".into());
            let (rc, kind, _, msg) = run_script(h, "rtt.expect('[')");
            assert_eq!(rc, OK);
            assert_eq!(kind, KIND_ERROR);
            assert!(msg.contains("malformed"), "got: {msg}");
        });
    }

    #[test]
    fn try_expect_returns_nil_and_message_on_the_soft_timeout() {
        with_mock(|ctx, h| {
            ctx.handle.set(h);
            ctx.region.replace("nothing here".into());
            ctx.soft_timeout.set(Some("try_expect: 'nope' not found within 100 ms"));
            let (rc, kind, _, msg) = run_script(
                h,
                "local a, b = rtt.try_expect('nope', 100) \
                 rtt.log(tostring(a)) rtt.log(b)",
            );
            assert_eq!(rc, OK, "{msg}");
            assert_eq!(kind, KIND_OK, "{msg}");
            assert_eq!(*ctx.log.lock().unwrap(), vec!["nil", "try_expect: 'nope' not found within 100 ms"]);
        });
    }

    #[test]
    fn read_line_strips_the_line_ending() {
        // the mock's region is static (no consumption modeling); sequential
        // reads are pinned C#-side against the real ScriptRuntime buffer
        with_mock(|ctx, h| {
            ctx.handle.set(h);
            ctx.region.replace("abc\r\ndef".into());
            let (rc, kind, _, msg) = run_script(h, "rtt.log(rtt.read_line(100))");
            assert_eq!(rc, OK, "{msg}");
            assert_eq!(kind, KIND_OK, "{msg}");
            assert_eq!(*ctx.log.lock().unwrap(), vec!["abc"]);
        });
        with_mock(|ctx, h| {
            ctx.handle.set(h);
            ctx.region.replace("\nrest".into());
            let (rc, kind, _, msg) = run_script(h, "rtt.log('[' .. rtt.read_line(100) .. ']')");
            assert_eq!(rc, OK, "{msg}");
            assert_eq!(kind, KIND_OK, "{msg}");
            // an empty line is "" - distinct from a nil timeout
            assert_eq!(*ctx.log.lock().unwrap(), vec!["[]"]);
        });
    }

    #[test]
    fn expect_absent_succeeds_on_a_quiet_window_and_fails_on_a_hit() {
        with_mock(|ctx, h| {
            ctx.absent_ok.set(true);
            let (rc, kind, _, msg) = run_script(h, "rtt.expect_absent('busy', 100)");
            assert_eq!(rc, OK, "{msg}");
            assert_eq!(kind, KIND_OK, "{msg}");

            ctx.absent_ok.set(false);
            let (rc, kind, _, msg) = run_script(h, "rtt.expect_absent('busy', 100)");
            assert_eq!(rc, OK);
            assert_eq!(kind, KIND_ERROR);
            assert!(msg.contains("appeared"), "got: {msg}");
        });
    }

    #[test]
    fn expect_any_returns_the_one_based_index_and_the_winner_captures() {
        with_mock(|ctx, h| {
            ctx.any_result.replace(Some((1, "ERR: hot".into())));
            let (rc, kind, _, msg) = run_script(
                h,
                "local i, t, m, c = rtt.expect_any(500, 'ok', 'ERR: (%a+)') \
                 rtt.log(i) rtt.log(t) rtt.log(m) rtt.log(c)",
            );
            assert_eq!(rc, OK, "{msg}");
            assert_eq!(kind, KIND_OK, "{msg}");
            // index 0-based 1 → shim +1 = 2; m = the whole match, c = the capture
            assert_eq!(*ctx.log.lock().unwrap(), vec!["2", "ERR: hot", "ERR: hot", "hot"]);
        });
    }

    #[test]
    fn expect_any_rejects_non_string_patterns_with_its_own_name() {
        with_mock(|_, h| {
            let (rc, kind, _, msg) = run_script(h, "rtt.expect_any(100, 'ok', 42)");
            assert_eq!(rc, OK);
            assert_eq!(kind, KIND_ERROR);
            assert!(msg.contains("expect_any: pattern #2 must be a string"), "got: {msg}");
        });
    }

    #[test]
    fn flush_records_the_call() {
        with_mock(|ctx, h| {
            let (rc, kind, _, msg) = run_script(h, "rtt.flush() rtt.flush()");
            assert_eq!(rc, OK, "{msg}");
            assert_eq!(kind, KIND_OK, "{msg}");
            assert_eq!(ctx.flushed.get(), 2);
        });
    }
}
