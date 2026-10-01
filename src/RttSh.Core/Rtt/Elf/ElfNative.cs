using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace RttSh.Core.Rtt.Elf;

/// <summary>P/Invoke surface of the Rust ELF crate (native/rttsh-elf →
/// rttsh_elf_native.dll, built by BuildRttshNative and shipped flat like lua54.dll
/// and rttsh_mcp_native.dll). Same interop conventions as McpNative: [LibraryImport]
/// source generation, UTF-8, panics caught inside the DLL. The return code reports
/// only the FFI ladder (Ok / invalid handle / bad argument / internal) - every domain
/// result travels through out-params, and a positive return is always a count, never
/// a status. The materialization blob is the one Rust allocation: parse hands it over
/// by pointer, this side copies it immediately and returns it via
/// <see cref="RttshElfFree"/> (same shape as the MCP dispatch response).</summary>
internal static unsafe partial class ElfNative
{
    private const string Library = "rttsh_elf_native";

    public const int Ok = 0;
    public const int ErrInvalidHandle = -1;
    public const int ErrBadArgument = -2;
    public const int ErrInternal = -3;   // panic caught inside the DLL

    /// <summary>Bumped on FFI shape changes; a mismatched DLL fails fast at first use
    /// (same fail-fast spirit as JLinkLibrary's version probe and McpNative's check).</summary>
    public const int AbiVersion = 1;

    /// <summary>Reason strings are one-liners; the DLL NUL-terminates and truncates to fit.</summary>
    private const uint ReasonCapacity = 512;

    // Materialization blob geometry; the layout is documented in
    // native/rttsh-elf/src/lib.rs - change the two sides together.
    private const int HeaderBytes = 12;
    private const int SymbolEntryBytes = 32;
    private const int SectionEntryBytes = 40;
    private const int CandidateEntryBytes = 24;

    [LibraryImport(Library, EntryPoint = "rttsh_elf_abi_version")]
    internal static partial int RttshElfAbiVersion();

    [LibraryImport(Library, EntryPoint = "rttsh_elf_parse")]
    private static unsafe partial int RttshElfParse(
        byte[] image,
        uint imageLen,
        out ElfLoadStatus status,
        out byte isLittleEndian,
        Span<byte> reason,
        uint reasonCap,
        out IntPtr blob,
        out uint blobLen,
        out IntPtr handle);

    [LibraryImport(Library, EntryPoint = "rttsh_elf_free")]
    private static partial void RttshElfFree(IntPtr blob, uint blobLen);

    [LibraryImport(Library, EntryPoint = "rttsh_elf_lookup", StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int RttshElfLookup(
        ElfNativeHandle handle,
        string name,
        uint nameLen,
        int kind,                  // -1 = no filter, else the ElfSymbolKind value
        out SymbolLookupStatus status,
        out ulong address,
        out ulong size,
        out byte kindCode,
        out byte bindingCode,
        Span<byte> candidates,     // fixed 24-byte entries; candidateCap = entry slots
        uint candidateCap,
        out uint candidateCount);

    [LibraryImport(Library, EntryPoint = "rttsh_elf_locate")]
    private static unsafe partial int RttshElfLocate(
        ElfNativeHandle handle,
        out RttElfLocateStatus status,
        out uint address,
        Span<byte> reason,
        uint reasonCap);

    [LibraryImport(Library, EntryPoint = "rttsh_elf_free_handle")]
    internal static partial int RttshElfFreeHandle(IntPtr handle);

    [LibraryImport(Library, EntryPoint = "rttsh_elf_panic_probe")]
    private static partial int RttshElfPanicProbe();

    private static int _abiChecked;

    /// <summary>Refuses a missing or ABI-mismatched DLL at first use.</summary>
    internal static void EnsureAbi()
    {
        if (Volatile.Read(ref _abiChecked) == 1)
            return;
        int abi = RttshElfAbiVersion();
        if (abi != AbiVersion)
            throw new InvalidOperationException(
                $"rttsh_elf_native.dll ABI mismatch: found v{abi}, expected v{AbiVersion} — rebuild native/ (cargo build --release)");
        Volatile.Write(ref _abiChecked, 1);
    }

    /// <summary>One-shot parse: bytes in, the load ladder out, and on success the decoded
    /// materialization plus an owned handle. The blob never outlives this call.</summary>
    internal static unsafe ElfNativeParseResult Parse(byte[]? bytes)
    {
        EnsureAbi();
        Span<byte> reason = stackalloc byte[(int)ReasonCapacity];
        int rc = RttshElfParse(bytes ?? [], (uint)(bytes?.Length ?? 0),
            out ElfLoadStatus status, out byte isLittleEndian,
            reason, ReasonCapacity, out IntPtr blob, out uint blobLen, out IntPtr handle);
        if (rc != Ok)
            throw Error(rc, nameof(Parse));

        if (status != ElfLoadStatus.Ok)
            return new ElfNativeParseResult(status, ReadNulString(reason), isLittleEndian != 0, null, null);

        // Wrap the handle before decoding: if the decode fails (only possible on ABI
        // drift the version probe failed to catch), the native model must still be freed.
        var native = new ElfNativeHandle(handle);
        ElfNativeMaterialization materialization;
        try
        {
            materialization = Decode(blob, blobLen);
        }
        catch
        {
            native.Dispose();
            throw;
        }
        finally
        {
            RttshElfFree(blob, blobLen);
        }
        return new ElfNativeParseResult(status, "", isLittleEndian != 0, materialization, native);
    }

    /// <summary>By-name lookup over the native model; see ElfImage.Lookup for the semantics.
    /// Ambiguous results make a second call to drain the full candidate list into an
    /// exactly-sized buffer (the first call reports the actual count; nothing truncates).</summary>
    internal static SymbolLookup Lookup(ElfNativeHandle handle, string name, ElfSymbolKind? kind)
    {
        int kindFilter = kind is { } wanted ? (int)wanted : -1;
        int rc = RttshElfLookup(handle, name, (uint)Encoding.UTF8.GetByteCount(name), kindFilter,
            out SymbolLookupStatus status, out ulong address, out ulong size,
            out byte kindCode, out byte bindingCode, Span<byte>.Empty, 0, out uint candidateCount);
        if (rc != Ok)
            throw Error(rc, nameof(Lookup));

        return status switch
        {
            SymbolLookupStatus.Found => SymbolLookup.Found(new ElfSymbol(
                name, address, size, (ElfSymbolKind)kindCode, (ElfSymbolBinding)bindingCode)),
            SymbolLookupStatus.Ambiguous => SymbolLookup.Ambiguous(
                DrainCandidates(handle, name, kindFilter, candidateCount)),
            _ => SymbolLookup.NotFound(),
        };
    }

    private static unsafe IReadOnlyList<ElfSymbol> DrainCandidates(
        ElfNativeHandle handle, string name, int kindFilter, uint count)
    {
        var entries = new byte[checked((int)count * CandidateEntryBytes)];
        int rc = RttshElfLookup(handle, name, (uint)Encoding.UTF8.GetByteCount(name), kindFilter,
            out _, out _, out _, out _, out _, entries, count, out _);
        if (rc != Ok)
            throw Error(rc, nameof(Lookup));

        var symbols = new List<ElfSymbol>((int)count);
        for (int i = 0; i < (int)count; i++)
        {
            int baseOffset = i * CandidateEntryBytes;
            symbols.Add(new ElfSymbol(
                name,
                BinaryPrimitives.ReadUInt64LittleEndian(entries.AsSpan(baseOffset, 8)),
                BinaryPrimitives.ReadUInt64LittleEndian(entries.AsSpan(baseOffset + 8, 8)),
                (ElfSymbolKind)entries[baseOffset + 16],
                (ElfSymbolBinding)entries[baseOffset + 17]));
        }
        return symbols;
    }

    /// <summary>The control-block policy ladder over an Ok image (null/non-Ok guards stay
    /// with RttControlBlock, which composes those reasons itself).</summary>
    internal static RttElfLocateResult Locate(ElfNativeHandle handle)
    {
        EnsureAbi();
        Span<byte> reason = stackalloc byte[(int)ReasonCapacity];
        int rc = RttshElfLocate(handle, out RttElfLocateStatus status, out uint address,
            reason, ReasonCapacity);
        if (rc != Ok)
            throw Error(rc, nameof(Locate));
        return new RttElfLocateResult(status, address, ReadNulString(reason));
    }

    /// <summary>Test path for the panic discipline: must answer <see cref="ErrInternal"/>.</summary>
    internal static int PanicProbe()
    {
        EnsureAbi();
        return RttshElfPanicProbe();
    }

    /// <summary>Decodes the materialization blob per the layout documented in
    /// native/rttsh-elf/src/lib.rs (all integers little-endian; the host is x64).
    /// A size mismatch means ABI drift the version probe failed to catch - fail fast.</summary>
    private static unsafe ElfNativeMaterialization Decode(IntPtr blob, uint blobLen)
    {
        var bytes = new ReadOnlySpan<byte>((void*)blob, checked((int)blobLen));
        uint symCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        uint sectCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        uint namesLen = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        int symBase = HeaderBytes;
        int sectBase = symBase + checked((int)symCount * SymbolEntryBytes);
        int namesBase = sectBase + checked((int)sectCount * SectionEntryBytes);
        if (namesBase + (long)namesLen != bytes.Length)
            throw new InvalidOperationException(
                "rttsh_elf_native.dll produced a malformed materialization blob (size mismatch — rebuild native/)");

        ReadOnlySpan<byte> names = bytes[namesBase..];
        var symbols = new List<ElfNativeSymbol>((int)symCount);
        for (int i = 0; i < (int)symCount; i++)
        {
            int baseOffset = symBase + i * SymbolEntryBytes;
            symbols.Add(new ElfNativeSymbol(
                ReadName(bytes, names, baseOffset),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(baseOffset + 8, 8)),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(baseOffset + 16, 8)),
                bytes[baseOffset + 24],
                bytes[baseOffset + 25]));
        }
        var sections = new List<ElfNativeSection>((int)sectCount);
        for (int i = 0; i < (int)sectCount; i++)
        {
            int baseOffset = sectBase + i * SectionEntryBytes;
            sections.Add(new ElfNativeSection(
                ReadName(bytes, names, baseOffset),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(baseOffset + 8, 8)),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(baseOffset + 16, 8)),
                BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(baseOffset + 24, 8)),
                bytes[baseOffset + 32] != 0));
        }
        return new ElfNativeMaterialization(symbols, sections);

        static string ReadName(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> names, int baseOffset)
        {
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(baseOffset, 4));
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(baseOffset + 4, 4));
            if ((long)offset + length > names.Length)
                throw new InvalidOperationException(
                    "rttsh_elf_native.dll produced a malformed materialization blob (name out of bounds — rebuild native/)");
            return Encoding.UTF8.GetString(names.Slice((int)offset, (int)length));
        }
    }

    private static string ReadNulString(ReadOnlySpan<byte> buffer)
    {
        int end = buffer.IndexOf((byte)0);
        if (end < 0)
            end = buffer.Length;
        return Encoding.UTF8.GetString(buffer[..end]);
    }

    private static InvalidOperationException Error(int rc, string operation) => new(
        rc switch
        {
            ErrInvalidHandle => $"{operation}: native image handle rejected (freed or unknown)",
            ErrBadArgument => $"{operation}: native call rejected an argument",
            ErrInternal => $"{operation}: native internal error (panic caught inside rttsh_elf_native.dll)",
            _ => $"{operation}: unknown native status {rc} (status ladder mismatch — rebuild native/)",
        });
}

/// <summary>Owns one parsed image on the Rust side; ReleaseHandle runs on the
/// finalizer thread, so an abandoned facade still frees its native model.</summary>
internal sealed class ElfNativeHandle : SafeHandle
{
    public ElfNativeHandle(IntPtr handle) : base(IntPtr.Zero, true) => SetHandle(handle);

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle() => ElfNative.RttshElfFreeHandle(handle) == ElfNative.Ok;
}

/// <summary>The parse outcome: the load ladder for the bytes themselves (IO never reaches
/// the DLL), and on Ok the materialized records plus the owned native handle the lookups
/// and the control-block locate run against.</summary>
internal sealed record ElfNativeParseResult(
    ElfLoadStatus Status,
    string Reason,
    bool IsLittleEndian,
    ElfNativeMaterialization? Materialization,
    ElfNativeHandle? Handle);

/// <summary>The C# mirror of the blob layout - symbols and sections as flat records,
/// names already decoded.</summary>
internal sealed record ElfNativeMaterialization(
    IReadOnlyList<ElfNativeSymbol> Symbols,
    IReadOnlyList<ElfNativeSection> Sections);

internal readonly record struct ElfNativeSymbol(
    string Name,
    ulong Address,
    ulong Size,
    byte Kind,
    byte Binding);

internal readonly record struct ElfNativeSection(
    string Name,
    ulong Address,
    ulong Size,
    long FileOffset,
    bool HasContents);
