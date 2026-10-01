//! Synthetic ELF32 builder for the Rust-side tests - the independent twin of the C#
//! ElfBuilder (tests/RttSh.Tests/TestSupport/ElfBuilder.cs): glue tests feed images
//! built by one to the parser and facade of the other. Emits a minimal but fully valid
//! image (ELF header, the sections you declare, optional .symtab/.dynsym, .shstrtab)
//! so every parser branch is exercised without real firmware. Pure byte assembly, no
//! involvement of the code under test, so parser failures cannot be masked by builder
//! failures. Symbols are given as raw ELF encodings (type/bind nibbles) so a
//! mis-mapping in the parser shows up as a test failure instead of silently matching
//! the builder's vocabulary.

// Each test binary links this module and uses a different subset of it.
#![allow(dead_code)]

use std::collections::HashMap;

pub const TYPE_NOT_TYPED: u8 = 0;
pub const TYPE_OBJECT: u8 = 1;
pub const TYPE_FUNC: u8 = 2;
pub const TYPE_SECTION: u8 = 3;
pub const TYPE_FILE: u8 = 4;

pub const BIND_LOCAL: u8 = 0;
pub const BIND_GLOBAL: u8 = 1;
pub const BIND_WEAK: u8 = 2;

pub const SHN_ABS: u16 = 0xfff1;

pub struct Sym {
    pub name: String,
    pub value: u32,
    pub size: u32,
    pub ty: u8,
    pub bind: u8,
    pub shndx: u16,
}

impl Sym {
    pub fn new(name: &str, value: u32, size: u32, ty: u8, bind: u8) -> Sym {
        Sym {
            name: name.into(),
            value,
            size,
            ty,
            bind,
            shndx: 1,
        }
    }
}

pub struct DataSection {
    pub name: String,
    pub address: u32,
    pub contents: Vec<u8>,
}

pub struct NoBitsSection {
    pub name: String,
    pub address: u32,
    pub size: u32,
}

pub struct ElfBuilder {
    pub big_endian: bool,
    /// Emits a header whose EI_CLASS is 2 (ELF64) with a zeroed body: enough for the
    /// class pre-check, which must reject it before any parser runs.
    pub elf64_header: bool,
    pub with_symtab: bool,
    pub with_dynsym: bool,
    /// Emits e_shnum = 0 with the real count in section 0's sh_size (and shstrndx in
    /// its sh_link): the >64k-sections escape hatch, exercised at small scale.
    pub xindex_shnum: bool,
    pub symbols: Vec<Sym>,
    pub dynamic_symbols: Vec<Sym>,
    pub data_sections: Vec<DataSection>,
    pub no_bits_sections: Vec<NoBitsSection>,
}

impl Default for ElfBuilder {
    fn default() -> Self {
        ElfBuilder {
            big_endian: false,
            elf64_header: false,
            with_symtab: true,
            with_dynsym: false,
            xindex_shnum: false,
            symbols: Vec::new(),
            dynamic_symbols: Vec::new(),
            data_sections: Vec::new(),
            no_bits_sections: Vec::new(),
        }
    }
}

impl ElfBuilder {
    pub fn build(&self) -> Vec<u8> {
        if self.elf64_header {
            let mut h = vec![0u8; 64];
            h[..4].copy_from_slice(&[0x7f, b'E', b'L', b'F']);
            h[4] = 2; // ELFCLASS64
            h[5] = if self.big_endian { 2 } else { 1 };
            return h;
        }

        let mut symbol_names: Vec<&str> = self.symbols.iter().map(|s| s.name.as_str()).collect();
        symbol_names.extend(self.dynamic_symbols.iter().map(|s| s.name.as_str()));
        let (strtab, strtab_offsets) = build_strtab(if self.with_symtab {
            &symbol_names[..]
        } else {
            &[]
        });
        let dyn_names: Vec<&str> = self
            .dynamic_symbols
            .iter()
            .map(|s| s.name.as_str())
            .collect();
        let (dynstr, dynstr_offsets) = build_strtab(if self.with_dynsym {
            &dyn_names[..]
        } else {
            &[]
        });

        let mut section_names: Vec<&str> = vec![""];
        section_names.extend(self.data_sections.iter().map(|d| d.name.as_str()));
        section_names.extend(self.no_bits_sections.iter().map(|s| s.name.as_str()));
        if self.with_symtab {
            section_names.push(".symtab");
            section_names.push(".strtab");
        }
        if self.with_dynsym {
            section_names.push(".dynsym");
            section_names.push(".dynstr");
        }
        section_names.push(".shstrtab");
        let (shstrtab, shstrtab_offsets) = build_strtab(&section_names);
        let name_off = |name: &str| -> u32 { shstrtab_offsets.get(name).copied().unwrap_or(0) };

        let mut sections: Vec<Shdr> = vec![Shdr::null()];
        for d in &self.data_sections {
            sections.push(Shdr::progbits(
                name_off(&d.name),
                d.address,
                d.contents.clone(),
            ));
        }
        for n in &self.no_bits_sections {
            sections.push(Shdr::nobits(name_off(&n.name), n.address, n.size));
        }
        if self.with_symtab {
            let link = (sections.len() + 1) as u32;
            sections.push(Shdr::symtab(
                name_off(".symtab"),
                build_symtab(&self.symbols, &strtab_offsets, self.big_endian),
                link,
            ));
            sections.push(Shdr::strtab(name_off(".strtab"), strtab));
        }
        if self.with_dynsym {
            let link = (sections.len() + 1) as u32;
            sections.push(Shdr::dynsym(
                name_off(".dynsym"),
                build_symtab(&self.dynamic_symbols, &dynstr_offsets, self.big_endian),
                link,
            ));
            sections.push(Shdr::strtab(name_off(".dynstr"), dynstr));
        }
        sections.push(Shdr::strtab(name_off(".shstrtab"), shstrtab));
        let shstrndx = sections.len() - 1;

        // File layout: ehdr | bodies (4-aligned) | section header table.
        let mut shoff: u32 = 52;
        let mut offsets = vec![0u32; sections.len()];
        for (i, s) in sections.iter().enumerate() {
            if let Some(body) = &s.body {
                shoff = (shoff + 3) & !3;
                offsets[i] = shoff;
                shoff += body.len() as u32;
            }
        }
        shoff = (shoff + 3) & !3;

        let be = self.big_endian;
        let mut out: Vec<u8> = Vec::new();
        out.extend_from_slice(&[0x7f, b'E', b'L', b'F']);
        out.push(1); // EI_CLASS = ELF32
        out.push(if be { 2 } else { 1 }); // EI_DATA
        out.push(1); // EI_VERSION
        out.push(0); // EI_OSABI
        out.push(0); // EI_ABIVERSION
        out.extend_from_slice(&[0u8; 7]); // EI_PAD - e_ident totals 16 bytes
        w16(&mut out, 2, be); // e_type = EXEC
        w16(&mut out, 40, be); // e_machine = ARM
        w32(&mut out, 1, be); // e_version
        w32(&mut out, 0x0800_0000, be); // e_entry
        w32(&mut out, 0, be); // e_phoff
        w32(&mut out, shoff, be); // e_shoff
        w32(&mut out, 0, be); // e_flags
        w16(&mut out, 52, be); // e_ehsize
        w16(&mut out, 0, be); // e_phentsize
        w16(&mut out, 0, be); // e_phnum
        w16(&mut out, 40, be); // e_shentsize
        w16(
            &mut out,
            if self.xindex_shnum {
                0
            } else {
                sections.len() as u16
            },
            be,
        ); // e_shnum
        w16(&mut out, shstrndx as u16, be); // e_shstrndx

        for (i, s) in sections.iter().enumerate() {
            if let Some(body) = &s.body {
                pad_to(&mut out, offsets[i]);
                out.extend_from_slice(body);
            }
        }
        pad_to(&mut out, shoff);
        for (i, s) in sections.iter().enumerate() {
            w32(&mut out, s.name_off, be);
            w32(&mut out, s.sh_type, be);
            w32(&mut out, 0, be); // flags
            w32(&mut out, s.addr, be);
            w32(&mut out, if s.body.is_some() { offsets[i] } else { 0 }, be);
            // XINDEX: section 0 (the null section) carries the real count and shstrndx.
            let (size, link) = if i == 0 && self.xindex_shnum {
                (sections.len() as u32, shstrndx as u32)
            } else {
                (s.size, s.link)
            };
            w32(&mut out, size, be);
            w32(&mut out, link, be);
            w32(&mut out, s.info, be);
            w32(&mut out, s.align, be);
            w32(&mut out, s.entsize, be);
        }
        out
    }
}

/// One section header plus its file body (None for NOBITS and the null section) -
/// keeping them together is what makes the file layout unable to desync from the table.
struct Shdr {
    name_off: u32,
    sh_type: u32,
    addr: u32,
    size: u32,
    link: u32,
    info: u32,
    align: u32,
    entsize: u32,
    body: Option<Vec<u8>>,
}

impl Shdr {
    fn null() -> Shdr {
        Shdr {
            name_off: 0,
            sh_type: 0,
            addr: 0,
            size: 0,
            link: 0,
            info: 0,
            align: 0,
            entsize: 0,
            body: None,
        }
    }
    fn progbits(name_off: u32, addr: u32, body: Vec<u8>) -> Shdr {
        let size = body.len() as u32;
        Shdr {
            name_off,
            sh_type: 1,
            addr,
            size,
            link: 0,
            info: 0,
            align: 4,
            entsize: 0,
            body: Some(body),
        }
    }
    fn nobits(name_off: u32, addr: u32, size: u32) -> Shdr {
        Shdr {
            name_off,
            sh_type: 8,
            addr,
            size,
            link: 0,
            info: 0,
            align: 1,
            entsize: 0,
            body: None,
        }
    }
    fn symtab(name_off: u32, body: Vec<u8>, link: u32) -> Shdr {
        let size = body.len() as u32;
        Shdr {
            name_off,
            sh_type: 2,
            addr: 0,
            size,
            link,
            info: 1,
            align: 4,
            entsize: 16,
            body: Some(body),
        }
    }
    fn dynsym(name_off: u32, body: Vec<u8>, link: u32) -> Shdr {
        let size = body.len() as u32;
        Shdr {
            name_off,
            sh_type: 11,
            addr: 0,
            size,
            link,
            info: 1,
            align: 4,
            entsize: 16,
            body: Some(body),
        }
    }
    fn strtab(name_off: u32, body: Vec<u8>) -> Shdr {
        let size = body.len() as u32;
        Shdr {
            name_off,
            sh_type: 3,
            addr: 0,
            size,
            link: 0,
            info: 0,
            align: 1,
            entsize: 0,
            body: Some(body),
        }
    }
}

/// Builds a string table (offset 0 = NUL, each name NUL-terminated); first
/// occurrence of a name wins, so offsets cannot disagree with the body.
fn build_strtab(names: &[&str]) -> (Vec<u8>, HashMap<String, u32>) {
    let mut offsets: HashMap<String, u32> = HashMap::new();
    let mut body: Vec<u8> = vec![0];
    for name in names.iter().filter(|n| !n.is_empty()) {
        offsets.entry(name.to_string()).or_insert(body.len() as u32);
        body.extend_from_slice(name.as_bytes());
        body.push(0);
    }
    (body, offsets)
}

fn build_symtab(syms: &[Sym], offsets: &HashMap<String, u32>, be: bool) -> Vec<u8> {
    let mut out: Vec<u8> = Vec::new();
    let mut write = |name: u32, value: u32, size: u32, info: u8, other: u8, shndx: u16| {
        w32(&mut out, name, be);
        w32(&mut out, value, be);
        w32(&mut out, size, be);
        out.push(info);
        out.push(other);
        let b = if be {
            shndx.to_be_bytes()
        } else {
            shndx.to_le_bytes()
        };
        out.extend_from_slice(&b);
    };
    // The mandatory null symbol (all zeros) first, matching real linkers.
    write(0, 0, 0, 0, 0, 0);
    for s in syms {
        write(
            offsets.get(&s.name).copied().unwrap_or(0),
            s.value,
            s.size,
            (s.bind << 4) | s.ty,
            0,
            s.shndx,
        );
    }
    out
}

fn w16(out: &mut Vec<u8>, v: u16, be: bool) {
    let b = if be { v.to_be_bytes() } else { v.to_le_bytes() };
    out.extend_from_slice(&b);
}

fn w32(out: &mut Vec<u8>, v: u32, be: bool) {
    let b = if be { v.to_be_bytes() } else { v.to_le_bytes() };
    out.extend_from_slice(&b);
}

fn pad_to(out: &mut Vec<u8>, target: u32) {
    while out.len() < target as usize {
        out.push(0);
    }
}

/// Decodes a materialization blob per the layout documented in lib.rs - the Rust
/// side of the round-trip check (C# has its own independent decoder).
pub struct BlobImage {
    pub symbols: Vec<BlobSymbol>,
    pub sections: Vec<BlobSection>,
}

pub struct BlobSymbol {
    pub name: String,
    pub address: u64,
    pub size: u64,
    pub kind: u8,
    pub binding: u8,
}

pub struct BlobSection {
    pub name: String,
    pub address: u64,
    pub size: u64,
    pub file_offset: i64,
    pub has_contents: bool,
}

pub fn decode_blob(blob: &[u8]) -> BlobImage {
    let le_u32 = |off: usize| -> u32 { u32::from_le_bytes(blob[off..off + 4].try_into().unwrap()) };
    let le_u64 = |off: usize| -> u64 { u64::from_le_bytes(blob[off..off + 8].try_into().unwrap()) };
    let le_i64 = |off: usize| -> i64 { i64::from_le_bytes(blob[off..off + 8].try_into().unwrap()) };

    let sym_count = le_u32(0) as usize;
    let sect_count = le_u32(4) as usize;
    let names_len = le_u32(8) as usize;
    let sym_base = 12;
    let sect_base = sym_base + sym_count * 32;
    let names_base = sect_base + sect_count * 40;
    let name = |off: u32, len: u32| -> String {
        String::from_utf8_lossy(
            &blob[names_base + off as usize..names_base + off as usize + len as usize],
        )
        .into_owned()
    };

    let mut symbols = Vec::new();
    for i in 0..sym_count {
        let base = sym_base + i * 32;
        symbols.push(BlobSymbol {
            name: name(le_u32(base), le_u32(base + 4)),
            address: le_u64(base + 8),
            size: le_u64(base + 16),
            kind: blob[base + 24],
            binding: blob[base + 25],
        });
    }
    let mut sections = Vec::new();
    for i in 0..sect_count {
        let base = sect_base + i * 40;
        sections.push(BlobSection {
            name: name(le_u32(base), le_u32(base + 4)),
            address: le_u64(base + 8),
            size: le_u64(base + 16),
            file_offset: le_i64(base + 24),
            has_contents: blob[base + 32] != 0,
        });
    }
    // The declared pool size must account for the whole rest of the blob - a
    // trailing gap would mean the writer and this reader disagree on the layout.
    assert_eq!(names_base + names_len, blob.len());
    BlobImage { symbols, sections }
}
