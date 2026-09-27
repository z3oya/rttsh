//! rttsh-svd: the Rust home of SVD-backed peripheral knowledge.
//!
//! Loads a CMSIS-SVD file through a three-rung tolerance ladder (strict parse →
//! relaxed parse → strip dangling `derivedFrom` refs), resolves
//! `PERIPH[.REG[.FIELD]]` queries against the expanded tree, decides whether a
//! read has side effects, and renders list/info/read text. Pure library: no
//! hardware, no FFI — the future MCP tool surface sits on top of this crate
//! (docs/plans/svd-module.md).
//!
//! Scope: designed for ST-generated SVDs (peripheral `derivedFrom`,
//! `bitOffset/bitWidth` fields, `enumeratedValues`, `readAction=clear`). Features
//! ST never emits (dim arrays, clusters, `bitRange`/`msb:lsb`) parse mechanically
//! via svd-parser but carry no behavior or test guarantees here.

use std::fmt;
use std::path::PathBuf;
use std::sync::Arc;

pub mod load;
pub mod policy;
pub mod query;
pub mod render;

/// Re-exported so consumers of `Resolved`'s borrowed types name one dependency.
/// `svd-rs` is pinned exactly in Cargo.toml (see docs/plans/svd-module.md).
pub use svd_rs;

/// How the tolerance ladder had to bend to load a file (see the `load` module).
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct LoadNotes {
    /// Rung 2: the file only parses with validation disabled — e.g. ST's
    /// U575/U585 carry enum value 8 in a 3-bit field.
    pub relaxed: bool,
    /// Rung 3: names of peripherals whose `derivedFrom` pointed at a peripheral
    /// the file never defines (e.g. ST's WBAxx `SEC_ADC2`); they were cut from
    /// the tree so the rest could load.
    pub stripped: Vec<String>,
}

/// A loaded SVD file. Immutable and cheap to share: the expanded tree sits in an
/// `Arc` and every query borrows from it.
#[derive(Debug)]
pub struct Svd {
    device: Arc<svd_rs::Device>,
    notes: LoadNotes,
}

impl Svd {
    /// Loads through the tolerance ladder (see the `load` module). The single entry point.
    pub fn load(path: impl AsRef<std::path::Path>) -> Result<Svd, LoadError> {
        load::load(path.as_ref())
    }

    /// The device name. Consumed by the (out-of-scope, research D3) `svd_load` summary.
    pub fn device_name(&self) -> &str {
        &self.device.name
    }

    pub fn notes(&self) -> &LoadNotes {
        &self.notes
    }

    pub fn peripherals(&self) -> impl Iterator<Item = &svd_rs::Peripheral> + '_ {
        self.device.peripherals.iter()
    }

    /// Resolves `"GPIOA"`, `"GPIOA.CRL"` or `"GPIOA.CRL.MODE0"` against the tree.
    pub fn resolve(&self, query: &str) -> Result<Resolved<'_>, ResolveError> {
        query::resolve(self, query)
    }

    pub(crate) fn new(device: svd_rs::Device, notes: LoadNotes) -> Svd {
        Svd { device: Arc::new(device), notes }
    }

    pub(crate) fn device(&self) -> &svd_rs::Device {
        &self.device
    }
}

/// One resolved query: a peripheral, a register, or a field, borrowing the tree.
///
/// Method semantics per variant are pinned by the "Resolved 变体 × 方法语义"
/// table in docs/plans/svd-module.md — policy and rendering both read that table,
/// neither invents its own rules.
#[derive(Debug)]
pub enum Resolved<'s> {
    Peripheral { periph: &'s svd_rs::Peripheral },
    Register { periph: &'s svd_rs::Peripheral, reg: &'s svd_rs::Register },
    Field { periph: &'s svd_rs::Peripheral, reg: &'s svd_rs::Register, field: &'s svd_rs::Field },
}

impl Resolved<'_> {
    /// Peripheral: its base address. Register: base + offset. Field: its
    /// register's absolute address — reading a field reads that address.
    pub fn absolute_address(&self) -> u64 {
        match self {
            Resolved::Peripheral { periph } => periph.base_address,
            Resolved::Register { periph, reg } | Resolved::Field { periph, reg, .. } => {
                periph.base_address + u64::from(reg.address_offset)
            }
        }
    }

    /// Whether reading this unit has side effects. Peripheral: `None` (not a
    /// readable unit). Register: its own restriction, or any restricted field
    /// propagated up. Field: the field's own restriction, or its register's —
    /// reading a field reads the whole register, so `readAction=clear` fires.
    pub fn read_restriction(&self) -> Option<policy::ReadRestriction> {
        match self {
            Resolved::Peripheral { .. } => None,
            Resolved::Register { reg, .. } => policy::register_restriction(reg),
            Resolved::Field { reg, field, .. } => policy::field_restriction(reg, field)
                .or_else(|| policy::register_restriction(reg)),
        }
    }

    /// Dotted name chain (`GPIOA.CRL.MODE0`) — the prefix refusal sentences carry.
    pub fn name(&self) -> String {
        match self {
            Resolved::Peripheral { periph } => periph.name.clone(),
            Resolved::Register { periph, reg } => format!("{}.{}", periph.name, reg.name),
            Resolved::Field { periph, reg, field } => {
                format!("{}.{}.{}", periph.name, reg.name, field.name)
            }
        }
    }
}

/// Loading failed: the file could not be read, or every rung of the ladder
/// rejected it. Both variants carry the path; `Parse` carries svd-parser's own
/// locator as its source text.
#[derive(Debug)]
pub enum LoadError {
    Io { path: PathBuf, source: std::io::Error },
    Parse { path: PathBuf, source: String },
}

impl fmt::Display for LoadError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            LoadError::Io { path, source } => {
                write!(f, "failed to read SVD file `{}`: {source}", path.display())
            }
            LoadError::Parse { path, source } => {
                write!(f, "failed to parse SVD file `{}`: {source}", path.display())
            }
        }
    }
}

impl std::error::Error for LoadError {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        match self {
            LoadError::Io { source, .. } => Some(source),
            LoadError::Parse { .. } => None,
        }
    }
}

/// Nothing matches the query.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ResolveError {
    NotFound {
        query: String,
        /// Available peripheral names, capped at `query::HINT_MAX_NAMES` with an
        /// "and N more" tail (design value, not measured: AI context size vs
        /// peripheral name length).
        hint: String,
    },
}

impl fmt::Display for ResolveError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            ResolveError::NotFound { query, hint } => {
                write!(f, "no match for `{query}`; {hint}")
            }
        }
    }
}

impl std::error::Error for ResolveError {}

/// Reading failed before any value existed. `Restricted` carries only the
/// restriction — its `Display` is the side-effect half-sentence; the complete
/// refusal sentence (path prefix + override tail) is assembled by the render
/// layer, which owns the resolved name chain (see `render::render_refusal`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ReadError {
    Restricted(policy::ReadRestriction),
    /// A `Resolved::Peripheral` was handed to a read; peripherals are not
    /// readable units.
    NotReadable,
}

impl fmt::Display for ReadError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            ReadError::Restricted(restriction) => write!(f, "{restriction}"),
            ReadError::NotReadable => {
                write!(f, "peripherals are not readable units; resolve a register or field")
            }
        }
    }
}

impl std::error::Error for ReadError {}
