//! The must-run corpus layer: the three ST files under `docs/SVD/` (also in the
//! STM32CubeProgrammer tree). Count snapshots pin svd-parser's expanded-tree
//! behavior; the invariants pin the derivedFrom semantics the crate relies on.
//! Numbers come from the research document (§3.1).

use rttsh_svd::{Resolved, Svd};

const DOCS_SVD: &str = concat!(env!("CARGO_MANIFEST_DIR"), "/../../docs/SVD");

fn load(name: &str) -> Svd {
    let path = std::path::Path::new(DOCS_SVD).join(name);
    Svd::load(&path).unwrap_or_else(|e| panic!("load {name}: {e}"))
}

#[derive(Debug)]
struct Counts {
    peripherals: usize,
    registers: usize,
    fields: usize,
    enum_fields: usize,
    read_action_registers: usize,
}

fn counts(svd: &Svd) -> Counts {
    let mut c = Counts {
        peripherals: 0,
        registers: 0,
        fields: 0,
        enum_fields: 0,
        read_action_registers: 0,
    };
    for p in svd.peripherals() {
        c.peripherals += 1;
        for r in p.all_registers() {
            c.registers += 1;
            if r.read_action.is_some() {
                c.read_action_registers += 1;
            }
            for f in r.fields() {
                c.fields += 1;
                if !f.enumerated_values.is_empty() {
                    c.enum_fields += 1;
                }
            }
        }
    }
    c
}

fn layout(svd: &Svd, periph: &str) -> Vec<(String, u32)> {
    let p = svd
        .peripherals()
        .find(|p| p.name == periph)
        .unwrap_or_else(|| panic!("peripheral {periph}"));
    p.all_registers().map(|r| (r.name.clone(), r.address_offset)).collect()
}

#[test]
fn stm32f103_snapshot() {
    let svd = load("STM32F103.svd");
    assert_eq!(svd.device_name(), "STM32F103");
    let c = counts(&svd);
    assert_eq!(c.peripherals, 61, "{c:?}");
    assert_eq!(c.registers, 906, "{c:?}");
    assert_eq!(c.fields, 6651, "{c:?}");
    assert_eq!(c.read_action_registers, 0, "{c:?}");
    // Peripheral-level derivedFrom: GPIOB copies GPIOA's layout at another base.
    assert_eq!(layout(&svd, "GPIOA"), layout(&svd, "GPIOB"));
    let mode0 = match svd.resolve("GPIOA.CRL.MODE0").unwrap() {
        Resolved::Field { field, .. } => field,
        other => panic!("expected a field, got {other:?}"),
    };
    assert_eq!((mode0.bit_offset(), mode0.bit_width()), (0, 2));
}

#[test]
fn stm32f411_snapshot() {
    let svd = load("STM32F411.svd");
    let c = counts(&svd);
    assert_eq!(c.peripherals, 47, "{c:?}");
    assert_eq!(c.registers, 669, "{c:?}");
    assert_eq!(c.fields, 4346, "{c:?}");
    assert_eq!(c.read_action_registers, 0, "{c:?}");
}

#[test]
fn stm32h743_snapshot() {
    let svd = load("STM32H743.svd");
    let c = counts(&svd);
    assert_eq!(c.peripherals, 122, "{c:?}");
    assert_eq!(c.registers, 2947, "{c:?}");
    assert_eq!(c.fields, 18242, "{c:?}");
    assert_eq!(c.enum_fields, 232, "{c:?}");
    assert_eq!(c.read_action_registers, 0, "{c:?}");
    // I2C2 derivedFrom I2C1: same layout, different base.
    assert_eq!(layout(&svd, "I2C1"), layout(&svd, "I2C2"));
    let i2c1 = svd.peripherals().find(|p| p.name == "I2C1").unwrap();
    let i2c2 = svd.peripherals().find(|p| p.name == "I2C2").unwrap();
    assert_eq!(i2c1.base_address, 0x40005400);
    assert_eq!(i2c2.base_address, 0x40005800);
    // Enumerated values survive loading (the crate's differentiator vs probe-rs).
    let resolved = svd.resolve("BDMA.BDMA_ISR.GIF0").unwrap();
    assert!(matches!(resolved, Resolved::Field { .. }));
}
