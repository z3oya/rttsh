//! Text rendering: peripheral list, info tree, and decoded register reads.
//!
//! All functions are pure — no hardware; [`render_read`] decodes a value the
//! caller read. Enumerate values are masked to the field width before compare
//! (research §3.6 rule 1: ST's U575 carries enum value 8 in a 3-bit field).

use std::fmt::Write as _;

use svd_rs::{Access, Usage};

use crate::policy::{ReadPermission, ReadRestriction};
use crate::{ReadError, Resolved, Svd};

/// Description truncation for list/info output (design value: conventional
/// terminal width; not measured).
const DESC_TRUNC_COLS: usize = 80;

/// One line per peripheral: `NAME  BASE  GROUP  #REGS  description`.
pub fn render_list(svd: &Svd) -> String {
    let peripherals: Vec<&svd_rs::Peripheral> = svd.peripherals().collect();
    let name_width = peripherals.iter().map(|p| p.name.len()).max().unwrap_or(0);
    let mut out = String::new();
    for p in &peripherals {
        let _ = writeln!(
            out,
            "{:<name_width$}  0x{:08X}  {:<10}  {:>4}  {}",
            p.name,
            p.base_address,
            p.group_name.as_deref().unwrap_or("-"),
            p.all_registers().count(),
            one_line(p.description.as_deref().unwrap_or("")),
        );
    }
    out
}

/// Peripheral → register tree; register → field tree with enum tables; field →
/// detail with enum table. Zero hardware, no failure path.
pub fn render_info(resolved: &Resolved) -> String {
    match resolved {
        Resolved::Peripheral { periph } => peripheral_info(periph),
        Resolved::Register { periph, reg } => register_info(periph, reg),
        Resolved::Field { periph, reg, field } => field_info(periph, reg, field),
    }
}

/// Decodes `raw` (the value at the register's address) into field lines.
/// Restricted reads are refused unless `IgnoreSideEffects`; the refusal
/// sentence comes from [`render_refusal`].
pub fn render_read(
    resolved: &Resolved,
    raw: u64,
    permission: ReadPermission,
) -> Result<String, ReadError> {
    let reg = match resolved {
        Resolved::Peripheral { .. } => return Err(ReadError::NotReadable),
        Resolved::Register { reg, .. } | Resolved::Field { reg, .. } => reg,
    };
    if let Some(restriction) = resolved.read_restriction() {
        if permission == ReadPermission::CheckSideEffects {
            return Err(ReadError::Restricted(restriction));
        }
    }

    let size = reg.properties.size.unwrap_or(32).clamp(1, 64) as usize;
    let hex_width = size.div_ceil(8) * 2;
    let raw = raw & value_mask(size);
    let mut out = String::new();
    let _ = write!(
        out,
        "{} = 0x{raw:0hex_width$x} (0b{raw:0size$b})",
        register_name(resolved),
    );
    if let Some(reset) = reg.properties.reset_value {
        if raw != reset {
            let _ = write!(out, " [≠RESET: reset 0x{reset:0hex_width$x}]");
        }
    }
    let _ = writeln!(out);

    let fields: Vec<&svd_rs::Field> = reg.fields().collect();
    let name_width = fields.iter().map(|f| f.name.len()).max().unwrap_or(0);
    let only = match resolved {
        Resolved::Field { field, .. } => Some(field.name.as_str()),
        _ => None,
    };
    for field in &fields {
        if let Some(want) = only {
            if field.name != want {
                continue;
            }
        }
        let offset = u64::from(field.bit_offset());
        let width = field.bit_width();
        let shifted = if offset >= 64 { 0 } else { raw >> offset };
        let value = shifted & value_mask(width as usize);
        let (msb, lsb) = bit_span(field);
        let _ = write!(out, "  {:<name_width$}[{}:{}] = 0x{value:x}", field.name, msb, lsb);
        if let Some((name, description)) = enum_match(field, value) {
            let _ = write!(out, " {name}");
            if let Some(description) = description {
                let _ = write!(out, " : {}", one_line(description));
            }
        }
        let _ = writeln!(out);
    }
    Ok(out)
}

/// The complete refusal sentence for a restricted read. `ReadRestriction`'s
/// `Display` is only the side-effect half-sentence; this function owns the path
/// prefix (the resolved name chain) and the override tail.
pub fn render_refusal(resolved: &Resolved, restriction: &ReadRestriction) -> String {
    format!("{} {restriction}; pass IgnoreSideEffects to override", resolved.name())
}

fn peripheral_info(periph: &svd_rs::Peripheral) -> String {
    let registers: Vec<&svd_rs::Register> = periph.all_registers().collect();
    let name_width = registers.iter().map(|r| r.name.len()).max().unwrap_or(0);
    let mut out = String::new();
    let _ = writeln!(
        out,
        "{} @ 0x{:08X} ({}): {}",
        periph.name,
        periph.base_address,
        periph.group_name.as_deref().unwrap_or("-"),
        one_line(periph.description.as_deref().unwrap_or("")),
    );
    for reg in &registers {
        let _ = writeln!(
            out,
            "  0x{:08X}  {:<name_width$}  {}b  {:<15}  {}  {}",
            periph.base_address + u64::from(reg.address_offset),
            reg.name,
            reg.properties.size.unwrap_or(32),
            access_str(reg.properties.access),
            reset_str(reg.properties.reset_value, reg.properties.size),
            one_line(reg.description.as_deref().unwrap_or("")),
        );
    }
    out
}

fn register_info(periph: &svd_rs::Peripheral, reg: &svd_rs::Register) -> String {
    let fields: Vec<&svd_rs::Field> = reg.fields().collect();
    let name_width = fields.iter().map(|f| f.name.len()).max().unwrap_or(0);
    let mut out = String::new();
    let _ = writeln!(
        out,
        "{}.{} @ 0x{:08X}, {} bits, {}, {}: {}",
        periph.name,
        reg.name,
        periph.base_address + u64::from(reg.address_offset),
        reg.properties.size.unwrap_or(32),
        access_str(reg.properties.access),
        reset_str(reg.properties.reset_value, reg.properties.size),
        one_line(reg.description.as_deref().unwrap_or("")),
    );
    for field in &fields {
        let (msb, lsb) = bit_span(field);
        let _ = writeln!(
            out,
            "  {:<name_width$}[{}:{}]  {}  {}",
            field.name,
            msb,
            lsb,
            access_str(field.access),
            one_line(field.description.as_deref().unwrap_or("")),
        );
        render_enum_table(&mut out, field, "    ");
    }
    out
}

fn field_info(periph: &svd_rs::Peripheral, reg: &svd_rs::Register, field: &svd_rs::Field) -> String {
    let mut out = String::new();
    let (msb, lsb) = bit_span(field);
    let _ = writeln!(
        out,
        "{}.{}.{}[{}:{}] @ 0x{:08X} (field of {}.{}, {} bits, {}): {}",
        periph.name,
        reg.name,
        field.name,
        msb,
        lsb,
        periph.base_address + u64::from(reg.address_offset),
        periph.name,
        reg.name,
        reg.properties.size.unwrap_or(32),
        access_str(field.access),
        one_line(field.description.as_deref().unwrap_or("")),
    );
    render_enum_table(&mut out, field, "  ");
    out
}

fn render_enum_table(out: &mut String, field: &svd_rs::Field, indent: &str) {
    let Some(set) = pick_enum_set(&field.enumerated_values) else {
        return;
    };
    for value in &set.values {
        let Some(raw) = value.value else { continue };
        let _ = match value.description.as_deref() {
            Some(description) => writeln!(
                out,
                "{indent}0x{raw:x} = {} : {}",
                value.name,
                one_line(description)
            ),
            None => writeln!(out, "{indent}0x{raw:x} = {}", value.name),
        };
    }
}

/// Fields can carry several `enumeratedValues` sets; the read set wins (research
/// D6). Fallback order — read-write, then the unspecified (applies to both), then
/// write — is this crate's completion of that decision; with none, no table.
fn pick_enum_set(sets: &[svd_rs::EnumeratedValues]) -> Option<&svd_rs::EnumeratedValues> {
    sets.iter()
        .find(|s| s.usage == Some(Usage::Read))
        .or_else(|| sets.iter().find(|s| s.usage == Some(Usage::ReadWrite)))
        .or_else(|| sets.iter().find(|s| s.usage.is_none()))
        .or_else(|| sets.iter().find(|s| s.usage == Some(Usage::Write)))
}

/// First enum whose value, masked to the field width, equals the field value —
/// out-of-range enum values (ST's U575) can never crash or shadow outside their
/// masked slot.
fn enum_match(field: &svd_rs::Field, value: u64) -> Option<(&str, Option<&str>)> {
    let set = pick_enum_set(&field.enumerated_values)?;
    let mask = value_mask(field.bit_width() as usize);
    set.values.iter().find_map(|v| {
        let raw = v.value?;
        if raw & mask == value {
            Some((v.name.as_str(), v.description.as_deref()))
        } else {
            None
        }
    })
}

fn value_mask(width: usize) -> u64 {
    if width >= 64 {
        u64::MAX
    } else {
        (1u64 << width) - 1
    }
}

/// (msb, lsb) label for a field. Zero-width fields only load through the
/// relaxed rung of the ladder (Weak validation rejects them); label them
/// `[offset:offset]` instead of underflowing — a zero-width field holds no
/// bits, so its decoded value is always 0.
fn bit_span(field: &svd_rs::Field) -> (u32, u32) {
    let lsb = field.bit_offset();
    (lsb + field.bit_width().saturating_sub(1), lsb)
}

fn access_str(access: Option<Access>) -> &'static str {
    match access {
        None => "-",
        Some(Access::ReadOnly) => "read-only",
        Some(Access::ReadWrite) => "read-write",
        Some(Access::WriteOnly) => "write-only",
        Some(Access::WriteOnce) => "write-once",
        Some(Access::ReadWriteOnce) => "read-write-once",
    }
}

fn reset_str(reset: Option<u64>, size: Option<u32>) -> String {
    match reset {
        None => "reset -".to_string(),
        Some(reset) => {
            let hex_width = (size.unwrap_or(32) as usize).div_ceil(8) * 2;
            format!("reset 0x{reset:0hex_width$x}")
        }
    }
}

fn register_name(resolved: &Resolved) -> String {
    match resolved {
        Resolved::Peripheral { periph } => periph.name.clone(),
        Resolved::Register { periph, reg } | Resolved::Field { periph, reg, .. } => {
            format!("{}.{}", periph.name, reg.name)
        }
    }
}

/// Flattens whitespace and truncates to `DESC_TRUNC_COLS` characters.
fn one_line(text: &str) -> String {
    text.split_whitespace().collect::<Vec<_>>().join(" ").chars().take(DESC_TRUNC_COLS).collect()
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
              <description>GPIO port A
                   with a second line</description>
              <groupName>GPIO</groupName>
              <baseAddress>0x40010800</baseAddress>
              <registers>
                <register>
                  <name>CRL</name>
                  <description>Port configuration register low</description>
                  <addressOffset>0x0</addressOffset>
                  <resetValue>0x44444444</resetValue>
                  <fields>
                    <field>
                      <name>MODE0</name>
                      <description>Port mode</description>
                      <bitOffset>0</bitOffset>
                      <bitWidth>2</bitWidth>
                      <enumeratedValues>
                        <enumeratedValue><name>INPUT</name><description>Input mode</description><value>0</value></enumeratedValue>
                        <enumeratedValue><name>OUTPUT</name><description>General purpose output</description><value>1</value></enumeratedValue>
                      </enumeratedValues>
                    </field>
                    <field><name>MODE1</name><bitOffset>4</bitOffset><bitWidth>2</bitWidth></field>
                  </fields>
                </register>
                <register><name>IDR</name><addressOffset>0x8</addressOffset>
                  <fields><field><name>ID0</name><bitOffset>0</bitOffset><bitWidth>1</bitWidth></field></fields>
                </register>
              </registers>
            </peripheral>
            <peripheral><name>WOB</name><baseAddress>0x40011000</baseAddress>
              <registers><register><name>TX</name><addressOffset>0x0</addressOffset><access>write-only</access></register></registers>
            </peripheral>
          </peripherals>
        </device>"#;

    fn svd() -> Svd {
        parse_for_test(XML).expect("parse")
    }

    #[test]
    fn list_truncates_descriptions_to_one_line() {
        let out = render_list(&svd());
        // One line per peripheral, descriptions flattened.
        assert_eq!(out.lines().count(), 2, "{out}");
        assert!(out.starts_with("GPIOA  0x40010800"), "{out}");
        assert!(out.contains("GPIO port A with a second line"), "{out}");
        assert!(out.contains("WOB"), "{out}");
    }

    #[test]
    fn info_renders_the_three_levels() {
        let svd = svd();
        let periph = svd.resolve("GPIOA").unwrap();
        let periph_info = render_info(&periph);
        assert!(periph_info.starts_with("GPIOA @ 0x40010800 (GPIO):"), "{periph_info}");
        assert!(periph_info.contains("0x40010800  CRL  32b  read-write"), "{periph_info}");
        assert!(periph_info.contains("reset 0x44444444"), "{periph_info}");
        assert!(periph_info.contains("0x40010808  IDR"), "{periph_info}");

        let reg = svd.resolve("GPIOA.CRL").unwrap();
        let reg_info = render_info(&reg);
        assert!(reg_info.starts_with("GPIOA.CRL @ 0x40010800, 32 bits, read-write, reset 0x44444444:"), "{reg_info}");
        assert!(reg_info.contains("MODE0[1:0]"), "{reg_info}");
        assert!(reg_info.contains("MODE1[5:4]"), "{reg_info}");
        assert!(reg_info.contains("0x0 = INPUT : Input mode"), "{reg_info}");

        let field = svd.resolve("GPIOA.CRL.MODE0").unwrap();
        let field_info = render_info(&field);
        assert!(field_info.starts_with("GPIOA.CRL.MODE0[1:0] @ 0x40010800 (field of GPIOA.CRL, 32 bits, -):"), "{field_info}");
        assert!(field_info.contains("0x1 = OUTPUT : General purpose output"), "{field_info}");
    }

    #[test]
    fn read_marks_reset_mismatch_only_when_it_differs() {
        let svd = svd();
        let reg = svd.resolve("GPIOA.CRL").unwrap();
        let same = render_read(&reg, 0x44444444, ReadPermission::CheckSideEffects).unwrap();
        assert!(same.starts_with("GPIOA.CRL = 0x44444444 (0b"), "{same}");
        assert!(!same.contains("≠RESET"), "{same}");
        let diff = render_read(&reg, 0x44444445, ReadPermission::CheckSideEffects).unwrap();
        assert!(diff.contains("[≠RESET: reset 0x44444444]"), "{diff}");
    }

    #[test]
    fn read_decodes_fields_with_enums_and_shifts() {
        let svd = svd();
        let reg = svd.resolve("GPIOA.CRL").unwrap();
        let out = render_read(&reg, 0x10, ReadPermission::CheckSideEffects).unwrap();
        // MODE0 = 0 (INPUT), MODE1 = 0x1 (no enum set).
        assert!(out.contains("MODE0[1:0] = 0x0 INPUT : Input mode"), "{out}");
        assert!(out.contains("MODE1[5:4] = 0x1"), "{out}");
        let idr = svd.resolve("GPIOA.IDR").unwrap();
        let out = render_read(&idr, 0x5, ReadPermission::CheckSideEffects).unwrap();
        assert!(out.contains("ID0[0:0] = 0x1"), "{out}");
    }

    #[test]
    fn field_read_renders_only_that_field_but_the_whole_register_value() {
        let svd = svd();
        let field = svd.resolve("GPIOA.CRL.MODE0").unwrap();
        let out = render_read(&field, 0x10, ReadPermission::CheckSideEffects).unwrap();
        assert!(out.starts_with("GPIOA.CRL = 0x00000010 "), "{out}");
        assert!(out.contains("MODE0[1:0] = 0x0"), "{out}");
        assert!(!out.contains("MODE1"), "{out}");
    }

    #[test]
    fn restricted_reads_are_refused_and_ignore_side_effects_renders() {
        let svd = parse_for_test(
            r#"<device schemaVersion="1.3"><name>T</name><peripherals>
               <peripheral><name>STAT</name><baseAddress>0x40000000</baseAddress>
               <registers><register><name>STATUS</name><addressOffset>0x0</addressOffset>
               <readAction>clear</readAction></register></registers></peripheral>
               </peripherals></device>"#,
        )
        .unwrap();
        let reg = svd.resolve("STAT.STATUS").unwrap();
        let err = render_read(&reg, 0x1, ReadPermission::CheckSideEffects).unwrap_err();
        match &err {
            ReadError::Restricted(restriction) => {
                assert_eq!(restriction.to_string(), "cannot be read without side effects (readAction=clear)");
                assert_eq!(
                    render_refusal(&reg, restriction),
                    "STAT.STATUS cannot be read without side effects (readAction=clear); pass IgnoreSideEffects to override"
                );
            }
            other => panic!("expected Restricted, got {other:?}"),
        }
        let out = render_read(&reg, 0x1, ReadPermission::IgnoreSideEffects).unwrap();
        assert!(out.starts_with("STAT.STATUS = 0x00000001 "), "{out}");
    }

    #[test]
    fn reading_a_peripheral_is_not_readable_even_with_ignore_side_effects() {
        let svd = svd();
        let periph = svd.resolve("GPIOA").unwrap();
        let err = render_read(&periph, 0, ReadPermission::IgnoreSideEffects).unwrap_err();
        assert_eq!(err, ReadError::NotReadable);
    }

    #[test]
    fn zero_width_fields_from_the_relaxed_rung_do_not_panic() {
        // bitWidth=0 fails Weak validation but loads through the relaxed rung;
        // rendering must stay total (no u32 underflow in the msb label).
        let svd = parse_for_test(
            r#"<device schemaVersion="1.3"><name>T</name><peripherals>
               <peripheral><name>P</name><baseAddress>0x0</baseAddress>
               <registers><register><name>R</name><addressOffset>0x0</addressOffset>
               <fields><field><name>Z</name><bitOffset>3</bitOffset><bitWidth>0</bitWidth></field></fields>
               </register></registers></peripheral></peripherals></device>"#,
        )
        .expect("relaxed rung loads");
        assert!(svd.notes().relaxed);
        let field = svd.resolve("P.R.Z").unwrap();
        let out = render_read(&field, 0xFF, ReadPermission::CheckSideEffects).unwrap();
        assert!(out.contains("Z[3:3] = 0x0"), "{out}");
        let reg = svd.resolve("P.R").unwrap();
        let info = render_info(&reg);
        assert!(info.contains("Z[3:3]"), "{info}");
    }

    #[test]
    fn out_of_range_enum_values_cannot_shadow_beyond_their_masked_slot() {
        // U575 shape: enum value 8 in a 3-bit field. Masked to 0 it can only ever
        // match a field value of 0 — and the value-0 entry wins by document order.
        let svd = parse_for_test(
            r#"<device schemaVersion="1.3"><name>T</name><peripherals>
               <peripheral><name>TIM</name><baseAddress>0x40002000</baseAddress>
               <registers><register><name>SMCR</name><addressOffset>0x0</addressOffset>
               <fields><field><name>SMS1</name><bitOffset>0</bitOffset><bitWidth>3</bitWidth>
               <enumeratedValues>
                 <enumeratedValue><name>ZERO</name><value>0</value></enumeratedValue>
                 <enumeratedValue><name>ONE</name><value>1</value></enumeratedValue>
                 <enumeratedValue><name>EIGHT</name><value>8</value></enumeratedValue>
               </enumeratedValues></field></fields></register></registers></peripheral>
               </peripherals></device>"#,
        )
        .unwrap();
        let field = svd.resolve("TIM.SMCR.SMS1").unwrap();
        for raw in 0..=7u64 {
            render_read(&field, raw, ReadPermission::CheckSideEffects)
                .unwrap_or_else(|e| panic!("raw {raw}: {e}"));
        }
        let zero = render_read(&field, 0, ReadPermission::CheckSideEffects).unwrap();
        assert!(zero.contains("ZERO"), "{zero}");
        let one = render_read(&field, 1, ReadPermission::CheckSideEffects).unwrap();
        assert!(one.contains("ONE"), "{one}");
        let two = render_read(&field, 2, ReadPermission::CheckSideEffects).unwrap();
        assert!(two.contains("= 0x2") && !two.contains("EIGHT"), "{two}");
    }
}
