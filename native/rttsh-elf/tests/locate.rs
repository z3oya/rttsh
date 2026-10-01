//! Control-block location: every policy rung with its verbatim reason (these strings
//! are user-visible CLI warnings - snapshot byte for byte) and the size-gate edges.

mod common;

use common::*;
use rttsh_elf_native::{parse, LocateStatus};

fn locate_of(builder: &ElfBuilder) -> rttsh_elf_native::Locate {
    rttsh_elf_native::locate(&parse(&builder.build()).expect("valid image"))
}

fn cb(address: u32, size: u32) -> ElfBuilder {
    ElfBuilder {
        symbols: vec![Sym::new(
            "_SEGGER_RTT",
            address,
            size,
            TYPE_OBJECT,
            BIND_GLOBAL,
        )],
        ..ElfBuilder::default()
    }
}

#[test]
fn a_plausible_block_resolves_with_the_confirmation_note() {
    let result = locate_of(&cb(0x2400_0070, 168));
    assert_eq!(result.status, LocateStatus::Resolved);
    assert_eq!(result.address, 0x2400_0070);
    assert_eq!(
        result.reason,
        "resolved _SEGGER_RTT at 0x24000070 (size 168); verify the \"SEGGER RTT\" ID on target before trusting it"
    );
}

#[test]
fn size_gate_edges() {
    // 64 = smallest legal block (one up + one down, legacy 20-byte descriptors):
    // body 40 = 2*20. 72 = one 24-byte-descriptor buffer (body 48 = 2*24).
    // Everything below 64, the zero-buffer block, and non-multiple bodies fail.
    for (size, plausible) in [
        (24u32, false),
        (44, false),
        (63, false),
        (64, true),
        (65, false),
        (72, true),
        (84, true),
        (96, true),
        (168, true),
        (999, false),
    ] {
        let result = locate_of(&cb(0x2400_0000, size));
        assert_eq!(
            result.status,
            if plausible {
                LocateStatus::Resolved
            } else {
                LocateStatus::ImplausibleSize
            },
            "size {size}"
        );
    }
}

#[test]
fn implausible_sizes_name_the_reason() {
    let result = locate_of(&cb(0x2400_0000, 24));
    assert_eq!(result.status, LocateStatus::ImplausibleSize);
    assert_eq!(
        result.reason,
        "implausible _SEGGER_RTT size 24 (control block is 16+2*4 bytes plus 20- or 24-byte buffer descriptors) - image likely from a different build"
    );
    assert!(locate_of(&cb(0x2400_0000, 999)).reason.contains("size 999"));
}

#[test]
fn a_missing_symbol_falls_back_to_the_scan() {
    let result = locate_of(&ElfBuilder::default());
    assert_eq!(result.status, LocateStatus::SymbolMissing);
    assert_eq!(result.address, 0);
    assert_eq!(
        result.reason,
        "_SEGGER_RTT not in the symbol table (firmware built without RTT, or the image is stripped); fall back to the SDK RAM scan"
    );
}

#[test]
fn ambiguous_candidates_are_listed_by_address() {
    let result = locate_of(&ElfBuilder {
        symbols: vec![
            Sym::new("_SEGGER_RTT", 0x2400_1000, 168, TYPE_OBJECT, BIND_LOCAL),
            Sym::new("_SEGGER_RTT", 0x2400_0000, 168, TYPE_OBJECT, BIND_GLOBAL),
        ],
        ..ElfBuilder::default()
    });
    assert_eq!(result.status, LocateStatus::Ambiguous);
    assert_eq!(
        result.reason,
        "ambiguous _SEGGER_RTT: 2 OBJECT candidates at 0x24000000, 0x24001000"
    );
}

#[test]
fn the_object_filter_shields_the_gate_from_same_name_functions() {
    // A FUNC named _SEGGER_RTT with an implausible size must not matter: the lookup
    // is OBJECT-only (kind filter before uniqueness).
    let result = locate_of(&ElfBuilder {
        symbols: vec![
            Sym::new("_SEGGER_RTT", 0x2400_0000, 999, TYPE_FUNC, BIND_GLOBAL),
            Sym::new("_SEGGER_RTT", 0x2400_0070, 168, TYPE_OBJECT, BIND_GLOBAL),
        ],
        ..ElfBuilder::default()
    });
    assert_eq!(result.status, LocateStatus::Resolved);
    assert_eq!(result.address, 0x2400_0070);
}
