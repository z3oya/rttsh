//! `PERIPH[.REG[.FIELD]]` resolution against the expanded tree.
//!
//! Segments are trimmed and matched case-insensitively and exactly (same
//! convention as load-elf-to-ram's `--chip`). Whitespace-only segments and
//! queries with more than three segments are [`crate::ResolveError::NotFound`],
//! as is any name miss; the hint lists the available peripheral names. Linear
//! scans only — the worst tree is ~17k registers, microseconds (research §3.5;
//! no index by design).

use crate::{ResolveError, Resolved, Svd};

/// Hint cap (design value, not measured: AI context size vs peripheral name length).
const HINT_MAX_NAMES: usize = 20;

pub(crate) fn resolve<'s>(svd: &'s Svd, query: &str) -> Result<Resolved<'s>, ResolveError> {
    let segments: Vec<&str> = query.split('.').collect();
    if segments.len() > 3 || segments.iter().any(|s| s.trim().is_empty()) {
        return Err(not_found(svd, query));
    }
    let want = |name: &str| name.to_ascii_lowercase();
    let segment = |i: usize| segments[i].trim();

    let Some(periph) = svd
        .device()
        .peripherals
        .iter()
        .find(|p| want(&p.name) == want(segment(0)))
    else {
        return Err(not_found(svd, query));
    };

    match segments.len() {
        1 => Ok(Resolved::Peripheral { periph }),
        2 => periph
            .all_registers()
            .find(|r| want(&r.name) == want(segment(1)))
            .map(|reg| Resolved::Register { periph, reg })
            .ok_or_else(|| not_found(svd, query)),
        _ => {
            let Some(reg) = periph
                .all_registers()
                .find(|r| want(&r.name) == want(segment(1)))
            else {
                return Err(not_found(svd, query));
            };
            reg.fields()
                .find(|f| want(&f.name) == want(segment(2)))
                .map(|field| Resolved::Field { periph, reg, field })
                .ok_or_else(|| not_found(svd, query))
        }
    }
}

fn not_found(svd: &Svd, query: &str) -> ResolveError {
    let names: Vec<&str> = svd.device().peripherals.iter().map(|p| p.name.as_str()).collect();
    let hint = if names.is_empty() {
        "the file defines no peripherals".to_string()
    } else {
        let shown = names.len().min(HINT_MAX_NAMES);
        let mut hint = format!("available peripherals: {}", names[..shown].join(", "));
        if names.len() > shown {
            hint.push_str(&format!(" and {} more", names.len() - shown));
        }
        hint
    };
    ResolveError::NotFound { query: query.to_string(), hint }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::load::parse_for_test;

    const XML: &str = r#"
        <device schemaVersion="1.3">
          <name>T</name>
          <size>32</size>
          <access>read-write</access>
          <peripherals>
            <peripheral>
              <name>GPIOA</name>
              <baseAddress>0x40010800</baseAddress>
              <registers>
                <register>
                  <name>CRL</name>
                  <addressOffset>0x0</addressOffset>
                  <fields>
                    <field><name>MODE0</name><bitOffset>0</bitOffset><bitWidth>2</bitWidth></field>
                  </fields>
                </register>
                <register><name>IDR</name><addressOffset>0x8</addressOffset></register>
              </registers>
            </peripheral>
            <peripheral>
              <name>GPIOB</name>
              <baseAddress>0x40010C00</baseAddress>
              <registers>
                <register><name>CRL</name><addressOffset>0x0</addressOffset></register>
              </registers>
            </peripheral>
          </peripherals>
        </device>"#;

    fn svd() -> Svd {
        parse_for_test(XML).expect("parse")
    }

    #[test]
    fn resolve_hits_peripheral_register_and_field_case_insensitively() {
        let svd = svd();
        assert!(matches!(svd.resolve("gpioa").unwrap(), Resolved::Peripheral { .. }));
        assert!(matches!(svd.resolve("GPIOA.crl").unwrap(), Resolved::Register { .. }));
        assert!(matches!(svd.resolve("GPIOA.CRL.mode0").unwrap(), Resolved::Field { .. }));
    }

    #[test]
    fn segment_whitespace_is_trimmed() {
        let svd = svd();
        assert!(matches!(svd.resolve(" GPIOA . crl ").unwrap(), Resolved::Register { .. }));
        // A whitespace-only segment is still NotFound, though.
        assert!(svd.resolve("GPIOA. .CRL").is_err());
    }

    #[test]
    fn resolved_addresses_follow_the_semantics_table() {
        let svd = svd();
        let periph = svd.resolve("GPIOA").unwrap();
        assert_eq!(periph.absolute_address(), 0x40010800);
        let reg = svd.resolve("GPIOA.IDR").unwrap();
        assert_eq!(reg.absolute_address(), 0x40010808);
        let field = svd.resolve("GPIOA.CRL.MODE0").unwrap();
        // A field reports its register's address: reading the field reads that address.
        assert_eq!(field.absolute_address(), 0x40010800);
    }

    #[test]
    fn empty_segments_and_over_three_segments_are_not_found() {
        let svd = svd();
        assert!(svd.resolve("GPIOA..CRL").is_err());
        assert!(svd.resolve(" ").is_err());
        assert!(svd.resolve("A.B.C.D").is_err());
    }

    #[test]
    fn name_misses_at_every_level_are_not_found() {
        let svd = svd();
        assert!(svd.resolve("NOPE").is_err());
        assert!(svd.resolve("GPIOA.NOPE").is_err());
        assert!(svd.resolve("GPIOA.CRL.NOPE").is_err());
    }

    #[test]
    fn miss_hint_lists_peripherals_and_caps_at_twenty() {
        let svd = svd();
        match svd.resolve("NOPE").unwrap_err() {
            ResolveError::NotFound { query, hint } => {
                assert_eq!(query, "NOPE");
                assert!(hint.contains("GPIOA") && hint.contains("GPIOB"), "{hint}");
            }
        }

        let mut many = String::from(r#"<device schemaVersion="1.3"><name>M</name><peripherals>"#);
        for i in 0..25 {
            many.push_str(&format!(
                r#"<peripheral><name>P{i:02}</name><baseAddress>0x{i:08X}</baseAddress></peripheral>"#
            ));
        }
        many.push_str("</peripherals></device>");
        let svd = parse_for_test(&many).expect("parse many");
        match svd.resolve("NOPE").unwrap_err() {
            ResolveError::NotFound { hint, .. } => {
                assert!(hint.contains("P19"), "{hint}");
                assert!(!hint.contains("P24"), "{hint}");
                assert!(hint.contains("and 5 more"), "{hint}");
            }
        }
    }
}
