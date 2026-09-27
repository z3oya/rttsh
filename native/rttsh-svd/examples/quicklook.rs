//! A quick look at what rttsh-svd produces: load a file through the tolerance
//! ladder, then render the list, an info tree, a decoded read, and the first
//! restricted-read refusal it can find.
//!
//! Usage:
//! - `cargo run -p rttsh-svd --example quicklook -- <path-to-svd>`
//! - `cargo run -p rttsh-svd --example quicklook -- <path-to-svd> <PERIPH.REG[.FIELD]> [raw]`
//!   (defaults to `docs/SVD/STM32F103.svd` relative to the repo root)

use std::time::Instant;

use rttsh_svd::policy::ReadPermission;
use rttsh_svd::{Resolved, Svd};

fn main() {
    let mut args = std::env::args().skip(1);
    let path = args
        .next()
        .unwrap_or_else(|| "../../docs/SVD/STM32F103.svd".to_string());
    let query = args.next();
    let raw = args.next().map_or(0x3333_3333, |a| match a.strip_prefix("0x") {
        Some(hex) => u64::from_str_radix(hex, 16).expect("raw hex"),
        None => a.parse().expect("raw"),
    });

    let started = Instant::now();
    let svd = Svd::load(&path).expect("load");
    println!(
        "# {} — {} peripherals, loaded in {:.0?} (debug build), notes: relaxed={} stripped={:?}",
        svd.device_name(),
        svd.peripherals().count(),
        started.elapsed(),
        svd.notes().relaxed,
        svd.notes().stripped,
    );

    if let Some(query) = &query {
        let resolved = svd.resolve(query).expect("resolve");
        println!("\n== render_info({query}) ==");
        println!("{}", rttsh_svd::render::render_info(&resolved));
        println!("== render_read({query}, raw = {raw:#x}) ==");
        match rttsh_svd::render::render_read(&resolved, raw, ReadPermission::CheckSideEffects) {
            Ok(text) => print!("{text}"),
            Err(e) => println!("refused: {}", refusal(&resolved, &e)),
        }
        return;
    }

    println!("\n== render_list (first 6 lines) ==");
    for line in rttsh_svd::render::render_list(&svd).lines().take(6) {
        println!("{line}");
    }

    // The first peripheral with a register that has fields, for the info/read demos.
    let demo = svd
        .peripherals()
        .find(|p| p.all_registers().any(|r| r.fields().next().is_some()))
        .expect("a peripheral with fields");
    let reg = demo
        .all_registers()
        .find(|r| r.fields().any(|f| !f.enumerated_values.is_empty()))
        .or_else(|| demo.all_registers().find(|r| r.fields().next().is_some()))
        .expect("a register with fields");

    println!("\n== render_info({}.{}) ==", demo.name, reg.name);
    let resolved = svd.resolve(&format!("{}.{}", demo.name, reg.name)).expect("resolve");
    print!("{}", rttsh_svd::render::render_info(&resolved));

    println!("\n== render_read({}.{}, raw = 0x33333333) ==", demo.name, reg.name);
    match rttsh_svd::render::render_read(&resolved, 0x3333_3333, ReadPermission::CheckSideEffects) {
        Ok(text) => print!("{text}"),
        Err(e) => println!("refused: {}", refusal(&resolved, &e)),
    }

    println!("\n== first restricted read (readAction / write-only) ==");
    let restricted = svd.peripherals().find_map(|p| {
        p.all_registers().find_map(|r| {
            let query = format!("{}.{}", p.name, r.name);
            svd.resolve(&query).ok()?.read_restriction().map(|_| query)
        })
    });
    match restricted.map(|q| svd.resolve(&q).expect("re-resolve")) {
        Some(resolved @ Resolved::Register { .. }) => {
            let err =
                rttsh_svd::render::render_read(&resolved, 0, ReadPermission::CheckSideEffects)
                    .expect_err("restricted");
            println!("{}", refusal(&resolved, &err));
        }
        _ => println!("(none in this file)"),
    }
}

fn refusal(resolved: &Resolved, err: &rttsh_svd::ReadError) -> String {
    match err {
        rttsh_svd::ReadError::Restricted(restriction) => {
            rttsh_svd::render::render_refusal(resolved, restriction)
        }
        other => format!("{other}"),
    }
}
