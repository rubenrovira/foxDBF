namespace CrossVault.FoxDbf;

/// <summary>
/// The DBC-stored validation/default metadata for ONE member-table field (plan §A8 / P3b),
/// decoded from that <c>OBJECTTYPE=="Field"</c> record's <c>PROPERTY</c> blob. Each string is
/// the VFP expression EXACTLY as <c>DBGETPROP('table.field','Field', ...)</c> returns it
/// (the authoritative oracle); a property that is not set in the DBC surfaces as
/// <see langword="null"/> (DBGETPROP returns the empty string for the same case).
/// </summary>
public sealed record DbcFieldRules
{
    /// <summary>The field's long name (the DBC Field <c>OBJECTNAME</c>).</summary>
    public required string FieldName { get; init; }

    /// <summary>
    /// The field's DEFAULT expression (<c>DBGETPROP(...,'Field','DefaultValue')</c>), e.g.
    /// <c>createid()</c> / <c>DATETIME()</c> / <c>0</c>, or <see langword="null"/> when unset.
    /// May call a stored-procedure UDF — evaluated by microVFP on the write path (later task).
    /// </summary>
    public string? DefaultValue { get; init; }

    /// <summary>The field-level validation RULE expression (<c>DBGETPROP(...,'Field','RuleExpression')</c>), or <see langword="null"/>.</summary>
    public string? RuleExpression { get; init; }

    /// <summary>The field rule's error MESSAGE text (<c>DBGETPROP(...,'Field','RuleText')</c>), or <see langword="null"/>.</summary>
    public string? RuleText { get; init; }
}
