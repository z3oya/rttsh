//! RTT control-block location from a firmware image's symbol table. The reason
//! strings are user-visible CLI warnings - snapshot-tested byte for byte.
//!
//! The InvalidImage rung is not here: the C# facade guards null/non-Ok images
//! before calling and composes those reasons itself. This layer only sees a
//! parsed image.
//!
//! Plausibility gate: the unique OBJECT named _SEGGER_RTT must fit the SEGGER_RTT_CB
//! layout (16-byte ID + two ints + N buffer descriptors; descriptors are 24 bytes in
//! current SEGGER releases with the sName field, 20 bytes in legacy ones). 64 is the
//! smallest legal block (one up + one down buffer, legacy layout). Zero-buffer (24) or
//! oversized sizes fail - a wrong-build image should fail here with a nameable reason.
//! Deliberately no section-containment gate: a custom linker script may place the
//! block in an unusual (but valid) section, and a cross-build mismatch is caught later
//! by reading the 16-byte ID off the target.

use crate::lookup::{lookup, LookupStatus};
use crate::model::{ParsedElf, SymbolKind};

/// SEGGER_RTT.c's control-block variable; the de-facto lookup target across the RTT
/// tool ecosystem (RTT viewers, probe-rs, pyOCD all resolve this name).
pub const CONTROL_BLOCK_SYMBOL_NAME: &str = "_SEGGER_RTT";

#[repr(i32)]
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum LocateStatus {
    Resolved = 0,
    SymbolMissing = 1,
    Ambiguous = 2,
    ImplausibleSize = 3,
}

pub struct Locate {
    pub status: LocateStatus,
    /// The resolved address when Resolved.
    pub address: u32,
    /// One-line human-readable reason in every case (a confirmation note on success).
    pub reason: String,
}

pub fn locate(image: &ParsedElf) -> Locate {
    let found = lookup(image, CONTROL_BLOCK_SYMBOL_NAME, Some(SymbolKind::Object));
    match found.status {
        LookupStatus::NotFound => {
            return Locate {
                status: LocateStatus::SymbolMissing,
                address: 0,
                reason: format!(
                "{CONTROL_BLOCK_SYMBOL_NAME} not in the symbol table (firmware built without RTT, \
                     or the image is stripped); fall back to the SDK RAM scan"
            ),
            }
        }
        LookupStatus::Ambiguous => {
            let candidates = found
                .candidates
                .iter()
                .map(|symbol| format!("0x{:X}", symbol.address))
                .collect::<Vec<_>>()
                .join(", ");
            return Locate {
                status: LocateStatus::Ambiguous,
                address: 0,
                reason: format!(
                    "ambiguous {CONTROL_BLOCK_SYMBOL_NAME}: {} OBJECT candidates at {candidates}",
                    found.candidates.len()
                ),
            };
        }
        LookupStatus::Found => {}
    }

    let symbol = found.found.expect("Found carries exactly one symbol");
    let size = symbol.size;
    let body = size.checked_sub(24).unwrap_or(0);
    if size < 64 || (body % 24 != 0 && body % 20 != 0) {
        return Locate {
            status: LocateStatus::ImplausibleSize,
            address: 0,
            reason: format!(
                "implausible {CONTROL_BLOCK_SYMBOL_NAME} size {size} (control block is 16+2*4 bytes plus \
                 20- or 24-byte buffer descriptors) - image likely from a different build"
            ),
        };
    }

    let address = symbol.address as u32;
    Locate {
        status: LocateStatus::Resolved,
        address,
        reason: format!(
            "resolved {CONTROL_BLOCK_SYMBOL_NAME} at 0x{address:X} (size {size}); \
             verify the \"SEGGER RTT\" ID on target before trusting it"
        ),
    }
}
