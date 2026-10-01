//! Domain model: the materialized view of an ELF32 image. FFI carries the enum
//! values as `u8`/`i32` codes — the discriminants below are the ABI contract with
//! `ElfSymbolKind`/`ElfSymbolBinding` on the C# side and must not be reordered.

/// Symbol classes, mapped from the st_info type nibble in [`crate::parse`].
/// Unmapped/processor-specific values collapse to `Other` rather than failing.
#[repr(u8)]
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum SymbolKind {
    NotTyped = 0,
    Object = 1,
    Function = 2,
    Section = 3,
    File = 4,
    Other = 5,
}

impl SymbolKind {
    /// FFI kind-filter sentinel on `rttsh_elf_lookup`: any kind matches.
    pub const ANY: i32 = -1;

    pub(crate) fn from_raw_type(raw: u8) -> SymbolKind {
        match raw {
            0 => SymbolKind::NotTyped,
            1 => SymbolKind::Object,
            2 => SymbolKind::Function,
            3 => SymbolKind::Section,
            4 => SymbolKind::File,
            _ => SymbolKind::Other,
        }
    }

    /// Accepts the FFI wire code (0..=5) for a lookup filter. The outer Option says
    /// whether the code is valid at all (invalid → caller error); the inner one is
    /// the filter itself - `None` inside means "any kind matches".
    pub fn from_code(code: i32) -> Option<Option<SymbolKind>> {
        match code {
            SymbolKind::ANY => Some(None),
            0 => Some(Some(SymbolKind::NotTyped)),
            1 => Some(Some(SymbolKind::Object)),
            2 => Some(Some(SymbolKind::Function)),
            3 => Some(Some(SymbolKind::Section)),
            4 => Some(Some(SymbolKind::File)),
            5 => Some(Some(SymbolKind::Other)),
            _ => None,
        }
    }
}

/// Symbol linkage, mapped from the st_info binding nibble.
#[repr(u8)]
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum SymbolBinding {
    Local = 0,
    Global = 1,
    Weak = 2,
    Other = 3,
}

impl SymbolBinding {
    pub(crate) fn from_raw_binding(raw: u8) -> SymbolBinding {
        match raw {
            0 => SymbolBinding::Local,
            1 => SymbolBinding::Global,
            2 => SymbolBinding::Weak,
            _ => SymbolBinding::Other,
        }
    }
}

/// One .symtab entry as consumers see it: a name, where it lives, how big it is.
/// Addresses are raw st_value (no Thumb-bit or relocation post-processing).
#[derive(Clone, Debug)]
pub struct Symbol {
    pub name: String,
    pub address: u64,
    pub size: u64,
    pub kind: SymbolKind,
    pub binding: SymbolBinding,
}

/// Section metadata for consumers that need where bytes live, not just the bytes
/// themselves. `has_contents` is false for SHT_NOBITS and every other non-PROGBITS type.
#[derive(Clone, Debug)]
pub struct Section {
    pub name: String,
    pub address: u64,
    pub size: u64,
    pub file_offset: i64,
    pub has_contents: bool,
}

/// Load outcome, mirroring the C# `ElfLoadStatus` ladder. IO failures are not here -
/// they never reach the crate (the bytes arrive in memory; missing files are a
/// caller-side usage error).
#[repr(i32)]
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum LoadStatus {
    Ok = 0,
    NotElf = 1,
    UnsupportedClass = 2,
    ParseFailed = 3,
}

/// The parsed model owned behind an FFI handle. Everything is materialized eagerly;
/// lookups run against the by-name index built over the address-sorted symbol list
/// (so candidates come out in address order for free).
pub struct ParsedElf {
    pub little_endian: bool,
    /// Named .symtab entries, ordered by address.
    pub symbols: Vec<Symbol>,
    /// Named sections, in file order.
    pub sections: Vec<Section>,
    by_name: std::collections::HashMap<Box<str>, Vec<u32>>,
}

impl ParsedElf {
    pub fn new(little_endian: bool, symbols: Vec<Symbol>, sections: Vec<Section>) -> ParsedElf {
        let mut by_name: std::collections::HashMap<Box<str>, Vec<u32>> =
            std::collections::HashMap::new();
        // The symbols vec is address-sorted by the parser; index order = address order.
        for (index, symbol) in symbols.iter().enumerate() {
            by_name
                .entry(symbol.name.as_str().into())
                .or_default()
                .push(index as u32);
        }
        ParsedElf {
            little_endian,
            symbols,
            sections,
            by_name,
        }
    }

    /// Every entry with `name`, ordered by address; empty when unknown.
    pub fn same_name(&self, name: &str) -> Vec<&Symbol> {
        self.by_name
            .get(name)
            .map(|indices| indices.iter().map(|&i| &self.symbols[i as usize]).collect())
            .unwrap_or_default()
    }
}
