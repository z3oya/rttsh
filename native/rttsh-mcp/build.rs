// Generates the committed McpNativeGen.g.cs from src/lib.rs; the negative
// status ladder is hand-written (csbindgen skips non-literal consts).
fn main() {
    csbindgen::Builder::default()
        .input_extern_file("src/lib.rs")
        .csharp_dll_name("rttsh_mcp_native")
        .csharp_namespace("Toolbox.Tools.RttCli")
        .csharp_class_name("McpNativeGen")
        .csharp_generate_const_filter(|n| n == "OK" || n == "ABI_VERSION")
        .generate_csharp_file("../../src/RttSh/McpNativeGen.g.cs")
        .unwrap();
}
