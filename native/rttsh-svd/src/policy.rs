//! Restricted-read policy: does reading this register/field have side effects?
//!
//! Rule (probe-rs semantics, research §2.2/D5): `read_action.is_some()` or
//! `!access.can_read()` means restricted; `access == None` after property
//! expansion means readable. A restricted field propagates up to its register —
//! reading the register reads the field. Pure functions over the parsed tree;
//! no hardware.

use std::fmt;

use svd_rs::{Access, Field, ReadAction, Register};

/// Why a read must be refused. `Display` renders only the side-effect
/// half-sentence; the path prefix and the `; pass IgnoreSideEffects to override`
/// tail belong to the render layer, which owns the resolved name chain.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ReadRestriction {
    /// The register/field is cleared/set/… on read (ST corpus: always `clear`).
    ReadAction(ReadAction),
    /// Writes are visible, reads are not — the value is not observable.
    WriteOnly,
}

impl fmt::Display for ReadRestriction {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            ReadRestriction::ReadAction(action) => write!(
                f,
                "cannot be read without side effects (readAction={})",
                action_str(*action)
            ),
            ReadRestriction::WriteOnly => {
                write!(f, "cannot be read without side effects (write-only)")
            }
        }
    }
}

fn action_str(action: ReadAction) -> &'static str {
    match action {
        ReadAction::Clear => "clear",
        ReadAction::Set => "set",
        ReadAction::Modify => "modify",
        ReadAction::ModifyExternal => "modify-external",
    }
}

/// How the caller wants side-effect checks handled. `CheckSideEffects` refuses
/// restricted reads (default); `IgnoreSideEffects` is the escape hatch from
/// research D5 — render the value anyway.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ReadPermission {
    CheckSideEffects,
    IgnoreSideEffects,
}

fn own_restriction(read_action: Option<ReadAction>, access: Option<Access>) -> Option<ReadRestriction> {
    read_action
        .map(ReadRestriction::ReadAction)
        .or_else(|| access.filter(|a| !a.can_read()).map(|_| ReadRestriction::WriteOnly))
}

/// The register itself is restricted, or any of its fields is (a restricted
/// field propagates up — reading the register reads every field).
pub fn register_restriction(reg: &Register) -> Option<ReadRestriction> {
    own_restriction(reg.read_action, reg.properties.access)
        .or_else(|| reg.fields().find_map(|f| own_restriction(f.read_action, f.access)))
}

/// The field's own restriction; when the field carries neither `readAction` nor
/// `access`, fall back to the register — defense for the cases
/// `expand_properties` does not cover (the property-inheritance fixture
/// exercises this path).
pub fn field_restriction(reg: &Register, field: &Field) -> Option<ReadRestriction> {
    own_restriction(field.read_action, field.access)
        .or_else(|| own_restriction(reg.read_action, reg.properties.access))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::load::parse_for_test;
    use crate::Resolved;

    const XML: &str = r#"
        <device schemaVersion="1.3">
          <name>T</name>
          <size>32</size>
          <access>read-write</access>
          <resetValue>0x0</resetValue>
          <peripherals>
            <peripheral>
              <name>STAT</name>
              <baseAddress>0x40000000</baseAddress>
              <registers>
                <register>
                  <name>STATUS</name>
                  <addressOffset>0x0</addressOffset>
                  <readAction>clear</readAction>
                  <fields>
                    <field><name>BUSY</name><bitOffset>0</bitOffset><bitWidth>1</bitWidth></field>
                  </fields>
                </register>
                <register>
                  <name>TX</name>
                  <addressOffset>0x4</addressOffset>
                  <access>write-only</access>
                </register>
                <register>
                  <name>DATA</name>
                  <addressOffset>0x8</addressOffset>
                  <fields>
                    <field>
                      <name>CLR</name>
                      <bitOffset>0</bitOffset>
                      <bitWidth>1</bitWidth>
                      <readAction>clear</readAction>
                    </field>
                    <field><name>PLAIN</name><bitOffset>1</bitOffset><bitWidth>1</bitWidth></field>
                  </fields>
                </register>
                <register>
                  <name>CTRL</name>
                  <addressOffset>0xC</addressOffset>
                  <fields>
                    <field><name>EN</name><bitOffset>0</bitOffset><bitWidth>1</bitWidth></field>
                  </fields>
                </register>
              </registers>
            </peripheral>
          </peripherals>
        </device>"#;

    fn register_of<'s>(svd: &'s crate::Svd, query: &str) -> &'s Register {
        match svd.resolve(query).expect("resolve") {
            Resolved::Register { reg, .. } => reg,
            other => panic!("expected register, got {other:?}"),
        }
    }

    #[test]
    fn restriction_table() {
        let svd = parse_for_test(XML).expect("parse");
        // readAction=clear on the register itself.
        let status = register_of(&svd, "STAT.STATUS");
        assert_eq!(register_restriction(status), Some(ReadRestriction::ReadAction(ReadAction::Clear)));
        // write-only access.
        let tx = register_of(&svd, "STAT.TX");
        assert_eq!(register_restriction(tx), Some(ReadRestriction::WriteOnly));
        // Clean register: readable.
        let ctrl = register_of(&svd, "STAT.CTRL");
        assert_eq!(register_restriction(ctrl), None);
    }

    #[test]
    fn field_restriction_propagates_up_to_the_register() {
        let svd = parse_for_test(XML).expect("parse");
        // DATA is readable itself but its CLR field clears on read.
        let data = register_of(&svd, "STAT.DATA");
        assert_eq!(register_restriction(data), Some(ReadRestriction::ReadAction(ReadAction::Clear)));
    }

    #[test]
    fn field_restriction_falls_back_to_the_register() {
        let svd = parse_for_test(XML).expect("parse");
        let data = register_of(&svd, "STAT.DATA");
        let status = register_of(&svd, "STAT.STATUS");
        let ctrl = register_of(&svd, "STAT.CTRL");
        let clr = data.fields().find(|f| f.name == "CLR").expect("CLR");
        let plain = data.fields().find(|f| f.name == "PLAIN").expect("PLAIN");
        let busy = status.fields().find(|f| f.name == "BUSY").expect("BUSY");
        let en = ctrl.fields().find(|f| f.name == "EN").expect("EN");
        // Field with its own readAction.
        assert_eq!(field_restriction(data, clr), Some(ReadRestriction::ReadAction(ReadAction::Clear)));
        // Field without any own property in a clean register: readable.
        assert_eq!(field_restriction(ctrl, en), None);
        // Field without any own property in a clear-on-read register: falls back
        // to the register (expand_properties coverage defense).
        assert_eq!(field_restriction(status, busy), Some(ReadRestriction::ReadAction(ReadAction::Clear)));
        // A field is not restricted sideways by a restricted sibling; the
        // Resolved::Field wrapper adds that term (see the fixtures test
        // `field_read_restriction_includes_the_register_term`).
        assert_eq!(field_restriction(data, plain), None);
    }
}
