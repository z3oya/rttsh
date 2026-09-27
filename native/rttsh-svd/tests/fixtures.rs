//! The six ST-style fixtures, asserted end to end: load notes, restrictions,
//! refusal sentences, rendering. Each fixture demonstrates exactly one feature
//! the ST corpus exercises (or one of its two known file-bug shapes); features
//! ST never emits are deliberately absent (docs/plans/svd-module.md).

use rttsh_svd::policy::{ReadPermission, ReadRestriction};
use rttsh_svd::svd_rs::ReadAction;
use rttsh_svd::{LoadNotes, ReadError, Resolved, Svd};

fn fixture(name: &str) -> Svd {
    let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("tests/fixtures")
        .join(name);
    Svd::load(&path).unwrap_or_else(|e| panic!("load {name}: {e}"))
}

fn layout(svd: &Svd, periph: &str) -> Vec<(String, u32)> {
    let p = svd
        .peripherals()
        .find(|p| p.name == periph)
        .unwrap_or_else(|| panic!("peripheral {periph}"));
    p.all_registers().map(|r| (r.name.clone(), r.address_offset)).collect()
}

fn register_of<'s>(resolved: &'s Resolved<'s>) -> &'s rttsh_svd::svd_rs::Register {
    match resolved {
        Resolved::Register { reg, .. } => reg,
        other => panic!("expected a register, got {other:?}"),
    }
}

#[test]
fn peripheral_derived_copies_layout_and_inherits_everything() {
    let svd = fixture("derived.svd");
    assert_eq!(svd.notes(), &LoadNotes::default());
    assert_eq!(layout(&svd, "GPIOA"), layout(&svd, "GPIOB"));
    let a = svd.resolve("GPIOA.CRL").unwrap();
    let b = svd.resolve("GPIOB.CRL").unwrap();
    assert_eq!(a.absolute_address(), 0x40010800);
    assert_eq!(b.absolute_address(), 0x40010C00);
    // The derived copy inherits the resetValue and the field's enums too.
    assert_eq!(
        register_of(&a).properties.reset_value,
        register_of(&b).properties.reset_value,
    );
    let read = rttsh_svd::render::render_read(&b, 0x44444441, ReadPermission::CheckSideEffects).unwrap();
    assert!(read.contains("MODE0[1:0] = 0x1 OUTPUT : General purpose output"), "{read}");
}

#[test]
fn read_action_clear_restricts_at_both_levels() {
    let svd = fixture("read_action_clear.svd");
    // Register-level clear.
    let status = svd.resolve("STAT.STATUS").unwrap();
    assert_eq!(
        status.read_restriction(),
        Some(ReadRestriction::ReadAction(ReadAction::Clear))
    );
    // Field-level clear propagates up to the register.
    let data = svd.resolve("STAT.DATA").unwrap();
    assert_eq!(
        data.read_restriction(),
        Some(ReadRestriction::ReadAction(ReadAction::Clear))
    );
    // A plain field of that register is restricted too at the Resolved level:
    // reading the field reads the whole register, sibling side effect included.
    assert_eq!(
        svd.resolve("STAT.DATA.PLAIN").unwrap().read_restriction(),
        Some(ReadRestriction::ReadAction(ReadAction::Clear))
    );
    // Clean register reads fine.
    let ctrl = svd.resolve("STAT.CTRL").unwrap();
    assert_eq!(ctrl.read_restriction(), None);
    let out = rttsh_svd::render::render_read(&ctrl, 0x1, ReadPermission::CheckSideEffects).unwrap();
    assert!(out.contains("EN[0:0] = 0x1"), "{out}");

    // The refusal sentence: half-sentence from ReadRestriction, prefix and tail
    // from the render layer.
    let err = rttsh_svd::render::render_read(&status, 0x1, ReadPermission::CheckSideEffects).unwrap_err();
    match &err {
        ReadError::Restricted(restriction) => {
            assert_eq!(restriction.to_string(), "cannot be read without side effects (readAction=clear)");
            assert_eq!(
                rttsh_svd::render::render_refusal(&status, restriction),
                "STAT.STATUS cannot be read without side effects (readAction=clear); pass IgnoreSideEffects to override"
            );
        }
        other => panic!("expected Restricted, got {other:?}"),
    }
    // The escape hatch renders the value.
    let forced = rttsh_svd::render::render_read(&status, 0x1, ReadPermission::IgnoreSideEffects).unwrap();
    assert!(forced.starts_with("STAT.STATUS = 0x00000001 "), "{forced}");
    assert!(forced.contains("BUSY[0:0] = 0x1"), "{forced}");
}

#[test]
fn write_only_access_restricts() {
    let svd = fixture("write_only.svd");
    let tx = svd.resolve("WO.TX").unwrap();
    assert_eq!(tx.read_restriction(), Some(ReadRestriction::WriteOnly));
    let err = rttsh_svd::render::render_read(&tx, 0, ReadPermission::CheckSideEffects).unwrap_err();
    assert!(err.to_string().contains("write-only"), "{err}");
    // read-only stays readable.
    let rx = svd.resolve("WO.RX").unwrap();
    assert_eq!(rx.read_restriction(), None);
    let out = rttsh_svd::render::render_read(&rx, 0xAB, ReadPermission::CheckSideEffects).unwrap();
    assert!(out.contains("DATAR[7:0] = 0xab"), "{out}");
}

#[test]
fn enum_out_of_range_loads_relaxed_and_never_mislabels() {
    let svd = fixture("enum_out_of_range.svd");
    assert!(svd.notes().relaxed, "{:?}", svd.notes());
    assert!(svd.notes().stripped.is_empty());
    let field = svd.resolve("TIM.SMCR.SMS1").unwrap();
    // Every field value decodes; the out-of-range entry cannot crash or shadow
    // beyond its masked slot (value-0 wins by document order).
    for raw in 0..=7u64 {
        let out = rttsh_svd::render::render_read(&field, raw, ReadPermission::CheckSideEffects)
            .unwrap_or_else(|e| panic!("raw {raw}: {e}"));
        assert!(out.contains("SMS1[2:0]"), "raw {raw}: {out}");
    }
    let zero = rttsh_svd::render::render_read(&field, 0, ReadPermission::CheckSideEffects).unwrap();
    assert!(zero.contains("ZERO"), "{zero}");
    let two = rttsh_svd::render::render_read(&field, 2, ReadPermission::CheckSideEffects).unwrap();
    assert!(two.contains("= 0x2") && !two.contains("EIGHT"), "{two}");
}

#[test]
fn dangling_derived_is_stripped_and_reported() {
    let svd = fixture("dangling_derived.svd");
    assert_eq!(svd.notes().stripped, vec!["SEC_X".to_string()]);
    assert!(!svd.notes().relaxed);
    assert!(svd.resolve("SEC_X").is_err());
    assert!(svd.resolve("GPIOA.CRL").is_ok());
}

#[test]
fn both_bug_shapes_at_once_need_repair_and_relaxation() {
    // Rung 3 repairs the dangling ref, then the repaired text still carries the
    // out-of-range enum, so the pass over it lands on the relaxed rung —
    // notes must report both.
    let svd = fixture("dangling_and_enum.svd");
    assert_eq!(svd.notes().stripped, vec!["SEC_X".to_string()]);
    assert!(svd.notes().relaxed);
    assert!(svd.resolve("SEC_X").is_err());
    assert!(svd.resolve("GPIOA").is_ok());
    let field = svd.resolve("TIM.SMCR.SMS1").unwrap();
    let out =
        rttsh_svd::render::render_read(&field, 2, ReadPermission::CheckSideEffects).unwrap();
    assert!(out.contains("= 0x2") && !out.contains("EIGHT"), "{out}");
}

#[test]
fn properties_inherit_and_the_field_fallback_defends() {
    let svd = fixture("props_inherit.svd");
    // Device resetValue pushed down to a register that carries none.
    let r1 = svd.resolve("P.R1").unwrap();
    let reg = register_of(&r1);
    assert_eq!(reg.properties.reset_value, Some(0x12345678));
    assert_eq!(reg.properties.size, Some(32));
    // Field without own properties in a clean register: readable.
    assert_eq!(svd.resolve("P.R1.F1").unwrap().read_restriction(), None);
    // Field without own properties in a clear-on-read register: the fallback
    // consults the register (expand_properties coverage defense).
    assert_eq!(
        svd.resolve("P.R2.F2").unwrap().read_restriction(),
        Some(ReadRestriction::ReadAction(ReadAction::Clear))
    );
}
