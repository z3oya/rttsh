// Generates the committed LuaNativeGen.g.cs from src/lib.rs; the negative
// status ladder is hand-written (csbindgen skips non-literal consts).
fn main() {
    csbindgen::Builder::default()
        .input_extern_file("src/lib.rs")
        .csharp_dll_name("rttsh_lua_native")
        .csharp_namespace("Toolbox.Tools.RttCli.Scripting")
        .csharp_class_name("LuaNativeGen")
        .csharp_type_rename(|n| if n == "ByteSlice" { "NativeByteSlice".to_string() } else { n })
        .csharp_generate_const_filter(|n| n != "EXIT_SENTINEL")
        .generate_csharp_file("../../src/RttSh/Scripting/LuaNativeGen.g.cs")
        .unwrap();
}
