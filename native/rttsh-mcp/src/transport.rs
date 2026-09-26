//! In-process transport for the rmcp server. The C# host feeds client bytes in through
//! `rttsh_mcp_feed` and drains server bytes out through `rttsh_mcp_drain`, so the process
//! console stays under C# control — the MCP JSON-RPC stream must own stdout alone.

use std::collections::VecDeque;
use std::io;
use std::pin::Pin;
use std::sync::{Arc, Condvar, Mutex};
use std::task::{Context, Poll};
use std::time::{Duration, Instant};

use tokio::io::{AsyncRead, AsyncWrite, ReadBuf};
use tokio::sync::mpsc::Receiver;

/// Server → host bytes, with the wake-up pair behind `rttsh_mcp_wait`/`rttsh_mcp_drain`.
/// The lock-protected queue (not a channel) is deliberate: drain is a synchronous FFI call
/// that copies whatever fits the caller's buffer, and wait needs a timeout at the front.
#[derive(Default)]
pub(crate) struct Outbound {
    queue: Mutex<VecDeque<u8>>,
    signal: Condvar,
}

impl Outbound {
    pub(crate) fn push(&self, bytes: &[u8]) {
        if bytes.is_empty() {
            return;
        }
        let mut queue = self.queue.lock().unwrap_or_else(|e| e.into_inner());
        queue.extend(bytes.iter().copied());
        self.signal.notify_all();
    }

    /// Copies at most `buf.len()` bytes out; returns how many were copied (0 = empty).
    pub(crate) fn drain(&self, buf: &mut [u8]) -> usize {
        let mut queue = self.queue.lock().unwrap_or_else(|e| e.into_inner());
        let take = buf.len().min(queue.len());
        for slot in buf.iter_mut().take(take) {
            *slot = queue.pop_front().expect("len checked above");
        }
        take
    }

    /// Blocks until bytes are pending or the timeout elapses; returns the pending count
    /// (0 = timed out with nothing to drain). Positive returns follow the count convention.
    pub(crate) fn wait_available(&self, timeout: Duration) -> usize {
        let mut queue = self.queue.lock().unwrap_or_else(|e| e.into_inner());
        let deadline = Instant::now() + timeout;
        loop {
            let pending = queue.len();
            if pending > 0 {
                return pending;
            }
            let now = Instant::now();
            if now >= deadline {
                return 0;
            }
            let (woken, _) = self
                .signal
                .wait_timeout(queue, deadline - now)
                .unwrap_or_else(|e| e.into_inner());
            queue = woken;
        }
    }
}

/// Client bytes → the server's read half. EOF happens when the session's inbound sender
/// is dropped — the stop path uses that to end the service gracefully.
pub(crate) struct InboundStream {
    rx: Receiver<Vec<u8>>,
    leftover: Vec<u8>,
    consumed: usize,
}

impl InboundStream {
    pub(crate) fn new(rx: Receiver<Vec<u8>>) -> Self {
        Self {
            rx,
            leftover: Vec::new(),
            consumed: 0,
        }
    }
}

impl AsyncRead for InboundStream {
    fn poll_read(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buf: &mut ReadBuf<'_>,
    ) -> Poll<io::Result<()>> {
        loop {
            if self.consumed < self.leftover.len() {
                let take = buf.remaining().min(self.leftover.len() - self.consumed);
                buf.put_slice(&self.leftover[self.consumed..self.consumed + take]);
                self.consumed += take;
                if self.consumed == self.leftover.len() {
                    self.leftover.clear();
                    self.consumed = 0;
                }
                return Poll::Ready(Ok(()));
            }
            match self.rx.poll_recv(cx) {
                Poll::Ready(Some(chunk)) => {
                    self.leftover = chunk;
                    self.consumed = 0;
                }
                // EOF: report nothing read; rmcp ends the service.
                Poll::Ready(None) => return Poll::Ready(Ok(())),
                Poll::Pending => return Poll::Pending,
            }
        }
    }
}

/// The server's write half → the host-drainable outbound queue. rmcp writes whole
/// JSON-RPC frames; each one lands in the queue with a wake-up.
#[derive(Clone)]
pub(crate) struct OutboundSink {
    outbound: Arc<Outbound>,
}

impl OutboundSink {
    pub(crate) fn new(outbound: Arc<Outbound>) -> Self {
        Self { outbound }
    }
}

impl AsyncWrite for OutboundSink {
    fn poll_write(
        self: Pin<&mut Self>,
        _cx: &mut Context<'_>,
        buf: &[u8],
    ) -> Poll<io::Result<usize>> {
        self.outbound.push(buf);
        Poll::Ready(Ok(buf.len()))
    }

    fn poll_flush(self: Pin<&mut Self>, _cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        Poll::Ready(Ok(()))
    }

    fn poll_shutdown(self: Pin<&mut Self>, _cx: &mut Context<'_>) -> Poll<io::Result<()>> {
        Poll::Ready(Ok(()))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn wait_wakes_when_bytes_are_pushed() {
        let outbound = Arc::new(Outbound::default());
        let waiter = outbound.clone();
        let pusher = std::thread::spawn(move || {
            std::thread::sleep(Duration::from_millis(50));
            waiter.push(b"hello");
        });
        assert_eq!(outbound.wait_available(Duration::from_secs(2)), 5);
        pusher.join().unwrap();
    }

    #[test]
    fn wait_times_out_on_a_quiet_queue() {
        let outbound = Outbound::default();
        assert_eq!(outbound.wait_available(Duration::from_millis(30)), 0);
    }

    #[test]
    fn drain_copies_what_fits() {
        let outbound = Outbound::default();
        outbound.push(b"0123456789");
        let mut buf = [0u8; 4];
        assert_eq!(outbound.drain(&mut buf), 4);
        assert_eq!(&buf, b"0123");
        let mut rest = [0u8; 16];
        let n = outbound.drain(&mut rest);
        assert_eq!(&rest[..n], b"456789");
    }
}
