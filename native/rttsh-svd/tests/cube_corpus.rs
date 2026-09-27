//! The opt-in corpus layer: the full STM32CubeProgrammer SVD tree (267 files,
//! 1.18GB at the time of the research). Set `RTTSH_SVD_CORPUS=<dir>` to run —
//! without it (or without the directory) the test skips silently, so plain
//! `cargo test` stays fast and CI needs nothing.
//!
//! Every file must load through the tolerance ladder; the assertion is the
//! research result itself (267/267). Feature-distribution totals are printed,
//! not asserted.

use rttsh_svd::Svd;

#[test]
fn full_corpus_parses_through_the_ladder() {
    let Ok(dir) = std::env::var("RTTSH_SVD_CORPUS") else {
        eprintln!("skipped: RTTSH_SVD_CORPUS is not set");
        return;
    };
    let root = std::path::PathBuf::from(dir);
    if !root.is_dir() {
        eprintln!("skipped: {} is not a directory", root.display());
        return;
    }

    let mut files: Vec<std::path::PathBuf> = match std::fs::read_dir(&root) {
        Ok(entries) => entries
            .filter_map(|e| e.ok())
            .map(|e| e.path())
            .filter(|p| p.extension().is_some_and(|x| x == "svd"))
            .collect(),
        Err(e) => panic!("read {}: {e}", root.display()),
    };
    let cores = root.join("Cores");
    if let Ok(entries) = std::fs::read_dir(&cores) {
        files.extend(
            entries
                .filter_map(|e| e.ok())
                .map(|e| e.path())
                .filter(|p| p.extension().is_some_and(|x| x == "svd")),
        );
    }
    files.sort();
    assert!(!files.is_empty(), "no .svd files under {}", root.display());

    let (mut ok, mut peripherals, mut registers, mut fields) = (0usize, 0usize, 0usize, 0usize);
    let mut failures: Vec<String> = Vec::new();
    for path in &files {
        match Svd::load(path) {
            Ok(svd) => {
                ok += 1;
                for p in svd.peripherals() {
                    peripherals += 1;
                    for r in p.all_registers() {
                        registers += 1;
                        fields += r.fields().count();
                    }
                }
            }
            Err(e) => failures.push(format!("{}: {e}", path.display())),
        }
    }

    eprintln!(
        "corpus: {ok}/{} files loaded; totals: {peripherals} peripherals, {registers} registers, {fields} fields",
        files.len()
    );
    assert!(failures.is_empty(), "{} file(s) failed:\n{}", failures.len(), failures.join("\n"));
}
