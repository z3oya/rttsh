//! The rmcp MCP server. Rust owns protocol + tool schemas; execution is forwarded to the
//! C# host through the dispatch callback — one JSON request/response per tool call, on a
//! blocking pool thread so the async runtime never stalls behind hardware round-trips.

use std::future::Future;

use rmcp::{
    handler::server::router::tool::ToolRouter, handler::server::tool::Parameters, model::*, tool,
    tool_handler, tool_router, ErrorData as McpError, ServerHandler,
};
use schemars::JsonSchema;
use serde::{Deserialize, Serialize};

use crate::{invoke_dispatch, SessionRefs};

/// How the C# host answers one tool call: `{"ok":true,"text":...}` on success,
/// `{"ok":false,"text":...}` with an actionable message on failure. Text is pre-formatted
/// for the model on the C# side (it owns the data); Rust passes it through.
#[derive(Deserialize)]
struct ToolEnvelope {
    ok: bool,
    text: String,
}

// ---- tool arguments (schemars builds the inputSchema; doc comments are the descriptions) ----

#[derive(Debug, Deserialize, Serialize, JsonSchema)]
pub struct ConnectArgs {
    /// Target chip name as known to the J-Link device database (e.g. "STM32H743XI"); list exact names with list_devices
    pub chip: String,
    /// Probe speed in kHz (default 4000)
    #[serde(default)]
    pub speed_khz: Option<u32>,
    /// Debug interface: "swd" (default) or "jtag"
    #[serde(default)]
    pub interface: Option<String>,
    /// Probe USB serial number (default: first probe)
    #[serde(default)]
    pub serial_number: Option<i64>,
    /// RTT channel pair index, 0-15 (default 0)
    #[serde(default)]
    pub channel: Option<u32>,
    /// Known RTT control-block address in hex (e.g. "0x20000001"); default: SDK RAM scan
    #[serde(default)]
    pub rtt_address: Option<String>,
    /// Firmware ELF (ELF32) to resolve the control block from; an explicit rtt_address wins
    #[serde(default)]
    pub elf: Option<String>,
}

#[derive(Debug, Deserialize, Serialize, JsonSchema)]
pub struct DisconnectArgs {
    // No parameters; the client passes an empty arguments object.
}

#[derive(Debug, Deserialize, Serialize, JsonSchema)]
pub struct GetStatusArgs {
    // No parameters; the client passes an empty arguments object.
}

#[derive(Debug, Deserialize, Serialize, JsonSchema)]
pub struct SendArgs {
    /// Text to write to the target's RTT down channel (a newline is appended)
    pub text: String,
}

#[derive(Debug, Deserialize, Serialize, JsonSchema)]
pub struct RttReadArgs {
    /// How long to wait for output when nothing is buffered yet, in ms (default 1000)
    #[serde(default)]
    pub timeout_ms: Option<u32>,
}

#[derive(Debug, Deserialize, Serialize, JsonSchema)]
pub struct ExpectArgs {
    /// Literal text to wait for in the target output
    pub pattern: String,
    /// Give up after this long, in ms
    pub timeout_ms: u32,
}

#[derive(Debug, Deserialize, Serialize, JsonSchema)]
pub struct MemReadArgs {
    /// Start address in hex (e.g. "0x20000000")
    pub address: String,
    /// Number of units to read (each call moves at most 1 MiB)
    pub count: u32,
    /// Access width in bits: 8, 16 or 32 (default 32; widths above 8 need an aligned address)
    #[serde(default)]
    pub width: Option<u32>,
}

#[derive(Debug, Deserialize, Serialize, JsonSchema)]
pub struct ListDevicesArgs {
    /// Substring filter on device names (default: every name)
    #[serde(default)]
    pub filter: Option<String>,
}

/// Cloneable rmcp handler holding the C# dispatch route. All eight tools share one shape:
/// serialize typed args → blocking dispatch → parse the envelope → pass the text through.
#[derive(Clone)]
pub(crate) struct RttshMcpHandler {
    tool_router: ToolRouter<Self>,
    refs: SessionRefs,
}

#[tool_router(router = rttsh_tool_router)]
impl RttshMcpHandler {
    pub(crate) fn new(refs: SessionRefs) -> Self {
        Self {
            tool_router: Self::rttsh_tool_router(),
            refs,
        }
    }

    async fn call_tool<A: Serialize>(&self, method: &str, args: A) -> Result<CallToolResult, McpError> {
        let params = serde_json::to_value(args).map_err(|e| {
            McpError::internal_error(format!("failed to serialize tool arguments: {e}"), None)
        })?;
        let request = serde_json::json!({ "method": method, "params": params }).to_string();
        let this = self.clone();
        let method = method.to_string();
        let response = tokio::task::spawn_blocking(move || this.invoke(&method, &request))
            .await
            .map_err(|e| McpError::internal_error(format!("tool dispatch failed: {e}"), None))?
            .map_err(|rc| {
                McpError::internal_error(format!("native dispatch failed with status {rc}"), None)
            })?;
        let text = String::from_utf8(response).map_err(|_| {
            McpError::internal_error("dispatch response is not UTF-8".to_string(), None)
        })?;
        let envelope: ToolEnvelope = serde_json::from_str(&text).map_err(|e| {
            McpError::internal_error(format!("malformed dispatch response: {e}"), None)
        })?;
        // Tool failures are isError tool results (what the model sees), not JSON-RPC
        // protocol errors; only the faults above - dispatch, encoding, envelope - are
        // protocol-level internal errors.
        let content = vec![Content::text(envelope.text)];
        if envelope.ok {
            Ok(CallToolResult::success(content))
        } else {
            Ok(CallToolResult::error(content))
        }
    }

    fn invoke(&self, method: &str, request: &str) -> Result<Vec<u8>, i32> {
        invoke_dispatch(self.refs, method, request)
    }

    #[tool(
        description = "Connect to a target over J-Link, attach RTT and take the single-instance target lock. Exactly one session exists at a time; every other tool fails until connect succeeds."
    )]
    async fn connect(
        &self,
        Parameters(args): Parameters<ConnectArgs>,
    ) -> Result<CallToolResult, McpError> {
        self.call_tool("connect", args).await
    }

    #[tool(description = "Disconnect from the target and release the target lock.")]
    async fn disconnect(
        &self,
        Parameters(args): Parameters<DisconnectArgs>,
    ) -> Result<CallToolResult, McpError> {
        self.call_tool("disconnect", args).await
    }

    #[tool(
        description = "Report the current session state: whether a target is connected, its chip, and whether the core is halted."
    )]
    async fn get_status(
        &self,
        Parameters(args): Parameters<GetStatusArgs>,
    ) -> Result<CallToolResult, McpError> {
        self.call_tool("get_status", args).await
    }

    #[tool(
        description = "Write one line of text to the target's RTT down channel (a newline is appended)."
    )]
    async fn send(
        &self,
        Parameters(args): Parameters<SendArgs>,
    ) -> Result<CallToolResult, McpError> {
        self.call_tool("send", args).await
    }

    #[tool(
        description = "Read target output: everything received but not yet consumed - returned immediately when output is pending, otherwise waits up to the timeout for the first bytes. Consuming - the next rtt_read or expect starts after this text. Returns \"(no output)\" when the target was quiet."
    )]
    async fn rtt_read(
        &self,
        Parameters(args): Parameters<RttReadArgs>,
    ) -> Result<CallToolResult, McpError> {
        self.call_tool("rtt_read", args).await
    }

    #[tool(
        description = "Wait until the target output contains pattern (literal text), returning everything from the last consumed position through the match end. On timeout the error names the buffer tail - narrow the pattern or rtt_read first."
    )]
    async fn expect(
        &self,
        Parameters(args): Parameters<ExpectArgs>,
    ) -> Result<CallToolResult, McpError> {
        self.call_tool("expect", args).await
    }

    #[tool(
        description = "Read target memory over J-Link and return a hexdump. address is hex (0x-prefixed); width is 8, 16 or 32 bits (default 32, wider widths need an aligned address); count is the number of width units."
    )]
    async fn mem_read(
        &self,
        Parameters(args): Parameters<MemReadArgs>,
    ) -> Result<CallToolResult, McpError> {
        self.call_tool("mem_read", args).await
    }

    #[tool(
        description = "Search the J-Link device database for valid chip names (substring filter). Run this before connect when unsure of the exact name."
    )]
    async fn list_devices(
        &self,
        Parameters(args): Parameters<ListDevicesArgs>,
    ) -> Result<CallToolResult, McpError> {
        self.call_tool("list_devices", args).await
    }
}

#[tool_handler]
impl ServerHandler for RttshMcpHandler {
    fn get_info(&self) -> ServerInfo {
        ServerInfo {
            protocol_version: ProtocolVersion::V_2024_11_05,
            capabilities: ServerCapabilities::builder().enable_tools().build(),
            server_info: Implementation::from_build_env(),
            instructions: Some(
                "rttsh MCP server: J-Link RTT acceptance testing for embedded targets. \
                 Workflow: list_devices to find the exact chip name, connect (chip [+ elf]), \
                 then drive the RTT console with send / rtt_read / expect, inspect memory with \
                 mem_read, and finish with disconnect. expect is the acceptance primitive: it \
                 returns the matched output or a timeout error with the buffer tail."
                    .to_string(),
            ),
        }
    }
}

#[cfg(test)]
mod tests {
    use crate::{
        rttsh_mcp_drain, rttsh_mcp_feed, rttsh_mcp_start, rttsh_mcp_stop, rttsh_mcp_wait, OK,
    };
    use serde_json::Value;
    use std::sync::atomic::{AtomicU32, Ordering};
    use std::time::{Duration, Instant};

    /// Fake C# host: answers each tool call with a canned envelope so the whole rmcp stack
    /// can be exercised without C# or hardware. The second get_status call fails (ok=false)
    /// so the isError mapping is exercised at the protocol level.
    unsafe extern "system" fn fake_dispatch(
        _handle: i32,
        _method: *const u8,
        _method_len: u32,
        request: *const u8,
        request_len: u32,
        resp_out: *mut *mut u8,
        resp_len_out: *mut u32,
    ) -> i32 {
        let request = unsafe { std::slice::from_raw_parts(request, request_len as usize) };
        let request: Value = serde_json::from_slice(request).expect("request json");
        static GET_STATUS_CALLS: AtomicU32 = AtomicU32::new(0);
        let (text, ok) = match request["method"].as_str() {
            Some("get_status") => {
                // The second get_status call fails (ok=false) so the isError mapping is
                // exercised at the protocol level.
                let n = GET_STATUS_CALLS.fetch_add(1, Ordering::Relaxed);
                if n == 0 {
                    ("connected: false".to_string(), true)
                } else {
                    ("not connected - call connect first".to_string(), false)
                }
            }
            Some("expect") => ("boot mark READY\r\n".to_string(), true),
            other => panic!("unexpected tool method: {other:?}"),
        };
        let envelope = serde_json::json!({ "ok": ok, "text": text }).to_string();
        unsafe { respond(envelope.as_bytes(), resp_out, resp_len_out) }
    }

    /// The trampoline's response path in miniature: allocate through the real
    /// `rttsh_mcp_alloc`, copy, hand over.
    unsafe fn respond(
        bytes: &[u8],
        resp_out: *mut *mut u8,
        resp_len_out: *mut u32,
    ) -> i32 {
        let ptr = crate::rttsh_mcp_alloc(bytes.len() as u32);
        unsafe {
            std::ptr::copy_nonoverlapping(bytes.as_ptr(), ptr, bytes.len());
            *resp_out = ptr;
            *resp_len_out = bytes.len() as u32;
        }
        crate::OK
    }

    unsafe fn feed_line(handle: i32, line: &str) {
        let mut line = line.to_string();
        line.push('\n'); // MCP stdio framing: newline-delimited JSON
        assert_eq!(
            unsafe { rttsh_mcp_feed(handle, line.as_ptr(), line.len() as u32) },
            OK
        );
    }

    /// Pumps wait+drain until `needle` shows up in the accumulated server output. Each
    /// cycle drains the queue COMPLETELY (a tools/list frame is larger than any single
    /// drain buffer, and the needle can sit early in the frame while the assertion needs
    /// the tail), then checks.
    unsafe fn drain_until(handle: i32, needle: &str, timeout: Duration) -> String {
        let deadline = Instant::now() + timeout;
        let mut acc = String::new();
        loop {
            loop {
                let mut buf = [0u8; 16384];
                let n = unsafe { rttsh_mcp_drain(handle, buf.as_mut_ptr(), buf.len() as u32) };
                assert!(n >= 0);
                if n == 0 {
                    break;
                }
                acc.push_str(&String::from_utf8_lossy(&buf[..n as usize]));
            }
            if acc.contains(needle) {
                return acc;
            }
            rttsh_mcp_wait(handle, 50);
            assert!(
                Instant::now() < deadline,
                "timed out waiting for {needle:?}; got: {acc}"
            );
        }
    }

    #[test]
    fn full_stack_initialize_list_and_call() {
        let handle = rttsh_mcp_start(Some(fake_dispatch));
        assert!(handle > 0);

        // Standard client handshake, then the tool surface, then a tool call whose answer
        // comes from the fake C# host through the dispatch callback and back out.
        unsafe {
            feed_line(
                handle,
                r#"{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"t","version":"0"}}}"#,
            );
            feed_line(
                handle,
                r#"{"jsonrpc":"2.0","method":"notifications/initialized"}"#,
            );
        }
        unsafe { drain_until(handle, "\"serverInfo\"", Duration::from_secs(5)) };

        unsafe { feed_line(handle, r#"{"jsonrpc":"2.0","id":2,"method":"tools/list"}"#) };
        let tools = unsafe { drain_until(handle, "\"id\":2", Duration::from_secs(5)) };
        for name in [
            "connect",
            "disconnect",
            "get_status",
            "send",
            "rtt_read",
            "expect",
            "mem_read",
            "list_devices",
        ] {
            assert!(tools.contains(name), "tools/list missing {name}");
        }

        unsafe {
            feed_line(
                handle,
                r#"{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"get_status","arguments":{}}}"#,
            );
        }
        let call = unsafe { drain_until(handle, "connected: false", Duration::from_secs(5)) };
        assert!(call.contains("\"id\":3"));

        // A failing tool call comes back as an isError tool result (the message stays
        // model-visible), not as a JSON-RPC protocol error.
        unsafe {
            feed_line(
                handle,
                r#"{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"get_status","arguments":{}}}"#,
            );
        }
        let failure = unsafe { drain_until(handle, "not connected - call connect first", Duration::from_secs(5)) };
        assert!(failure.contains("\"id\":4"));
        assert!(failure.contains("\"isError\":true"), "ok=false must map to isError: {failure}");

        assert_eq!(rttsh_mcp_stop(handle), OK);
    }

    #[test]
    fn stop_is_bounded_while_a_tool_call_is_in_flight() {
        // A dispatch that stalls, like a C# expect against quiet hardware would.
        unsafe extern "system" fn slow_dispatch(
            _handle: i32,
            _method: *const u8,
            _method_len: u32,
            _request: *const u8,
            _request_len: u32,
            resp_out: *mut *mut u8,
            resp_len_out: *mut u32,
        ) -> i32 {
            std::thread::sleep(Duration::from_millis(300));
            let text = r#"{"ok":true,"text":"done"}"#;
            unsafe { respond(text.as_bytes(), resp_out, resp_len_out) }
        }

        let handle = rttsh_mcp_start(Some(slow_dispatch));
        assert!(handle > 0);
        unsafe {
            feed_line(
                handle,
                r#"{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"t","version":"0"}}}"#,
            );
        }
        let _ = unsafe { drain_until(handle, "\"serverInfo\"", Duration::from_secs(5)) };
        unsafe {
            feed_line(
                handle,
                r#"{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_status","arguments":{}}}"#,
            );
        }
        std::thread::sleep(Duration::from_millis(50)); // let the call enter the dispatch

        let started = Instant::now();
        assert_eq!(rttsh_mcp_stop(handle), OK);
        assert!(
            started.elapsed() < Duration::from_secs(5),
            "stop took {:?} - the shutdown grace bound is 2s plus margin",
            started.elapsed()
        );
    }
}
