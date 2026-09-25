# native/

Rust workspace for rttsh's native layer. `cargo build --release` builds it; the
`BuildRttshNative` MSBuild target in `src/RttSh/RttSh.csproj` runs that automatically
and copies `rttsh_mcp_native.dll` flat next to the managed output (same shape as
lua54.dll), so a plain `dotnet build` is all the C# side needs.

- `rttsh-mcp` — the FFI glue foundation (session lifecycle, byte pump, Rust→C#
  dispatch callback, panic discipline). No MCP functionality yet; the MCP server
  layer will be added here. `cargo test` covers the FFI logic on the Rust side, and
  `tests/RttSh.Tests/RttNative/` covers the same boundary from the C# side.

Conventions (from the C#↔Rust interop study): `extern "C"` exports return status
codes and never let a panic escape; buffers are caller-allocated with a
needed-size retry; UTF-8 everywhere; the Windows cdylib statically links the CRT
(see `.cargo/config.toml`), so it needs no VC++ Redistributable.
