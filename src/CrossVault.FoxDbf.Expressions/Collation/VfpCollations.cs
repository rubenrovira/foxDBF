using System;

namespace CrossVault.FoxDbf.Expressions;

/// <summary>
/// Built-in VFP collations and the factory that maps a CDX tag's sort-sequence name
/// (the tag-header string at offset 494) to an <see cref="IVfpCollation"/>.
/// </summary>
/// <remarks>
/// Covered code pages: <b>1252</b> (Western) today. 437 / 850 / 1250 use the same
/// head/tail table format in <c>COLL4ARR.C</c> and are addable as sibling tables; until
/// then a non-MACHINE, non-1252-GENERAL sort sequence falls back to MACHINE.
/// </remarks>
public static class VfpCollations
{
    /// <summary>MACHINE collation: raw CP1252 unsigned byte order (the engine default).</summary>
    public static IVfpCollation Machine { get; } = MachineCollation.Instance;

    /// <summary>GENERAL collation: the CP1252 Western sort sequence (case- and
    /// accent-folded primary ordering, accents resolved by a tail pass).</summary>
    public static IVfpCollation General { get; } = GeneralCollation.Instance;

    /// <summary>Resolves a collation by its VFP name (case-insensitive). Unknown or
    /// empty names fall back to MACHINE.</summary>
    public static IVfpCollation ByName(string? name)
        => string.Equals(name?.Trim(), "GENERAL", StringComparison.OrdinalIgnoreCase) ? General : Machine;

    /// <summary>
    /// Selects the collation for a CDX tag from its stored sort-sequence name (tag header
    /// @494, as surfaced by the index reader): empty / "MACHINE" → <see cref="Machine"/>;
    /// "GENERAL" → <see cref="General"/> (CP1252); any other (currently unsupported)
    /// sequence → <see cref="Machine"/> as a documented, never-throwing fallback.
    /// </summary>
    public static IVfpCollation FromSortSequence(string? sortSequence)
    {
        if (string.IsNullOrWhiteSpace(sortSequence)) return Machine;
        string s = sortSequence.Trim();
        if (string.Equals(s, "MACHINE", StringComparison.OrdinalIgnoreCase)) return Machine;
        if (string.Equals(s, "GENERAL", StringComparison.OrdinalIgnoreCase)) return General;
        return Machine; // documented fallback for unsupported sort sequences (437/850/1250/etc.)
    }
}
