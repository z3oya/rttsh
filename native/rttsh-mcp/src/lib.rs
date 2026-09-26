//! rttsh-mcp: the Rust home of rttsh's MCP capability.
//!
//! Layer split: this file is the FFI surface (session lifecycle, the feed/drain/wait byte
//! pump, the Rust→C# dispatch callback, panic discipline); `server` is the rmcp protocol
//! layer whose tool schemas forward to the C# host; `transport` bridges the queues into
//! the async stream pair rmcp serves on. The C# host owns the process console and the
//! debugger; Rust owns the protocol. Conventions from the C#↔Rust interop study:
//!
//! - every export is `extern "C"`, returns a status code, and never lets a panic
//!   escape (a panic crossing `extern "C"` aborts the process, so `catch_unwind` is
//!   mandatory — this also means `panic = "abort"` must never be set for this crate)
//! - exports that take raw pointers are `unsafe extern "C"`, putting the caller
//!   contract in the type; the C# side is unaffected (the ABI is identical)
//! - buffers are caller-allocated (drain copies whatever fits); the dispatch response
//!   is the one exception: the C# side allocates it through `rttsh_mcp_alloc` and
//!   hands ownership over by pointer (see [`DispatchFn`])
//! - all text is UTF-8; whoever allocates, frees (the dispatch response is taken over
//!   as a `Vec` of this allocator, so freeing stays on the Rust side)
//! - a positive return value means a byte/element count, a negative one is the
//!   status ladder — never mix the two in one function (`rttsh_mcp_alloc` returning a
//!   pointer and `rttsh_mcp_free` returning nothing are the two exceptions)

use std::collections::HashMap;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::sync::atomic::{AtomicI32, Ordering};
use std::sync::{Arc, LazyLock, Mutex};
use std::time::Duration;

use tokio::sync::mpsc;

use rmcp::ServiceExt;

mod server;
mod transport;

use server::RttshMcpHandler;
use transport::{InboundStream, Outbound, OutboundSink};

/// Status ladder, mirrored by the C# side in `McpNative.cs`.
pub const OK: i32 = 0;
pub const ERR_INVALID_HANDLE: i32 = -1;
pub const ERR_BAD_ARGUMENT: i32 = -2;
pub const ERR_INTERNAL: i32 = -3;

/// Bumped when the FFI surface changes shape; the C# host refuses to run against a
/// mismatched DLL (same fail-fast spirit as JLinkLibrary's version probe). v4: the
/// dispatch handler hands the response over by pointer (see [`DispatchFn`]) — the
/// caller-buffer retry and the execute-once cache are gone.
pub const ABI_VERSION: i32 = 4;

/// How long `stop` waits for in-flight tool dispatches (a C# expect against quiet
/// hardware can hold one for a while) before the runtime cancels what is left.
const SHUTDOWN_GRACE: Duration = Duration::from_secs(2);

/// Managed callback that executes one tool call, synchronously, on the calling thread,
/// and hands the response over by pointer: the handler allocates the response bytes
/// through `rttsh_mcp_alloc` (this crate's allocator), copies them in, and reports the
/// pair through the out-params; this side takes the buffer back as a `Vec` and frees it
/// on drop. A null pointer with `*resp_len_out == 0` means an empty response; any
/// negative return means no buffer was produced.
///
/// Execute-once is structural: one logical call crosses the boundary exactly once —
/// there is no retry and no replay — so a non-idempotent tool (connect!) can never run
/// twice and identical repeated calls always re-execute. (The earlier caller-buffer
/// shape needed a call-id cache to keep that once-only guarantee; the pointer handover
/// is what removed it. Do not reintroduce a caller-buffer retry here.)
///
/// Return [`OK`] or a negative status from the ladder.
pub type DispatchFn = unsafe extern "system" fn(
    handle: i32,
    method: *const u8,
    method_len: u32,
    request: *const u8,
    request_len: u32,
    resp_out: *mut *mut u8,
    resp_len_out: *mut u32,
) -> i32;

/// Everything the MCP layer needs to reach the C# host. Cloned out from under the
/// registry lock so no FFI call — not `feed`'s blocking send, not `wait`'s condvar —
/// ever holds the lock across a blocking operation (a held lock would deadlock stop).
#[derive(Clone, Copy)]
pub(crate) struct SessionRefs {
    pub(crate) dispatch: DispatchFn,
    pub(crate) handle: i32,
}

struct Session {
    /// The C# route every tool call takes; cloned out by `lookup`.
    dispatch: DispatchFn,
    /// Client bytes in; dropped by `stop` to end the service with an EOF.
    inbound_tx: mpsc::Sender<Vec<u8>>,
    outbound: Arc<Outbound>,
    /// The tool calls run on blocking pool threads inside this runtime; `stop` tears it
    /// down with a grace bound instead of an unbounded join.
    rt: tokio::runtime::Runtime,
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

/// The per-call session pieces `lookup` clones out of the registry (see [`SessionRefs`]
/// for why cloning, not borrowing).
pub(crate) struct SessionView {
    pub(crate) refs: SessionRefs,
    pub(crate) outbound: Arc<Outbound>,
    pub(crate) inbound_tx: mpsc::Sender<Vec<u8>>,
}

fn lookup(handle: i32) -> Option<SessionView> {
    let sessions = SESSIONS.lock().unwrap_or_else(|e| e.into_inner());
    let session = sessions.get(&handle)?;
    Some(SessionView {
        refs: SessionRefs {
            dispatch: session.dispatch,
            handle,
        },
        outbound: session.outbound.clone(),
        inbound_tx: session.inbound_tx.clone(),
    })
}

/// One tool-call round trip through the C# host, shared by the rmcp tool layer and the
/// `dispatch_probe` test path: calls the handler once and takes ownership of the
/// response buffer it produced — allocated with this crate's allocator, so the `Vec`
/// takeover is zero-copy and frees on drop.
pub(crate) fn invoke_dispatch(refs: SessionRefs, method: &str, request: &str) -> Result<Vec<u8>, i32> {
    let method = method.as_bytes();
    let request = request.as_bytes();
    let mut resp_ptr: *mut u8 = std::ptr::null_mut();
    let mut resp_len: u32 = 0;
    let rc = unsafe {
        (refs.dispatch)(
            refs.handle,
            method.as_ptr(),
            method.len() as u32,
            request.as_ptr(),
            request.len() as u32,
            &mut resp_ptr,
            &mut resp_len,
        )
    };
    if rc != OK {
        if !resp_ptr.is_null() {
            // Defensive: a misbehaving handler allocated, then reported an error.
            unsafe { rttsh_mcp_free(resp_ptr, resp_len) };
        }
        return Err(rc);
    }
    match (resp_ptr.is_null(), resp_len) {
        (true, 0) => Ok(Vec::new()),
        (true, _) => Err(ERR_INTERNAL),
        (false, len) => Ok(unsafe { Vec::from_raw_parts(resp_ptr, len as usize, len as usize) }),
    }
}

#[no_mangle]
pub extern "C" fn rttsh_mcp_abi_version() -> i32 {
    ABI_VERSION
}

/// Allocates `len` bytes with this crate's allocator for the C# dispatch handler to
/// write a response into and report back through the callback's out-params; from that
/// moment the buffer belongs to the Rust side (taken over as a `Vec`, or freed manually
/// through [`rttsh_mcp_free`]). Null = the allocation failed; the handler answers
/// [`ERR_INTERNAL`].
#[no_mangle]
pub extern "C" fn rttsh_mcp_alloc(len: u32) -> *mut u8 {
    let mut vec: Vec<u8> = Vec::with_capacity(len as usize);
    let ptr = vec.as_mut_ptr();
    std::mem::forget(vec);
    ptr
}

/// Frees a buffer produced by [`rttsh_mcp_alloc`] without the `Vec` takeover — the
/// defensive path for a handler that allocated, then reported an error. `ptr` must come
/// from `rttsh_mcp_alloc` with the same `len`; null is a no-op.
///
/// # Safety
/// `ptr` must be null or a live `rttsh_mcp_alloc` buffer of exactly `len` bytes that the
/// Rust side has not already taken over.
#[no_mangle]
pub unsafe extern "C" fn rttsh_mcp_free(ptr: *mut u8, len: u32) {
    if ptr.is_null() {
        return;
    }
    drop(unsafe { Vec::from_raw_parts(ptr, len as usize, len as usize) });
}

/// Creates a session: the tokio runtime, the rmcp server task (fed by `feed`, drained by
/// `drain`), and the C# dispatch route. Returns the handle (> 0) or a negative status.
#[no_mangle]
pub extern "C" fn rttsh_mcp_start(dispatch: Option<DispatchFn>) -> i32 {
    guard(|| {
        let Some(dispatch) = dispatch else {
            return ERR_BAD_ARGUMENT;
        };
        let handle = NEXT_HANDLE.fetch_add(1, Ordering::Relaxed) + 1;
        let refs = SessionRefs { dispatch, handle };

        let rt = match tokio::runtime::Builder::new_multi_thread()
            .worker_threads(2)
            .enable_all()
            .build()
        {
            Ok(rt) => rt,
            Err(e) => {
                eprintln!("rttsh_mcp_native: failed to start the async runtime: {e}");
                return ERR_INTERNAL;
            }
        };

        let (inbound_tx, inbound_rx) = mpsc::channel::<Vec<u8>>(64);
        let outbound = Arc::new(Outbound::default());
        let handler = RttshMcpHandler::new(refs);
        let sink_outbound = outbound.clone();
        rt.spawn(async move {
            let sink = OutboundSink::new(sink_outbound);
            // Errors from serve/waiting land here; a panic inside the service task would
            // end the spawned task silently (the client sees EOF and can reconnect).
            let served = async {
                let service = handler
                    .serve((InboundStream::new(inbound_rx), sink))
                    .await?;
                let _ = service.waiting().await;
                Ok::<(), Box<dyn std::error::Error + Send + Sync>>(())
            }
            .await;
            if let Err(e) = served {
                eprintln!("rttsh_mcp_native: MCP service error: {e}");
            }
        });

        SESSIONS.lock().unwrap_or_else(|e| e.into_inner()).insert(
            handle,
            Session {
                dispatch,
                inbound_tx,
                outbound,
                rt,
            },
        );
        handle
    })
}

/// Destroys the session: EOF ends the MCP service gracefully, then the runtime shuts
/// down with a grace bound for in-flight tool dispatches. Any later use of the handle
/// returns [`ERR_INVALID_HANDLE`].
#[no_mangle]
pub extern "C" fn rttsh_mcp_stop(handle: i32) -> i32 {
    guard(|| {
        let Some(session) = SESSIONS
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .remove(&handle)
        else {
            return ERR_INVALID_HANDLE;
        };
        let Session {
            dispatch: _,
            inbound_tx,
            outbound: _,
            rt,
        } = session;
        drop(inbound_tx);
        rt.shutdown_timeout(SHUTDOWN_GRACE);
        OK
    })
}

/// Copies `len` bytes into the server's inbound stream (client → server direction).
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
        let bytes = unsafe { std::slice::from_raw_parts(data, len as usize) }.to_vec();
        let Some(view) = lookup(handle) else {
            return ERR_INVALID_HANDLE;
        };
        // From a host thread, never from the runtime. A full bounded channel applies
        // backpressure to the client stream; a dropped receiver means we are stopping.
        if view.inbound_tx.blocking_send(bytes).is_err() {
            return ERR_INVALID_HANDLE;
        }
        OK
    })
}

/// Copies up to `cap` bytes out of the outbound stream (server → client direction) and
/// returns how many were copied; 0 means the stream is currently empty.
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
        let Some(view) = lookup(handle) else {
            return ERR_INVALID_HANDLE;
        };
        let slice = unsafe { std::slice::from_raw_parts_mut(buf, cap as usize) };
        view.outbound.drain(slice) as i32
    })
}

/// Blocks up to `timeout_ms` for server bytes to become available; returns the pending
/// byte count (0 = timed out with nothing to drain).
#[no_mangle]
pub extern "C" fn rttsh_mcp_wait(handle: i32, timeout_ms: u32) -> i32 {
    guard(|| {
        let Some(view) = lookup(handle) else {
            return ERR_INVALID_HANDLE;
        };
        view.outbound
            .wait_available(Duration::from_millis(timeout_ms as u64)) as i32
    })
}

/// Exercises the full dispatch glue without MCP: calls the registered C# handler with
/// `method`/`request` (the same ownership-transfer path the tool layer uses) and
/// enqueues the response into the outbound stream. Returns the response length (>= 0)
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
        let (Ok(method), Ok(request)) = (std::str::from_utf8(method), std::str::from_utf8(request))
        else {
            return ERR_BAD_ARGUMENT;
        };
        let Some(view) = lookup(handle) else {
            return ERR_INVALID_HANDLE;
        };
        match invoke_dispatch(view.refs, method, request) {
            Ok(resp) => {
                let len = resp.len();
                view.outbound.push(&resp);
                len as i32
            }
            Err(rc) => rc,
        }
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
    use crate::{rttsh_mcp_drain, rttsh_mcp_feed, rttsh_mcp_wait, OK};
    use std::time::{Duration, Instant};

    /// Rust-side stand-in for the C# host: reverses the request into the response,
    /// handing the buffer over through `rttsh_mcp_alloc` exactly like the real
    /// trampoline does.
    unsafe extern "system" fn reverse_dispatch(
        _handle: i32,
        method: *const u8,
        method_len: u32,
        request: *const u8,
        request_len: u32,
        resp_out: *mut *mut u8,
        resp_len_out: *mut u32,
    ) -> i32 {
        let method = unsafe { std::slice::from_raw_parts(method, method_len as usize) };
        assert_eq!(method, b"probe");
        let request = unsafe { std::slice::from_raw_parts(request, request_len as usize) };
        let reversed: Vec<u8> = request.iter().rev().copied().collect();
        unsafe { respond(&reversed, resp_out, resp_len_out) }
    }

    /// The trampoline's response path in miniature: allocate through the real
    /// `rttsh_mcp_alloc`, copy, hand over.
    unsafe fn respond(
        bytes: &[u8],
        resp_out: *mut *mut u8,
        resp_len_out: *mut u32,
    ) -> i32 {
        let ptr = rttsh_mcp_alloc(bytes.len() as u32);
        unsafe {
            std::ptr::copy_nonoverlapping(bytes.as_ptr(), ptr, bytes.len());
            *resp_out = ptr;
            *resp_len_out = bytes.len() as u32;
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
    fn dispatch_probe_round_trips_through_the_callback() {
        let handle = start_reverse_session();
        // 100 varied bytes: a payload large enough that a trivial pass-through would not
        // pass for a real round trip.
        let request: Vec<u8> = (0..100).map(|i| b'a' + (i % 26) as u8).collect();
        let n = unsafe {
            rttsh_mcp_dispatch_probe(
                handle,
                b"probe".as_ptr(),
                5,
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
    fn wait_returns_pending_bytes_and_times_out_when_empty() {
        let handle = start_reverse_session();
        assert_eq!(rttsh_mcp_wait(handle, 30), 0);
        let request = b"ping";
        let n = unsafe {
            rttsh_mcp_dispatch_probe(
                handle,
                b"probe".as_ptr(),
                5,
                request.as_ptr(),
                request.len() as u32,
            )
        };
        assert_eq!(n, 4);
        assert_eq!(rttsh_mcp_wait(handle, 1000), 4);
        assert_eq!(drain_all(handle), b"gnip");
        assert_eq!(rttsh_mcp_wait(handle, 30), 0);
        assert_eq!(rttsh_mcp_stop(handle), OK);
    }

    #[test]
    fn dispatch_probe_propagates_unknown_handle() {
        assert_eq!(
            unsafe { rttsh_mcp_dispatch_probe(987_654, b"probe".as_ptr(), 5, b"x".as_ptr(), 1) },
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
        assert_eq!(
            unsafe { rttsh_mcp_drain(handle, std::ptr::null_mut(), 0) },
            ERR_INVALID_HANDLE
        );
        assert_eq!(rttsh_mcp_wait(handle, 10), ERR_INVALID_HANDLE);
    }

    #[test]
    fn stop_finishes_within_the_grace_bound() {
        let handle = start_reverse_session();
        let started = Instant::now();
        assert_eq!(rttsh_mcp_stop(handle), OK);
        assert!(
            started.elapsed() < Duration::from_secs(5),
            "stop hung: {:?}",
            started.elapsed()
        );
    }
}
