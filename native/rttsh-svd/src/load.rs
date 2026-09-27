//! The loading tolerance ladder.
//!
//! 1. Strict parse (`ValidateLevel::Weak`) — the normal path; 264/267 of the ST
//!    corpus loads here.
//! 2. Relaxed parse (`ValidateLevel::Disabled`) — tolerates ST's own file bugs
//!    such as U575/U585's 3-bit field carrying enum value 8 (`relaxed = true`).
//! 3. Dangling-`derivedFrom` repair — the expand pass fails hard on a reference
//!    to a peripheral the file never defines (WBAxx: `SEC_ADC2 -> ADC2`), and no
//!    validation level bypasses it. A no-expand pre-parse names the dangling
//!    targets, the referencing peripheral blocks are cut out of the XML text, and
//!    rungs 1-2 re-run on the repaired text (`stripped` lists what was cut).
//! 4. Everything failed: svd-parser's own locator travels in [`LoadError::Parse`].

use std::collections::HashSet;
use std::fs;
use std::path::Path;

use svd_parser::{Config, ValidateLevel};

use crate::{LoadError, LoadNotes, Svd};

pub(crate) fn load(path: &Path) -> Result<Svd, LoadError> {
    let xml = fs::read_to_string(path)
        .map_err(|source| LoadError::Io { path: path.to_path_buf(), source })?;

    match load_text(&xml) {
        Ok((device, notes)) => Ok(Svd::new(device, notes)),
        Err(first_pass) => {
            // Rung 3: dangling-derivedFrom repair (see the module docs), then a
            // fresh pass over the repaired text. Stripping one block can expose
            // a second-order dangling reference (A→GHOST, B→A), so strip to a
            // fixpoint, bounded against pathological chains.
            let mut repaired = xml.to_string();
            let mut stripped_all: Vec<String> = Vec::new();
            let Ok(mut pre) = parse_unexpanded(&xml) else {
                return Err(parse_error(path, first_pass));
            };
            for _ in 0..REPAIR_ROUND_CAP {
                let targets = dangling_targets(&pre);
                if targets.is_empty() {
                    break;
                }
                let (next, stripped) = strip_dangling(&repaired, &targets);
                if stripped.is_empty() {
                    break;
                }
                repaired = next;
                stripped_all.extend(stripped);
                let Ok(next_pre) = parse_unexpanded(&repaired) else {
                    return Err(parse_error(path, first_pass));
                };
                pre = next_pre;
            }
            if stripped_all.is_empty() {
                return Err(parse_error(path, first_pass));
            }
            match load_text(&repaired) {
                Ok((device, mut notes)) => {
                    notes.stripped = stripped_all;
                    Ok(Svd::new(device, notes))
                }
                Err(_) => Err(parse_error(path, first_pass)),
            }
        }
    }
}

/// Upper bound for the dangling-repair fixpoint. Each round removes at least one
/// peripheral, so real chains (depth 1-2 in the wild) terminate in a round or
/// two; the cap keeps a pathological file from looping and fails loud instead.
const REPAIR_ROUND_CAP: usize = 8;

/// Rungs 1-2 over one XML text. When the relaxed rung fails too, the strict
/// rung's error is reported: it disables nothing but validation, so it names
/// the same defect with more context.
fn load_text(xml: &str) -> Result<(svd_rs::Device, LoadNotes), String> {
    match parse_expanded(xml, ValidateLevel::Weak) {
        Ok(device) => Ok((device, LoadNotes::default())),
        Err(strict) => parse_expanded(xml, ValidateLevel::Disabled)
            .map(|device| (device, LoadNotes { relaxed: true, stripped: Vec::new() }))
            .map_err(|_| strict),
    }
}

fn parse_error(path: &Path, source: String) -> LoadError {
    LoadError::Parse { path: path.to_path_buf(), source }
}

fn parse_expanded(xml: &str, level: ValidateLevel) -> Result<svd_rs::Device, String> {
    svd_parser::parse_with_config(
        xml,
        &Config::default()
            .expand(true)
            .expand_properties(true)
            .validate_level(level),
    )
    .map_err(|e| format!("{e:#}"))
}

/// A no-expand parse keeps dangling `derivedFrom` strings unresolved instead of
/// failing, which is what makes the dangling pre-scan possible.
fn parse_unexpanded(xml: &str) -> Result<svd_rs::Device, String> {
    svd_parser::parse_with_config(
        xml,
        &Config::default().expand(false).validate_level(ValidateLevel::Disabled),
    )
    .map_err(|e| format!("{e:#}"))
}

/// Targets of `derivedFrom` references that no peripheral in the pre-parse satisfies.
fn dangling_targets(pre: &svd_rs::Device) -> Vec<String> {
    let names: HashSet<&str> = pre.peripherals.iter().map(|p| p.name.as_str()).collect();
    let mut out: Vec<String> = Vec::new();
    for peripheral in &pre.peripherals {
        if let Some(target) = &peripheral.derived_from {
            if !names.contains(target.as_str()) && !out.contains(target) {
                out.push(target.clone());
            }
        }
    }
    out
}

/// Cuts every `<peripheral derivedFrom="target"…</peripheral>` block for the given
/// dangling targets. Peripherals never nest, so "from the open tag to the next
/// `</peripheral>`" is a safe cut. Returns the repaired XML and the names of the
/// stripped peripherals, in document order.
fn strip_dangling(xml: &str, targets: &[String]) -> (String, Vec<String>) {
    let mut repaired = xml.to_string();
    let mut stripped: Vec<String> = Vec::new();
    for target in targets {
        while let Some(start) = repaired.find(&format!(r#"<peripheral derivedFrom="{target}""#)) {
            let Some(end_rel) = repaired[start..].find("</peripheral>") else {
                break;
            };
            let end = start + end_rel + "</peripheral>".len();
            let block = &repaired[start..end];
            let name = block
                .split("<name>")
                .nth(1)
                .and_then(|rest| rest.split("</name>").next())
                .unwrap_or(target)
                .trim()
                .to_string();
            repaired.replace_range(start..end, "");
            stripped.push(name);
        }
    }
    (repaired, stripped)
}

/// Parses an inline XML string through rungs 1-2 of the ladder — the shared
/// substrate for the in-module tests (query, policy, render).
#[cfg(test)]
pub(crate) fn parse_for_test(xml: &str) -> Result<Svd, String> {
    match parse_expanded(xml, ValidateLevel::Weak) {
        Ok(device) => Ok(Svd::new(device, LoadNotes::default())),
        Err(weak_error) => match parse_expanded(xml, ValidateLevel::Disabled) {
            Ok(device) => Ok(Svd::new(
                device,
                LoadNotes { relaxed: true, stripped: Vec::new() },
            )),
            Err(_) => Err(weak_error),
        },
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::Resolved;

    /// One base peripheral, one legitimate derived one, one dangling reference —
    /// the WBAxx shape in miniature.
    const XML: &str = r#"
        <device schemaVersion="1.3">
          <name>T</name>
          <peripherals>
            <peripheral>
              <name>GPIOA</name>
              <baseAddress>0x40010800</baseAddress>
              <registers>
                <register><name>CRL</name><addressOffset>0x0</addressOffset></register>
              </registers>
            </peripheral>
            <peripheral derivedFrom="GPIOA">
              <name>GPIOB</name>
              <baseAddress>0x40010C00</baseAddress>
            </peripheral>
            <peripheral derivedFrom="GHOST">
              <name>SEC_X</name>
              <baseAddress>0x50000000</baseAddress>
            </peripheral>
          </peripherals>
        </device>"#;

    #[test]
    fn dangling_targets_reports_only_unresolved_references() {
        let pre = parse_unexpanded(XML).expect("no-expand parse");
        assert_eq!(dangling_targets(&pre), vec!["GHOST".to_string()]);
    }

    #[test]
    fn strip_dangling_cuts_only_the_dangling_blocks() {
        let pre = parse_unexpanded(XML).expect("no-expand parse");
        let (repaired, stripped) = strip_dangling(XML, &dangling_targets(&pre));
        assert_eq!(stripped, vec!["SEC_X".to_string()]);
        assert!(!repaired.contains("SEC_X"));
        assert!(repaired.contains(r#"derivedFrom="GPIOA""#));
        assert!(repaired.contains("GPIOB"));
    }

    #[test]
    fn ladder_repairs_a_dangling_reference() {
        let svd = Svd::load("tests/fixtures/dangling_derived.svd").expect("ladder loads");
        assert_eq!(svd.notes().stripped, vec!["SEC_X".to_string()]);
        assert!(!svd.notes().relaxed);
        assert!(matches!(svd.resolve("GPIOA"), Ok(Resolved::Peripheral { .. })));
        assert!(svd.resolve("SEC_X").is_err());
    }

    #[test]
    fn ladder_strips_second_order_dangling_chains_to_a_fixpoint() {
        // A is the base; B references the missing GHOST; C references B. Once B
        // is stripped, C dangles too — the fixpoint needs a second round.
        const CHAIN: &str = r#"
            <device schemaVersion="1.3">
              <name>T</name>
              <peripherals>
                <peripheral>
                  <name>A</name>
                  <baseAddress>0x0</baseAddress>
                  <registers><register><name>R</name><addressOffset>0x0</addressOffset></register></registers>
                </peripheral>
                <peripheral derivedFrom="GHOST">
                  <name>B</name>
                  <baseAddress>0x1000</baseAddress>
                </peripheral>
                <peripheral derivedFrom="B">
                  <name>C</name>
                  <baseAddress>0x2000</baseAddress>
                </peripheral>
              </peripherals>
            </device>"#;
        let path =
            std::env::temp_dir().join(format!("rttsh-svd-chain-{}.svd", std::process::id()));
        fs::write(&path, CHAIN).expect("write temp file");
        let svd = Svd::load(&path).expect("ladder loads");
        fs::remove_file(&path).ok();
        assert_eq!(svd.notes().stripped, vec!["B".to_string(), "C".to_string()]);
        assert!(!svd.notes().relaxed);
        assert!(svd.resolve("A").is_ok());
        assert!(svd.resolve("B").is_err());
        assert!(svd.resolve("C").is_err());
    }

    #[test]
    fn io_error_carries_the_path_and_parse_error_carries_the_locator() {
        let err = Svd::load("tests/fixtures/no-such-file.svd").unwrap_err();
        assert!(err.to_string().contains("tests/fixtures/no-such-file.svd"), "{err}");
        assert!(matches!(err, LoadError::Io { .. }));

        let broken = std::env::temp_dir().join(format!("rttsh-svd-broken-{}.svd", std::process::id()));
        fs::write(&broken, "<device><name>broken</name>").expect("write temp file");
        let err = Svd::load(&broken).unwrap_err();
        fs::remove_file(&broken).ok();
        assert!(matches!(err, LoadError::Parse { .. }), "{err}");
        // svd-parser's own locator (an XML position or an "Expected …" message).
        assert!(err.to_string().contains("failed to parse SVD file"), "{err}");
    }
}
