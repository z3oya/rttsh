//! By-name symbol lookup over a parsed image. Kind filtering runs BEFORE the
//! uniqueness check, so a FUNC and an OBJECT sharing a name do not look ambiguous
//! to an OBJECT-only query (the RTT control-block path relies on this - GCC ships
//! symbols like memmove twice, and ARM mapping symbols share names wholesale).

use crate::model::{ParsedElf, Symbol, SymbolKind};

/// Outcome of a by-name lookup, mirroring C# `SymbolLookupStatus` order. NotFound and
/// Ambiguous are ordinary results, not errors; Ambiguous carries every same-name
/// candidate (after the kind filter), ordered by address, for diagnostics.
#[repr(i32)]
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum LookupStatus {
    Found = 0,
    NotFound = 1,
    Ambiguous = 2,
}

pub struct Lookup {
    pub status: LookupStatus,
    /// The found symbol, when `status` is Found.
    pub found: Option<Symbol>,
    /// Same-name candidates when Ambiguous, ordered by address.
    pub candidates: Vec<Symbol>,
}

/// Looks a symbol up by exact name, optionally restricted to one kind.
/// An empty name is not resolvable - the C# facade guards it too, and both
/// entry points must answer identically.
pub fn lookup(image: &ParsedElf, name: &str, kind: Option<SymbolKind>) -> Lookup {
    if name.is_empty() {
        return not_found();
    }
    let matches: Vec<Symbol> = image
        .same_name(name)
        .into_iter()
        .filter(|symbol| kind.map_or(true, |wanted| symbol.kind == wanted))
        .cloned()
        .collect();
    match matches.len() {
        0 => not_found(),
        1 => Lookup {
            status: LookupStatus::Found,
            found: matches.into_iter().next(),
            candidates: Vec::new(),
        },
        _ => Lookup {
            status: LookupStatus::Ambiguous,
            found: None,
            candidates: matches,
        },
    }
}

fn not_found() -> Lookup {
    Lookup {
        status: LookupStatus::NotFound,
        found: None,
        candidates: Vec::new(),
    }
}
