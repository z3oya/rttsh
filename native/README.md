# native/

Rust workspace for rttsh's native layer. `cargo build --release` builds it; the
`BuildRttshNative` MSBuild target in `src/RttSh/RttSh.csproj` runs that automatically
and copies the cdylib outputs (`rttsh_mcp_native.dll`, `rttsh_elf_native.dll`) flat
next to the managed output (same shape as lua54.dll), so a plain `dotnet build` is
all the C# side needs.

- `rttsh-mcp` — the MCP layer (rmcp protocol + tool schemas in Rust, tool execution
  dispatched back into managed code over the v4 pointer-handover callback).
  `cargo test` covers the FFI logic on the Rust side, and
  `tests/RttSh.Tests/RttNative/` covers the same boundary from the C# side.
- `rttsh-svd` — SVD parsing/query/rendering library (rlib only, no FFI yet; the
  future foundation for the sfr_* tool surface).
- `rttsh-elf` — the ELF domain (parse ladder, symbol lookup, RTT control-block
  locate) behind `rttsh_elf_native.dll`; the C# facade is
  `src/RttSh.Core/Rtt/Elf/{ElfNative,ElfImage}.cs`.

Conventions (from the C#↔Rust interop study, per crate ABI ladder): `extern "C"`
exports return status codes and never let a panic escape; a negative return is the
status ladder, a positive one a count, and domain results travel through out-params;
buffers are caller-allocated, with Rust-allocated blobs handed over by pointer and
freed by the allocator that made them; UTF-8 everywhere; the Windows cdylib
statically links the CRT (see `.cargo/config.toml`), so it needs no VC++
Redistributable.
