//! Hand-written ELF32 reader. The format subset is exactly what the C# ElfBuilder
//! (tests) assembles and what real firmware emits: ELF header, section table,
//! .shstrtab, the first .symtab and its string table. Every read is bounds-checked -
//! a structural problem becomes a status on the error ladder, never a panic and never
//! an exception crossing the FFI boundary.
//!
//! Ladder order is load-bearing: magic, then truncation, then EI_CLASS - so a
//! class byte of 2 is reported as out-of-scope even when the rest is garbage.
//! Only SHT_SYMTAB is read (.dynsym is ignored: bare-metal images have no dynamic
//! linker); entries without a name are not materialized; symbols are address-sorted
//! with a stable sort, keeping symtab order for ties.

use crate::model::{LoadStatus, ParsedElf, Section, Symbol, SymbolBinding, SymbolKind};

const SHT_PROGBITS: u32 = 1;
const SHT_SYMTAB: u32 = 2;
const SHT_NOBITS: u32 = 8;

const SHN_XINDEX: u16 = 0xffff;

const EHDR_SIZE: usize = 52;
const SHDR_SIZE: usize = 40;
const SYM_SIZE: usize = 16;

/// Failure payload: the ladder status plus a one-line, deterministic reason.
type LoadFailure = (LoadStatus, String);

fn failed(status: LoadStatus, reason: impl Into<String>) -> LoadFailure {
    (status, reason.into())
}

/// A bounds-checked byte-order-aware view over the image bytes.
struct Reader<'a> {
    bytes: &'a [u8],
    little_endian: bool,
}

impl Reader<'_> {
    fn u16(&self, offset: usize) -> Option<u16> {
        let raw = self.bytes.get(offset..offset + 2)?;
        Some(if self.little_endian {
            u16::from_le_bytes(raw.try_into().ok()?)
        } else {
            u16::from_be_bytes(raw.try_into().ok()?)
        })
    }

    fn u32(&self, offset: usize) -> Option<u32> {
        let raw = self.bytes.get(offset..offset + 4)?;
        Some(if self.little_endian {
            u32::from_le_bytes(raw.try_into().ok()?)
        } else {
            u32::from_be_bytes(raw.try_into().ok()?)
        })
    }

    /// The file bytes a section body claims; rejects NOBITS-style sections whose
    /// size extends past the file (there are no file-backed bytes there).
    fn body(&self, offset: u32, size: u32) -> Option<&[u8]> {
        let start = offset as usize;
        let end = start.checked_add(size as usize)?;
        self.bytes.get(start..end)
    }

    /// NUL-terminated entry of a string table, decoded once and lossily (malformed
    /// UTF-8 becomes U+FFFD); the decoded string is what crosses every later
    /// boundary - blob, C# records - so both sides see the same name.
    fn c_str(&self, table: &[u8], offset: u32) -> Option<String> {
        let start = offset as usize;
        let rest = table.get(start..)?;
        let end = rest.iter().position(|&b| b == 0)?;
        Some(String::from_utf8_lossy(&rest[..end]).into_owned())
    }
}

/// Parses an in-memory image. Never panics: any structural problem is a
/// `(LoadStatus, reason)` failure.
pub fn parse(bytes: &[u8]) -> Result<ParsedElf, LoadFailure> {
    if bytes.len() < 4 || bytes[..4] != [0x7f, b'E', b'L', b'F'] {
        return Err(failed(
            LoadStatus::NotElf,
            r"not an ELF image (bad \x7fELF magic)",
        ));
    }
    if bytes.len() < 16 {
        return Err(failed(
            LoadStatus::ParseFailed,
            "malformed ELF: truncated before the class field",
        ));
    }
    match bytes[4] {
        1 => {}
        2 => {
            return Err(failed(
                LoadStatus::UnsupportedClass,
                "ELF64 images are out of scope (ELF32 only)",
            ))
        }
        n => {
            return Err(failed(
                LoadStatus::ParseFailed,
                format!("malformed ELF: invalid EI_CLASS {n}"),
            ))
        }
    }
    let little_endian = match bytes[5] {
        1 => true,
        2 => false,
        n => {
            return Err(failed(
                LoadStatus::ParseFailed,
                format!("malformed ELF: invalid EI_DATA {n}"),
            ))
        }
    };

    if bytes.len() < EHDR_SIZE {
        return Err(failed(
            LoadStatus::ParseFailed,
            "malformed ELF: truncated ELF header",
        ));
    }
    let r = Reader {
        bytes,
        little_endian,
    };
    let e_shoff = r.u32(32).expect("header bounds checked above");
    let e_shentsize = r.u16(46).expect("header bounds checked above");
    let e_shnum = r.u16(48).expect("header bounds checked above");
    let e_shstrndx = r.u16(50).expect("header bounds checked above");

    if e_shoff == 0 {
        if e_shnum != 0 {
            return Err(failed(
                LoadStatus::ParseFailed,
                "malformed ELF: section header table offset is null but sections are declared",
            ));
        }
        // No section table at all (fully stripped image): nothing to materialize.
        return Ok(ParsedElf::new(little_endian, Vec::new(), Vec::new()));
    }
    if e_shentsize as usize != SHDR_SIZE {
        return Err(failed(
            LoadStatus::ParseFailed,
            format!("malformed ELF: unsupported section header entry size {e_shentsize}"),
        ));
    }

    // e_shnum == 0 with a table present is the >64k-sections escape hatch: the real
    // count hides in sh_size of section 0 (and shstrndx, if XINDEX, in its sh_link).
    let mut section_count = e_shnum as u64;
    let mut shstrndx = e_shstrndx;
    let first = read_shdr(&r, e_shoff as usize, 0).ok_or_else(|| {
        failed(
            LoadStatus::ParseFailed,
            "malformed ELF: truncated section header table",
        )
    })?;
    if section_count == 0 {
        section_count = first.sh_size as u64;
        if shstrndx == SHN_XINDEX {
            shstrndx = first.sh_link as u16;
        }
    }

    // The XINDEX count comes from a u32 field, so it must be bounded before any
    // capacity is reserved: a count that cannot fit in the file is truncation.
    let table_end = section_count
        .checked_mul(SHDR_SIZE as u64)
        .and_then(|total| total.checked_add(e_shoff as u64));
    if table_end.map_or(true, |end| end > bytes.len() as u64) {
        return Err(failed(
            LoadStatus::ParseFailed,
            "malformed ELF: truncated section header table",
        ));
    }
    let mut headers = Vec::with_capacity(section_count as usize);
    for index in 0..section_count as usize {
        headers.push(read_shdr(&r, e_shoff as usize, index).ok_or_else(|| {
            failed(
                LoadStatus::ParseFailed,
                "malformed ELF: truncated section header table",
            )
        })?);
    }

    // Section names: shstrndx == 0 (SHN_UNDEF) means no name string table - every
    // section is then unnamed and, per the named-only materialization, not listed.
    let shstrtab: Option<&[u8]> = if shstrndx == 0 {
        None
    } else {
        let header = headers.get(shstrndx as usize).ok_or_else(|| {
            failed(
                LoadStatus::ParseFailed,
                "malformed ELF: section header string table index out of range",
            )
        })?;
        Some(section_body(
            &r,
            header,
            "malformed ELF: truncated section name string table",
        )?)
    };

    let mut sections = Vec::new();
    let mut symtab: Option<&Shdr> = None;
    for header in &headers {
        if let Some(shstrtab) = shstrtab {
            if let Some(name) = name_of(&r, shstrtab, header.sh_name)? {
                sections.push(Section {
                    name,
                    address: header.sh_addr as u64,
                    size: header.sh_size as u64,
                    file_offset: header.sh_offset as i64,
                    has_contents: header.sh_type == SHT_PROGBITS,
                });
            }
        }
        if symtab.is_none() && header.sh_type == SHT_SYMTAB {
            symtab = Some(header);
        }
    }

    let mut symbols = Vec::new();
    if let Some(header) = symtab {
        let body = section_body(&r, header, "malformed ELF: truncated symbol table")?;
        if body.len() % SYM_SIZE != 0 {
            return Err(failed(
                LoadStatus::ParseFailed,
                "malformed ELF: truncated symbol table entry",
            ));
        }
        let strtab_header = headers.get(header.sh_link as usize).ok_or_else(|| {
            failed(
                LoadStatus::ParseFailed,
                "malformed ELF: symbol string table index out of range",
            )
        })?;
        if strtab_header.sh_type == SHT_NOBITS {
            return Err(failed(
                LoadStatus::ParseFailed,
                "malformed ELF: symbol string table has no file contents",
            ));
        }
        let strtab = section_body(
            &r,
            strtab_header,
            "malformed ELF: truncated symbol string table",
        )?;

        for entry in body.chunks_exact(SYM_SIZE) {
            let st_name = u32_at(entry, 0, little_endian);
            let st_value = u32_at(entry, 4, little_endian);
            let st_size = u32_at(entry, 8, little_endian);
            let st_info = entry[12];
            if st_name as usize >= strtab.len() {
                return Err(failed(
                    LoadStatus::ParseFailed,
                    "malformed ELF: symbol name out of string table bounds",
                ));
            }
            if let Some(name) = name_of(&r, strtab, st_name)? {
                symbols.push(Symbol {
                    name,
                    address: st_value as u64,
                    size: st_size as u64,
                    kind: SymbolKind::from_raw_type(st_info & 0x0f),
                    binding: SymbolBinding::from_raw_binding(st_info >> 4),
                });
            }
        }
    }
    // Stable sort: equal addresses keep symtab order (candidates come out deterministic).
    symbols.sort_by_key(|symbol| symbol.address);

    Ok(ParsedElf::new(little_endian, symbols, sections))
}

/// Minimal section-header view of the fields the reader needs.
struct Shdr {
    sh_name: u32,
    sh_type: u32,
    sh_addr: u32,
    sh_offset: u32,
    sh_size: u32,
    sh_link: u32,
}

fn read_shdr(r: &Reader, table_offset: usize, index: usize) -> Option<Shdr> {
    let base = table_offset.checked_add(index.checked_mul(SHDR_SIZE)?)?;
    Some(Shdr {
        sh_name: r.u32(base)?,
        sh_type: r.u32(base + 4)?,
        sh_addr: r.u32(base + 12)?,
        sh_offset: r.u32(base + 16)?,
        sh_size: r.u32(base + 20)?,
        sh_link: r.u32(base + 24)?,
    })
}

/// File bytes of a section body, mapped onto the given failure reason when out of bounds.
fn section_body<'a>(
    r: &'a Reader,
    header: &Shdr,
    reason: &'static str,
) -> Result<&'a [u8], LoadFailure> {
    r.body(header.sh_offset, header.sh_size)
        .ok_or_else(|| failed(LoadStatus::ParseFailed, reason))
}

/// Resolves a string-table entry; `None` is the unnamed case (offset 0 / empty string),
/// which the named-only materialization skips. Out-of-table offsets are structural errors.
fn name_of(r: &Reader, table: &[u8], offset: u32) -> Result<Option<String>, LoadFailure> {
    if offset as usize >= table.len() {
        return Err(failed(
            LoadStatus::ParseFailed,
            "malformed ELF: name out of string table bounds",
        ));
    }
    Ok(r.c_str(table, offset).filter(|name| !name.is_empty()))
}

fn u32_at(bytes: &[u8], offset: usize, little_endian: bool) -> u32 {
    let raw: [u8; 4] = bytes[offset..offset + 4]
        .try_into()
        .expect("fixed-size chunk");
    if little_endian {
        u32::from_le_bytes(raw)
    } else {
        u32::from_be_bytes(raw)
    }
}
