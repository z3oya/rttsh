//! Parser tests: the error ladder (with its verbatim reason strings), materialization
//! semantics, endianness, the XINDEX escape hatch, and the exhaustive truncation sweep
//! that proves no input length can panic the parser.

mod common;

use common::*;
use rttsh_elf_native::{parse, LoadStatus, SymbolBinding, SymbolKind};

fn ok_reason(bytes: &[u8]) -> String {
    match parse(bytes) {
        Ok(_) => panic!("expected a parse failure"),
        Err((status, reason)) => {
            assert_ne!(status, LoadStatus::Ok);
            reason
        }
    }
}

fn status_of(bytes: &[u8]) -> LoadStatus {
    parse(bytes).map_or_else(|(status, _)| status, |_| LoadStatus::Ok)
}

#[test]
fn not_elf_ladder_and_reason() {
    assert_eq!(status_of(&[]), LoadStatus::NotElf);
    assert_eq!(status_of(b"ELF\x7f"), LoadStatus::NotElf);
    assert_eq!(status_of(&[0x7f]), LoadStatus::NotElf);
    assert_eq!(
        ok_reason(b"this is not an elf image at all"),
        r"not an ELF image (bad \x7fELF magic)"
    );
}

#[test]
fn truncated_before_the_class_field() {
    // Valid magic but the 16-byte e_ident is not there yet.
    let bytes: Vec<u8> = vec![0x7f, b'E', b'L', b'F', 1, 0, 0];
    assert_eq!(status_of(&bytes), LoadStatus::ParseFailed);
    assert_eq!(
        ok_reason(&bytes),
        "malformed ELF: truncated before the class field"
    );
}

#[test]
fn elf64_is_rejected_before_anything_else_parses() {
    let bytes = ElfBuilder {
        elf64_header: true,
        ..ElfBuilder::default()
    }
    .build();
    assert_eq!(status_of(&bytes), LoadStatus::UnsupportedClass);
    assert_eq!(
        ok_reason(&bytes),
        "ELF64 images are out of scope (ELF32 only)"
    );
}

#[test]
fn invalid_class_and_data_bytes() {
    let base = ElfBuilder::default().build();
    assert_eq!(
        ok_reason(&patch(&base, 4, &[3])),
        "malformed ELF: invalid EI_CLASS 3"
    );
    assert_eq!(
        ok_reason(&patch(&base, 5, &[0])),
        "malformed ELF: invalid EI_DATA 0"
    );
    assert_eq!(
        ok_reason(&patch(&base, 5, &[3])),
        "malformed ELF: invalid EI_DATA 3"
    );
}

#[test]
fn truncated_elf_header() {
    let full = ElfBuilder::default().build();
    assert_eq!(status_of(&full[..30]), LoadStatus::ParseFailed);
    assert_eq!(
        ok_reason(&full[..30]),
        "malformed ELF: truncated ELF header"
    );
}

#[test]
fn unsupported_section_header_entry_size() {
    let mut full = ElfBuilder::default().build();
    // e_shentsize lives at header offset 46; claim 48-byte entries.
    patch_u16(&mut full, 46, 48);
    assert_eq!(
        ok_reason(&full),
        "malformed ELF: unsupported section header entry size 48"
    );
}

#[test]
fn truncated_section_header_table() {
    let mut full = ElfBuilder::default().build();
    // e_shoff at offset 32: point it past the end of the file.
    patch_u32(&mut full, 32, u32::MAX - 8);
    assert_eq!(
        ok_reason(&full),
        "malformed ELF: truncated section header table"
    );
}

#[test]
fn section_name_out_of_string_table_bounds() {
    let mut full = ElfBuilder::default().build();
    let shoff = u32_le(&full, 32) as usize;
    // First section header's sh_name: point it outside .shstrtab.
    patch_u32(&mut full, shoff, 0xffff_ffff);
    assert_eq!(
        ok_reason(&full),
        "malformed ELF: name out of string table bounds"
    );
}

#[test]
fn no_section_table_is_an_empty_but_valid_image() {
    let mut full = ElfBuilder::default().build();
    // e_shoff = 0 and e_shnum = 0: a fully stripped image has nothing to materialize.
    patch_u32(&mut full, 32, 0);
    patch_u16(&mut full, 48, 0);
    let image = parse(&full).expect("valid empty image");
    assert!(image.symbols.is_empty());
    assert!(image.sections.is_empty());
}

#[test]
fn xindex_section_count_is_supported() {
    let normal = ElfBuilder::default().build();
    let xindex = ElfBuilder {
        xindex_shnum: true,
        ..ElfBuilder::default()
    }
    .build();
    let a = parse(&normal).expect("plain image");
    let b = parse(&xindex).expect("xindex image");
    assert_eq!(a.sections.len(), b.sections.len());
    assert_eq!(
        a.symbols
            .iter()
            .map(|s| s.name.as_str())
            .collect::<Vec<_>>(),
        b.symbols
            .iter()
            .map(|s| s.name.as_str())
            .collect::<Vec<_>>()
    );
}

#[test]
fn symbols_materialize_sorted_by_address() {
    let bytes = ElfBuilder {
        symbols: vec![
            Sym::new("late", 0x2400_1000, 4, TYPE_OBJECT, BIND_GLOBAL),
            Sym::new("early", 0x2400_0000, 8, TYPE_FUNC, BIND_GLOBAL),
            Sym::new("middle", 0x2400_0800, 16, TYPE_OBJECT, BIND_LOCAL),
        ],
        ..Default::default()
    }
    .build();
    let image = parse(&bytes).expect("valid image");
    let names: Vec<&str> = image.symbols.iter().map(|s| s.name.as_str()).collect();
    assert_eq!(names, ["early", "middle", "late"]);
}

#[test]
fn abs_symbols_materialize_like_any_other() {
    // SHN_ABS (absolute, not section-relative) is common for linker-defined markers;
    // st_shndx never matters to the materialization, only the raw value does.
    let bytes = ElfBuilder {
        symbols: vec![Sym {
            name: "abs_marker".into(),
            value: 0x1234,
            size: 0,
            ty: TYPE_NOT_TYPED,
            bind: BIND_LOCAL,
            shndx: SHN_ABS,
        }],
        ..Default::default()
    }
    .build();
    let image = parse(&bytes).expect("valid image");
    assert_eq!(image.symbols.len(), 1);
    assert_eq!(image.symbols[0].name, "abs_marker");
    assert_eq!(image.symbols[0].address, 0x1234);
}

#[test]
fn kind_and_binding_nibbles_map_with_other_fallback() {
    let bytes = ElfBuilder {
        symbols: vec![
            Sym::new("t0", 0x10, 0, TYPE_NOT_TYPED, BIND_LOCAL),
            Sym::new("t1", 0x11, 0, TYPE_OBJECT, BIND_GLOBAL),
            Sym::new("t2", 0x12, 0, TYPE_FUNC, BIND_WEAK),
            Sym::new("t3", 0x13, 0, TYPE_SECTION, 3),
            Sym::new("t4", 0x14, 0, TYPE_FILE, 5),
            Sym::new("t9", 0x15, 0, 9, 9),
        ],
        ..Default::default()
    }
    .build();
    let image = parse(&bytes).expect("valid image");
    let by_name = |name: &str| image.symbols.iter().find(|s| s.name == name).unwrap();
    assert_eq!(by_name("t0").kind, SymbolKind::NotTyped);
    assert_eq!(by_name("t1").kind, SymbolKind::Object);
    assert_eq!(by_name("t2").kind, SymbolKind::Function);
    assert_eq!(by_name("t3").kind, SymbolKind::Section);
    assert_eq!(by_name("t4").kind, SymbolKind::File);
    assert_eq!(by_name("t9").kind, SymbolKind::Other);
    assert_eq!(by_name("t0").binding, SymbolBinding::Local);
    assert_eq!(by_name("t1").binding, SymbolBinding::Global);
    assert_eq!(by_name("t2").binding, SymbolBinding::Weak);
    assert_eq!(by_name("t3").binding, SymbolBinding::Other);
    assert_eq!(by_name("t9").binding, SymbolBinding::Other);
}

#[test]
fn dynsym_is_ignored_and_unnamed_entries_skipped() {
    let bytes = ElfBuilder {
        with_dynsym: true,
        symbols: vec![Sym::new("static", 0x100, 4, TYPE_OBJECT, BIND_LOCAL)],
        dynamic_symbols: vec![Sym::new("dynamic", 0x200, 4, TYPE_FUNC, BIND_GLOBAL)],
        ..Default::default()
    }
    .build();
    let image = parse(&bytes).expect("valid image");
    let names: Vec<&str> = image.symbols.iter().map(|s| s.name.as_str()).collect();
    assert_eq!(names, ["static"]); // .dynsym never reaches the materialized list
}

#[test]
fn sections_keep_file_order_and_the_nobits_flag() {
    let bytes = ElfBuilder {
        data_sections: vec![DataSection {
            name: ".text".into(),
            address: 0x0800_0000,
            contents: vec![0xAA; 8],
        }],
        no_bits_sections: vec![NoBitsSection {
            name: ".bss".into(),
            address: 0x2000_0000,
            size: 64,
        }],
        symbols: vec![Sym::new(
            "marker",
            0x2000_0000,
            64,
            TYPE_OBJECT,
            BIND_GLOBAL,
        )],
        ..Default::default()
    }
    .build();
    let image = parse(&bytes).expect("valid image");
    let names: Vec<&str> = image.sections.iter().map(|s| s.name.as_str()).collect();
    // .symtab/.strtab/.shstrtab are named sections too - file order, not a curated list.
    assert_eq!(names, [".text", ".bss", ".symtab", ".strtab", ".shstrtab"]);
    let text = &image.sections[0];
    assert!(text.has_contents);
    assert_eq!(text.address, 0x0800_0000);
    assert_eq!(text.size, 8);
    assert!(text.file_offset > 0);
    let bss = &image.sections[1];
    assert!(!bss.has_contents);
    assert_eq!(bss.size, 64);
}

#[test]
fn big_endian_images_parse_and_resolve_symbols() {
    let bytes = ElfBuilder {
        big_endian: true,
        symbols: vec![Sym::new(
            "be_cb",
            0x2400_0070,
            168,
            TYPE_OBJECT,
            BIND_GLOBAL,
        )],
        data_sections: vec![DataSection {
            name: ".data".into(),
            address: 0x2400_0000,
            contents: vec![1, 2, 3, 4],
        }],
        ..Default::default()
    }
    .build();
    let image = parse(&bytes).expect("big-endian image");
    assert!(!image.little_endian);
    let symbol = &image.symbols[0];
    assert_eq!(symbol.name, "be_cb");
    assert_eq!(symbol.address, 0x2400_0070);
    assert_eq!(symbol.size, 168);
    assert_eq!(image.sections[0].address, 0x2400_0000);
    assert_eq!(image.sections[0].size, 4);
}

#[test]
fn every_truncation_length_lands_on_the_ladder_without_panicking() {
    let samples = vec![
        ElfBuilder::default().build(),
        ElfBuilder {
            data_sections: vec![DataSection {
                name: ".text".into(),
                address: 0x0800_0000,
                contents: vec![0x42; 16],
            }],
            no_bits_sections: vec![NoBitsSection {
                name: ".bss".into(),
                address: 0x2000_0000,
                size: 32,
            }],
            symbols: vec![
                Sym::new("_SEGGER_RTT", 0x2400_0070, 168, TYPE_OBJECT, BIND_GLOBAL),
                Sym::new("abs_marker", 0x1234, 0, TYPE_NOT_TYPED, BIND_LOCAL),
            ],
            ..Default::default()
        }
        .build(),
        ElfBuilder {
            big_endian: true,
            with_dynsym: true,
            dynamic_symbols: vec![Sym::new("dyn", 4, 4, TYPE_FUNC, BIND_GLOBAL)],
            ..ElfBuilder::default()
        }
        .build(),
    ];
    for sample in &samples {
        for len in 0..sample.len() {
            let outcome = parse(&sample[..len]);
            if let Err((status, reason)) = outcome {
                assert_ne!(status, LoadStatus::Ok);
                assert!(!reason.is_empty(), "length {len} produced an empty reason");
            }
        }
    }
}

fn patch(bytes: &[u8], offset: usize, replacement: &[u8]) -> Vec<u8> {
    let mut out = bytes.to_vec();
    out[offset..offset + replacement.len()].copy_from_slice(replacement);
    out
}

fn patch_u16(bytes: &mut Vec<u8>, offset: usize, value: u16) {
    bytes[offset..offset + 2].copy_from_slice(&value.to_le_bytes());
}

fn patch_u32(bytes: &mut Vec<u8>, offset: usize, value: u32) {
    bytes[offset..offset + 4].copy_from_slice(&value.to_le_bytes());
}

fn u32_le(bytes: &[u8], offset: usize) -> u32 {
    u32::from_le_bytes(bytes[offset..offset + 4].try_into().unwrap())
}
