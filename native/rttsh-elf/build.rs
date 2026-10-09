// Generates the committed ElfNativeGen.g.cs from the exports plus the #[repr]
// status enums in the sibling modules (syn does not follow `mod`; the enums
// cross only as `as i32` casts, hence always_included_types). LocateStatus is
// left out - the C# twin carries an extra hand-written variant.
fn main() {
    csbindgen::Builder::default()
        .input_extern_file("src/lib.rs")
        .input_extern_file("src/model.rs")
        .input_extern_file("src/lookup.rs")
        .input_extern_file("src/locate.rs")
        .csharp_dll_name("rttsh_elf_native")
        .csharp_namespace("RttSh.Core.Rtt.Elf")
        .csharp_class_name("ElfNativeGen")
        .csharp_class_accessibility("public") // consumed cross-assembly
        .always_included_types(["LoadStatus", "LookupStatus", "SymbolKind", "SymbolBinding"])
        .csharp_type_rename(|n| match n.as_str() {
            "LoadStatus" => "ElfLoadStatus".to_string(),
            "LookupStatus" => "SymbolLookupStatus".to_string(),
            "SymbolKind" => "ElfSymbolKind".to_string(),
            "SymbolBinding" => "ElfSymbolBinding".to_string(),
            _ => n,
        })
        .csharp_generate_const_filter(|n| {
            n == "OK" || n == "ABI_VERSION" || n.starts_with("BLOB_")
        })
        .generate_csharp_file("../../src/RttSh.Core/Rtt/Elf/ElfNativeGen.g.cs")
        .unwrap();
}
