namespace CrossVault.FoxDbf;

/// <summary>
/// The DBC-stored validation/default/trigger metadata for ONE member table (plan §A8 / P3b),
/// decoded from its <c>OBJECTTYPE=="Table"</c> record's <c>PROPERTY</c> blob plus the per-field
/// rules from its child Field records. Each string is the VFP expression EXACTLY as
/// <c>DBGETPROP('table','Table', ...)</c> returns it (the authoritative oracle); a property that
/// is not set in the DBC surfaces as <see langword="null"/> (DBGETPROP returns the empty string
/// for the same case). The trigger expressions are typically the bound RI calls
/// (e.g. <c>__ri_delete_customer()</c>) or user procedures.
/// </summary>
public sealed record DbcTableRules
{
    /// <summary>The table's logical name (the DBC Table <c>OBJECTNAME</c>).</summary>
    public required string TableName { get; init; }

    /// <summary>The record-level (table) validation RULE expression (<c>DBGETPROP(...,'Table','RuleExpression')</c>), or <see langword="null"/>.</summary>
    public string? RuleExpression { get; init; }

    /// <summary>The record rule's error MESSAGE text (<c>DBGETPROP(...,'Table','RuleText')</c>), or <see langword="null"/>.</summary>
    public string? RuleText { get; init; }

    /// <summary>The INSERT trigger expression (<c>DBGETPROP(...,'Table','InsertTrigger')</c>), or <see langword="null"/>.</summary>
    public string? InsertTrigger { get; init; }

    /// <summary>The UPDATE trigger expression (<c>DBGETPROP(...,'Table','UpdateTrigger')</c>), or <see langword="null"/>.</summary>
    public string? UpdateTrigger { get; init; }

    /// <summary>The DELETE trigger expression (<c>DBGETPROP(...,'Table','DeleteTrigger')</c>), or <see langword="null"/>.</summary>
    public string? DeleteTrigger { get; init; }

    /// <summary>The primary-key index tag (<c>DBGETPROP(...,'Table','PrimaryKey')</c>), or <see langword="null"/> when the table has none.</summary>
    public string? PrimaryKey { get; init; }

    /// <summary>The per-field rules/defaults, in physical column order (one entry per DBC Field record).</summary>
    public IReadOnlyList<DbcFieldRules> Fields { get; init; } = Array.Empty<DbcFieldRules>();

    /// <summary>The rules for the named field (case-insensitive), or <see langword="null"/> when the table has no such field.</summary>
    public DbcFieldRules? Field(string fieldName)
    {
        ArgumentNullException.ThrowIfNull(fieldName);
        foreach (var f in Fields)
            if (string.Equals(f.FieldName, fieldName, StringComparison.OrdinalIgnoreCase))
                return f;
        return null;
    }
}
