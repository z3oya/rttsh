using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace RttSh.Core.Rtt.Elf;

/// <summary>P/Invoke facade of the Rust ELF crate (native/rttsh-elf →
/// rttsh_elf_native.dll); the declarations are the generated ElfNativeGen.g.cs.
/// Same interop conventions as the MCP crate: UTF-8, panics caught inside the
/// DLL. The return code reports only the FFI ladder (Ok / invalid handle / bad
/// argument / internal) - every domain result travels through out-params, and a
/// positive return is always a count, never a status. The materialization blob
/// is the one Rust allocation: parse hands it over by pointer, this side copies
/// it immediately and returns it via <see cref="ElfNativeGen.rttsh_elf_free"/>.</summary>
internal static unsafe partial class ElfNative
{
    /// <summary>Reason strings are one-liners; the DLL NUL-terminates and truncates to fit.</summary>
    private const uint ReasonCapacity = 512;

    private static int _abiChecked;

    /// <summary>Refuses a missing or ABI-mismatched DLL at first use.</summary>
    internal static void EnsureAbi()
    {
        if (Volatile.Read(ref _abiChecked) == 1)
            return;
        int abi = ElfNativeGen.rttsh_elf_abi_version();
        if (abi != ElfNativeGen.ABI_VERSION)
            throw new InvalidOperationException(
                $"rttsh_elf_native.dll ABI mismatch: found v{abi}, expected v{ElfNativeGen.ABI_VERSION} — rebuild native/ (cargo build --release)");
        Volatile.Write(ref _abiChecked, 1);
    }

    /// <summary>One-shot parse: bytes in, the load ladder out, and on success the decoded
    /// materialization plus an owned handle. The blob never outlives this call.</summary>
    internal static unsafe ElfNativeParseResult Parse(byte[]? bytes)
    {
        EnsureAbi();
        byte[] image = bytes ?? [];
        Span<byte> reason = stackalloc byte[(int)ReasonCapacity];
        int status = 0;
        byte isLittleEndian = 0;
        byte* blob = null;
        uint blobLen = 0;
        nuint model = 0;
        fixed (byte* imagePtr = NonNull(image), reasonPtr = reason)
        {
            int rc = ElfNativeGen.rttsh_elf_parse(imagePtr, (uint)image.Length,
                &status, &isLittleEndian, reasonPtr, ReasonCapacity, &blob, &blobLen, &model);
            if (rc != ElfNativeGen.OK)
                throw Error(rc, nameof(Parse));
        }

        var loadStatus = (ElfLoadStatus)status;
        if (loadStatus != ElfLoadStatus.Ok)
            return new ElfNativeParseResult(loadStatus, ReadNulString(reason), isLittleEndian != 0, null, null);

        // Wrap the handle before decoding: if the decode fails (only possible on ABI
        // drift the version probe failed to catch), the native model must still be freed.
        var native = new ElfNativeHandle((IntPtr)model);
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
            ElfNativeGen.rttsh_elf_free(blob, blobLen);
        }
        return new ElfNativeParseResult(loadStatus, "", isLittleEndian != 0, materialization, native);
    }

    /// <summary>By-name lookup over the native model; see ElfImage.Lookup for the semantics.
    /// Ambiguous results make a second call to drain the full candidate list into an
    /// exactly-sized buffer (the first call reports the actual count; nothing truncates).</summary>
    internal static unsafe SymbolLookup Lookup(ElfNativeHandle handle, string name, ElfSymbolKind? kind)
    {
        int kindFilter = kind is { } wanted ? (int)wanted : -1;
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        int status = 0;
        ulong address = 0, size = 0;
        byte kindCode = 0, bindingCode = 0;
        uint candidateCount = 0;
        bool added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            nuint h = (nuint)handle.DangerousGetHandle();
            fixed (byte* namePtr = NonNull(nameBytes))
            {
                int rc = ElfNativeGen.rttsh_elf_lookup(h, namePtr, (uint)nameBytes.Length, kindFilter,
                    &status, &address, &size, &kindCode, &bindingCode, null, 0, &candidateCount);
                if (rc != ElfNativeGen.OK)
                    throw Error(rc, nameof(Lookup));
            }
        }
        finally
        {
            if (added)
                handle.DangerousRelease();
        }
        return ((SymbolLookupStatus)status) switch
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
        var entries = new byte[checked((int)count * ElfNativeGen.BLOB_CANDIDATE_ENTRY_BYTES)];
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        bool added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            nuint h = (nuint)handle.DangerousGetHandle();
            int status = 0;
            ulong address = 0, size = 0;
            byte kindCode = 0, bindingCode = 0;
            uint candidateCount = 0;
            fixed (byte* namePtr = NonNull(nameBytes), entriesPtr = entries)
            {
                int rc = ElfNativeGen.rttsh_elf_lookup(h, namePtr, (uint)nameBytes.Length, kindFilter,
                    &status, &address, &size, &kindCode, &bindingCode,
                    entriesPtr, (uint)entries.Length, &candidateCount);
                if (rc != ElfNativeGen.OK)
                    throw Error(rc, nameof(Lookup));
            }
        }
        finally
        {
            if (added)
                handle.DangerousRelease();
        }

        var symbols = new List<ElfSymbol>((int)count);
        for (int i = 0; i < (int)count; i++)
        {
            int baseOffset = i * ElfNativeGen.BLOB_CANDIDATE_ENTRY_BYTES;
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
    internal static unsafe RttElfLocateResult Locate(ElfNativeHandle handle)
    {
        EnsureAbi();
        Span<byte> reason = stackalloc byte[(int)ReasonCapacity];
        int status = 0;
        uint address = 0;
        bool added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            nuint h = (nuint)handle.DangerousGetHandle();
            fixed (byte* reasonPtr = reason)
            {
                int rc = ElfNativeGen.rttsh_elf_locate(h, &status, &address, reasonPtr, ReasonCapacity);
                if (rc != ElfNativeGen.OK)
                    throw Error(rc, nameof(Locate));
            }
        }
        finally
        {
            if (added)
                handle.DangerousRelease();
        }
        // InvalidImage in the ladder is the C# scan's own verdict; the DLL never produces it
        return new RttElfLocateResult((RttElfLocateStatus)status, address, ReadNulString(reason));
    }

    /// <summary>Test path for the panic discipline: must answer <see cref="ElfNativeGen.ErrInternal"/>.</summary>
    internal static int PanicProbe()
    {
        EnsureAbi();
        return ElfNativeGen.rttsh_elf_panic_probe();
    }

    /// <summary>Decodes the materialization blob per the layout documented in
    /// native/rttsh-elf/src/lib.rs (all integers little-endian; the host is x64).
    /// A size mismatch means ABI drift the version probe failed to catch - fail fast.</summary>
    private static unsafe ElfNativeMaterialization Decode(byte* blob, uint blobLen)
    {
        var bytes = new ReadOnlySpan<byte>(blob, checked((int)blobLen));
        uint symCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        uint sectCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        uint namesLen = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        int symBase = ElfNativeGen.BLOB_HEADER_BYTES;
        int sectBase = symBase + checked((int)symCount * ElfNativeGen.BLOB_SYMBOL_ENTRY_BYTES);
        int namesBase = sectBase + checked((int)sectCount * ElfNativeGen.BLOB_SECTION_ENTRY_BYTES);
        if (namesBase + (long)namesLen != bytes.Length)
            throw new InvalidOperationException(
                "rttsh_elf_native.dll produced a malformed materialization blob (size mismatch — rebuild native/)");

        ReadOnlySpan<byte> names = bytes[namesBase..];
        var symbols = new List<ElfNativeSymbol>((int)symCount);
        for (int i = 0; i < (int)symCount; i++)
        {
            int baseOffset = symBase + i * ElfNativeGen.BLOB_SYMBOL_ENTRY_BYTES;
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
            int baseOffset = sectBase + i * ElfNativeGen.BLOB_SECTION_ENTRY_BYTES;
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

    /// <summary>`fixed` on an empty array yields null; the DLL rejects null even
    /// at len 0 (it short-circuits the 0 length before any read).</summary>
    private static byte[] NonNull(byte[] bytes) => bytes.Length == 0 ? [0] : bytes;

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
            ElfNativeGen.ErrInvalidHandle => $"{operation}: native image handle rejected (freed or unknown)",
            ElfNativeGen.ErrBadArgument => $"{operation}: native call rejected an argument",
            ElfNativeGen.ErrInternal => $"{operation}: native internal error (panic caught inside rttsh_elf_native.dll)",
            _ => $"{operation}: unknown native status {rc} (status ladder mismatch — rebuild native/)",
        });
}

/// <summary>Owns one parsed image on the Rust side; ReleaseHandle runs on the
/// finalizer thread, so an abandoned facade still frees its native model.</summary>
internal sealed class ElfNativeHandle : SafeHandle
{
    public ElfNativeHandle(IntPtr handle) : base(IntPtr.Zero, true) => SetHandle(handle);

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle() =>
        ElfNativeGen.rttsh_elf_free_handle((nuint)handle) == ElfNativeGen.OK;
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
