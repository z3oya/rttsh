//! Lookup semantics: kind filtering runs BEFORE the uniqueness check, candidates
//! come out in address order, and an empty name is not resolvable.

mod common;

use common::*;
use rttsh_elf_native::{lookup, parse, LookupStatus, ParsedElf, SymbolBinding, SymbolKind};

fn image() -> ParsedElf {
    parse(
        &ElfBuilder {
            symbols: vec![
                Sym::new("shared", 0x2400_1000, 4, TYPE_FUNC, BIND_GLOBAL),
                Sym::new("shared", 0x2400_0000, 8, TYPE_OBJECT, BIND_LOCAL),
                Sym::new("unique", 0x2400_2000, 12, TYPE_OBJECT, BIND_GLOBAL),
            ],
            ..Default::default()
        }
        .build(),
    )
    .expect("valid image")
}

#[test]
fn kind_filter_runs_before_the_uniqueness_check() {
    let image = image();
    let object = lookup(&image, "shared", Some(SymbolKind::Object));
    assert_eq!(object.status, LookupStatus::Found);
    assert_eq!(object.found.as_ref().unwrap().address, 0x2400_0000);

    let function = lookup(&image, "shared", Some(SymbolKind::Function));
    assert_eq!(function.status, LookupStatus::Found);
    assert_eq!(function.found.as_ref().unwrap().address, 0x2400_1000);
}

#[test]
fn unfiltered_lookup_of_a_doubly_defined_name_is_ambiguous_in_address_order() {
    let image = image();
    let result = lookup(&image, "shared", None);
    assert_eq!(result.status, LookupStatus::Ambiguous);
    let addresses: Vec<u64> = result.candidates.iter().map(|s| s.address).collect();
    assert_eq!(addresses, [0x2400_0000, 0x2400_1000]);
    assert!(result.found.is_none());
}

#[test]
fn unknown_and_empty_names_are_not_found() {
    let image = image();
    assert_eq!(
        lookup(&image, "absent", None).status,
        LookupStatus::NotFound
    );
    assert_eq!(lookup(&image, "", None).status, LookupStatus::NotFound);
    assert!(lookup(&image, "", None).candidates.is_empty());
}

#[test]
fn found_symbols_carry_the_full_record() {
    let image = image();
    let result = lookup(&image, "unique", Some(SymbolKind::Object));
    let symbol = result.found.as_ref().expect("found");
    assert_eq!(symbol.name, "unique");
    assert_eq!(symbol.address, 0x2400_2000);
    assert_eq!(symbol.size, 12);
    assert_eq!(symbol.kind, SymbolKind::Object);
    assert_eq!(symbol.binding, SymbolBinding::Global);
}
