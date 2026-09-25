//! rttsh-mcp: the Rust home of rttsh's MCP capability.
//!
//! This crate holds no MCP functionality yet — only the FFI foundation the MCP server
//! layer will stand on: session lifecycle, the stdio byte-pump shape, the Rust→C#
//! dispatch callback, and the panic discipline. Conventions come from the C#↔Rust
//! interop study:
//!
//! - every export is `extern "C"`, returns a status code, and never lets a panic
//!   escape (a panic crossing `extern "C"` aborts the process, so `catch_unwind` is
//!   mandatory — this also means `panic = "abort"` must never be set for this crate)
//! - exports that take raw pointers are `unsafe extern "C"`, putting the caller
//!   contract in the type; the C# side is unaffected (the ABI is identical)
//! - buffers are caller-allocated; on insufficient capacity the callee stores the
//!   needed size through the out-param and returns [`ERR_BUFFER_TOO_SMALL`]
//! - all text is UTF-8; whoever allocates, frees
//! - a positive return value means a byte/element count, a negative one is the
//!   status ladder — never mix the two in one function

use std::collections::{HashMap, VecDeque};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::sync::atomic::{AtomicI32, Ordering};
use std::sync::{LazyLock, Mutex};

/// Status ladder, mirrored by the C# side in `McpNative.cs`.
pub const OK: i32 = 0;
pub const ERR_INVALID_HANDLE: i32 = -1;
pub const ERR_BUFFER_TOO_SMALL: i32 = -2;
pub const ERR_BAD_ARGUMENT: i32 = -3;
pub const ERR_INTERNAL: i32 = -4;

/// Bumped when the FFI surface changes shape; the C# host refuses to run against a
/// mismatched DLL (same fail-fast spirit as JLinkLibrary's version probe).
pub const ABI_VERSION: i32 = 1;

/// Upper bound for one dispatch response, so a misbehaving handler cannot grow the
/// retry loop without end.
const MAX_RESPONSE_BYTES: usize = 16 * 1024 * 1024;

const INITIAL_RESPONSE_BYTES: usize = 64;
const DISPATCH_RETRY_ATTEMPTS: usize = 3;

/// Managed callback that executes one (future) tool call, synchronously, on the
/// calling thread. The response uses the caller-buffer pattern: the handler writes at
/// most `resp_cap` bytes into `resp` and reports the written count through `*resp_len`;
/// when they do not fit it returns [`ERR_BUFFER_TOO_SMALL`] with `*resp_len` set to
/// the needed size and this side retries with a grown buffer. Return [`OK`] or a
/// negative status from the ladder.
pub type DispatchFn = unsafe extern "system" fn(
    handle: i32,
    method: *const u8,
    method_len: u32,
    request: *const u8,
    request_len: u32,
    resp: *mut u8,
    resp_cap: u32,
    resp_len: *mut u32,
) -> i32;

struct Session {
    dispatch: DispatchFn,
    /// Bytes fed from the C# side (client → server direction), not yet consumed.
    inbound: Mutex<VecDeque<u8>>,
    /// Bytes produced for the C# side to drain (server → client direction).
    outbound: Mutex<VecDeque<u8>>,
}

static NEXT_HANDLE: AtomicI32 = AtomicI32::new(0);
static SESSIONS: LazyLock<Mutex<HashMap<i32, Session>>> =
    LazyLock::new(|| Mutex::new(HashMap::new()));

/// Every export body runs under `guard`: a panic anywhere becomes [`ERR_INTERNAL`]
/// instead of crossing the FFI boundary. The panic text goes to stderr (diagnostics
/// never touch stdout — that belongs to the MCP stream).
fn guard(f: impl FnOnce() -> i32) -> i32 {
    match catch_unwind(AssertUnwindSafe(f)) {
        Ok(rc) => rc,
        Err(payload) => {
            let message = payload
                .downcast_ref::<&str>()
                .copied()
                .or_else(|| payload.downcast_ref::<String>().map(String::as_str))
                .unwrap_or("<non-string panic payload>");
            eprintln!("rttsh_mcp_native: panic caught: {message}");
            ERR_INTERNAL
        }
    }
}

/// Runs `f` with the session, or [`ERR_INVALID_HANDLE`] when the handle is unknown.
fn with_session(handle: i32, f: impl FnOnce(&Session) -> i32) -> i32 {
    let sessions = SESSIONS.lock().unwrap_or_else(|e| e.into_inner());
    match sessions.get(&handle) {
        Some(session) => f(session),
        None => ERR_INVALID_HANDLE,
    }
}

#[no_mangle]
pub extern "C" fn rttsh_mcp_abi_version() -> i32 {
    ABI_VERSION
}

/// Creates a session and registers `dispatch`; returns the handle (> 0) or a negative
/// status. The skeleton spawns no threads — the MCP layer will start its async runtime
/// here and drive `inbound`/`outbound` from a transport task.
#[no_mangle]
pub extern "C" fn rttsh_mcp_start(dispatch: Option<DispatchFn>) -> i32 {
    guard(|| {
        let Some(dispatch) = dispatch else {
            return ERR_BAD_ARGUMENT;
        };
        let handle = NEXT_HANDLE.fetch_add(1, Ordering::Relaxed) + 1;
        let session = Session {
            dispatch,
            inbound: Mutex::new(VecDeque::new()),
            outbound: Mutex::new(VecDeque::new()),
        };
        SESSIONS
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .insert(handle, session);
        handle
    })
}

/// Destroys the session; any later use of the handle returns [`ERR_INVALID_HANDLE`].
#[no_mangle]
pub extern "C" fn rttsh_mcp_stop(handle: i32) -> i32 {
    guard(|| {
        match SESSIONS
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .remove(&handle)
        {
            Some(_) => OK,
            None => ERR_INVALID_HANDLE,
        }
    })
}

/// Copies `len` bytes into the inbound queue (client → server direction).
///
/// # Safety
/// `data` must be valid for reads of `len` bytes, or null when `len` is 0 (which is
/// rejected with [`ERR_BAD_ARGUMENT`]).
#[no_mangle]
pub unsafe extern "C" fn rttsh_mcp_feed(handle: i32, data: *const u8, len: u32) -> i32 {
    guard(|| {
        if data.is_null() && len > 0 {
            return ERR_BAD_ARGUMENT;
        }
        let bytes = unsafe { std::slice::from_raw_parts(data, len as usize) };
        with_session(handle, |s| {
            s.inbound
                .lock()
                .unwrap_or_else(|e| e.into_inner())
                .extend(bytes.iter().copied());
            OK
        })
    })
}

/// Skeleton stand-in for the future transport task: moves everything waiting in the
/// inbound queue to the outbound queue and returns the moved byte count. The MCP layer
/// replaces this with a real async transport; the feed/drain FFI shape stays.
#[no_mangle]
pub extern "C" fn rttsh_mcp_pump_step(handle: i32) -> i32 {
    guard(|| {
        with_session(handle, |s| {
            // Lock order (inbound, then outbound) is fixed so the two locks below are
            // the only nesting and can never deadlock against themselves.
            let mut inbound = s.inbound.lock().unwrap_or_else(|e| e.into_inner());
            let mut outbound = s.outbound.lock().unwrap_or_else(|e| e.into_inner());
            let moved = inbound.len();
            outbound.extend(inbound.drain(..));
            moved as i32
        })
    })
}

/// Copies up to `cap` bytes out of the outbound queue (server → client direction) and
/// returns how many were copied; 0 means the queue is currently empty.
///
/// # Safety
/// `buf` must be valid for writes of `cap` bytes, or null when `cap` is 0 (which is
/// rejected with [`ERR_BAD_ARGUMENT`]).
#[no_mangle]
pub unsafe extern "C" fn rttsh_mcp_drain(handle: i32, buf: *mut u8, cap: u32) -> i32 {
    guard(|| {
        if buf.is_null() && cap > 0 {
            return ERR_BAD_ARGUMENT;
        }
        with_session(handle, |s| {
            let mut outbound = s.outbound.lock().unwrap_or_else(|e| e.into_inner());
            let take = (cap as usize).min(outbound.len());
            let slice = unsafe { std::slice::from_raw_parts_mut(buf, take) };
            for (slot, byte) in slice.iter_mut().zip(outbound.drain(..take)) {
                *slot = byte;
            }
            take as i32
        })
    })
}

/// Exercises the full dispatch glue without MCP: calls the registered C# handler with
/// `method`/`request`, retrying with a grown buffer on [`ERR_BUFFER_TOO_SMALL`], and
/// enqueues the response into the outbound queue. Returns the response length (>= 0)
/// or a negative status.
///
/// # Safety
/// `method`/`request` must each be valid for reads of their `*_len` bytes, or null
/// when the length is 0 (rejected with [`ERR_BAD_ARGUMENT`]).
#[no_mangle]
pub unsafe extern "C" fn rttsh_mcp_dispatch_probe(
    handle: i32,
    method: *const u8,
    method_len: u32,
    request: *const u8,
    request_len: u32,
) -> i32 {
    guard(|| {
        if (method.is_null() && method_len > 0) || (request.is_null() && request_len > 0) {
            return ERR_BAD_ARGUMENT;
        }
        let method = unsafe { std::slice::from_raw_parts(method, method_len as usize) };
        let request = unsafe { std::slice::from_raw_parts(request, request_len as usize) };
        with_session(handle, |s| {
            let mut resp_cap = INITIAL_RESPONSE_BYTES;
            for _ in 0..DISPATCH_RETRY_ATTEMPTS {
                let mut resp = vec![0u8; resp_cap];
                let mut resp_len: u32 = 0;
                let rc = unsafe {
                    (s.dispatch)(
                        handle,
                        method.as_ptr(),
                        method.len() as u32,
                        request.as_ptr(),
                        request.len() as u32,
                        resp.as_mut_ptr(),
                        resp_cap as u32,
                        &mut resp_len,
                    )
                };
                if rc == OK {
                    resp.truncate(resp_len as usize);
                    s.outbound
                        .lock()
                        .unwrap_or_else(|e| e.into_inner())
                        .extend(resp);
                    return resp_len as i32;
                }
                if rc != ERR_BUFFER_TOO_SMALL || resp_len as usize <= resp_cap {
                    return rc;
                }
                resp_cap = (resp_len as usize).min(MAX_RESPONSE_BYTES);
            }
            ERR_BUFFER_TOO_SMALL
        })
    })
}

/// Proves the panic discipline: a panic inside the guard must surface as
/// [`ERR_INTERNAL`], never abort the host process.
#[no_mangle]
pub extern "C" fn rttsh_mcp_panic_probe() -> i32 {
    guard(|| panic!("panic probe: must surface as ERR_INTERNAL"))
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Rust-side stand-in for the C# handler: reverses the request into the response,
    /// claiming `ERR_BUFFER_TOO_SMALL` when it does not fit (the natural behavior that
    /// forces the probe's retry path for large requests).
    unsafe extern "system" fn reverse_dispatch(
        _handle: i32,
        method: *const u8,
        method_len: u32,
        request: *const u8,
        request_len: u32,
        resp: *mut u8,
        resp_cap: u32,
        resp_len: *mut u32,
    ) -> i32 {
        let method = unsafe { std::slice::from_raw_parts(method, method_len as usize) };
        assert_eq!(method, b"echo");
        let request = unsafe { std::slice::from_raw_parts(request, request_len as usize) };
        let reversed: Vec<u8> = request.iter().rev().copied().collect();
        if reversed.len() as u32 > resp_cap {
            unsafe { *resp_len = reversed.len() as u32 };
            return ERR_BUFFER_TOO_SMALL;
        }
        unsafe {
            std::ptr::copy_nonoverlapping(reversed.as_ptr(), resp, reversed.len());
            *resp_len = reversed.len() as u32;
        }
        OK
    }

    fn start_reverse_session() -> i32 {
        let handle = rttsh_mcp_start(Some(reverse_dispatch));
        assert!(handle > 0, "start failed: {handle}");
        handle
    }

    fn drain_all(handle: i32) -> Vec<u8> {
        let mut out = Vec::new();
        loop {
            let mut buf = [0u8; 64];
            let n = unsafe { rttsh_mcp_drain(handle, buf.as_mut_ptr(), buf.len() as u32) };
            assert!(n >= 0, "drain failed: {n}");
            if n == 0 {
                return out;
            }
            out.extend_from_slice(&buf[..n as usize]);
        }
    }

    #[test]
    fn abi_version_is_reported() {
        assert_eq!(rttsh_mcp_abi_version(), ABI_VERSION);
    }

    #[test]
    fn start_rejects_missing_dispatch() {
        assert_eq!(rttsh_mcp_start(None), ERR_BAD_ARGUMENT);
    }

    #[test]
    fn stop_is_idempotent_only_once() {
        let handle = start_reverse_session();
        assert_eq!(rttsh_mcp_stop(handle), OK);
        assert_eq!(rttsh_mcp_stop(handle), ERR_INVALID_HANDLE);
    }

    #[test]
    fn pump_moves_fed_bytes_to_drain() {
        let handle = start_reverse_session();
        assert_eq!(unsafe { rttsh_mcp_feed(handle, b"hello".as_ptr(), 5) }, OK);
        assert_eq!(rttsh_mcp_pump_step(handle), 5);
        assert_eq!(drain_all(handle), b"hello");
        assert_eq!(rttsh_mcp_pump_step(handle), 0);
        assert_eq!(rttsh_mcp_stop(handle), OK);
    }

    #[test]
    fn drain_copies_at_most_cap_bytes() {
        let handle = start_reverse_session();
        assert_eq!(
            unsafe { rttsh_mcp_feed(handle, b"0123456789".as_ptr(), 10) },
            OK
        );
        assert_eq!(rttsh_mcp_pump_step(handle), 10);
        let mut buf = [0u8; 4];
        assert_eq!(unsafe { rttsh_mcp_drain(handle, buf.as_mut_ptr(), 4) }, 4);
        assert_eq!(&buf, b"0123");
        assert_eq!(drain_all(handle), b"456789");
        assert_eq!(rttsh_mcp_stop(handle), OK);
    }

    #[test]
    fn dispatch_probe_round_trips_through_the_callback() {
        let handle = start_reverse_session();
        // 100 bytes: larger than the probe's initial 64-byte buffer, so the callback's
        // ERR_BUFFER_TOO_SMALL / needed-size path (the C# glue contract) is exercised.
        let request: Vec<u8> = (0..100).map(|i| b'a' + (i % 26) as u8).collect();
        let n = unsafe {
            rttsh_mcp_dispatch_probe(
                handle,
                b"echo".as_ptr(),
                4,
                request.as_ptr(),
                request.len() as u32,
            )
        };
        assert_eq!(n as usize, request.len());
        let expected: Vec<u8> = request.iter().rev().copied().collect();
        assert_eq!(drain_all(handle), expected);
        assert_eq!(rttsh_mcp_stop(handle), OK);
    }

    #[test]
    fn dispatch_probe_propagates_unknown_handle() {
        assert_eq!(
            unsafe { rttsh_mcp_dispatch_probe(987_654, b"echo".as_ptr(), 4, b"x".as_ptr(), 1) },
            ERR_INVALID_HANDLE
        );
    }

    #[test]
    fn panic_probe_surfaces_as_internal_error() {
        assert_eq!(rttsh_mcp_panic_probe(), ERR_INTERNAL);
    }

    #[test]
    fn operations_after_stop_report_invalid_handle() {
        let handle = start_reverse_session();
        assert_eq!(rttsh_mcp_stop(handle), OK);
        assert_eq!(
            unsafe { rttsh_mcp_feed(handle, b"x".as_ptr(), 1) },
            ERR_INVALID_HANDLE
        );
        assert_eq!(rttsh_mcp_pump_step(handle), ERR_INVALID_HANDLE);
        assert_eq!(
            unsafe { rttsh_mcp_drain(handle, std::ptr::null_mut(), 0) },
            ERR_INVALID_HANDLE
        );
    }
}
