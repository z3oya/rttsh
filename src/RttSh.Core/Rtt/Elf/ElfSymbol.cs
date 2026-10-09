namespace RttSh.Core.Rtt.Elf;

/// <summary>One .symtab entry as our consumers see it: a name, where it lives, how big it is.
/// Addresses are raw st_value (no Thumb-bit or relocation post-processing - the RTT control
/// block is an OBJECT whose value is the plain RAM address, and function bits are irrelevant
/// to every current consumer).</summary>
public readonly record struct ElfSymbol(
    string Name,
    ulong Address,
    ulong Size,
    ElfSymbolKind Kind,
    ElfSymbolBinding Binding);

/// <summary>Immutable lookup result. There is deliberately no Symbol property - a struct
/// default would smuggle a null Name past the non-nullable annotation - so the safe accessor
/// is TryGetSymbol, which answers only for Found. Candidates is meaningful only when Status is
/// Ambiguous (one entry per same-name symbol, after the kind filter).</summary>
public sealed class SymbolLookup
{
    public static SymbolLookup NotFound() => new(SymbolLookupStatus.NotFound, default, []);
    public static SymbolLookup Found(ElfSymbol symbol) => new(SymbolLookupStatus.Found, symbol, []);
    public static SymbolLookup Ambiguous(IReadOnlyList<ElfSymbol> candidates) => new(SymbolLookupStatus.Ambiguous, default, candidates);

    private SymbolLookup(SymbolLookupStatus status, ElfSymbol symbol, IReadOnlyList<ElfSymbol> candidates)
    {
        Status = status;
        _symbol = symbol;
        Candidates = candidates;
    }

    public SymbolLookupStatus Status { get; }
    public IReadOnlyList<ElfSymbol> Candidates { get; }

    /// <summary>The found symbol, when Status is Found; false with a default symbol otherwise.</summary>
    public bool TryGetSymbol(out ElfSymbol symbol)
    {
        symbol = _symbol;
        return Status == SymbolLookupStatus.Found;
    }

    private readonly ElfSymbol _symbol;
}
