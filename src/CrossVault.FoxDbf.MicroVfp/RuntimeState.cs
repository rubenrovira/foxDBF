namespace CrossVault.FoxDbf.MicroVfp;

// ─────────────────────────────────────────────────────────────────────────────
//  microVFP P1b — runtime STATE the interpreter threads through a call.
//
//  Backs the VFP state functions (SELECT/RECNO/EOF/PCOUNT/ON/SET …) with REAL
//  runtime values and carries the two settings whose interaction the RI
//  framework depends on: the installed ON ERROR handler and SET REPROCESS.
//  Per MICROVFP_SEMANTICS.md §Locking: SET REPROCESS TO 0 branches on whether an
//  ON ERROR handler is installed (with handler → fail-fast .F.; without → retry
//  forever). This type is intentionally tiny in P1b — it is the observable seam
//  the unit tests assert against; the full state-function backing lands with the
//  statement interpreter.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Mutable per-session interpreter runtime state (the backing for the VFP state
/// functions and the SET-setting interactions the RI framework relies on).</summary>
public sealed class RuntimeState
{
    /// <summary>The current <c>ON ERROR</c> command text, or <see langword="null"/> when no handler is
    /// installed (a bare <c>ON ERROR</c> clears it). <c>ON("ERROR")</c> reads this back.</summary>
    public string? OnError { get; internal set; }

    /// <summary>True when an <c>ON ERROR</c> handler is currently installed.</summary>
    public bool OnErrorInstalled => !string.IsNullOrEmpty(OnError);

    /// <summary>The numeric operand of the last <c>SET REPROCESS TO n</c> (AUTOMATIC is modelled as
    /// <c>-2</c>). VFP's default is 0 retries.</summary>
    public int Reprocess { get; internal set; }

    /// <summary>
    /// SET UNIQUE (default OFF). When ON, an <c>INDEX ON … TAG</c> with no explicit
    /// <c>UNIQUE</c>/<c>CANDIDATE</c> clause builds a UNIQUE tag (one entry, lowest recno, per distinct
    /// key). Read back by <c>SET("UNIQUE")</c>. microVFP P1 gap #1 — INDEX/ORDER WRITE model.
    /// </summary>
    public bool Unique { get; internal set; }

    /// <summary>
    /// The RI-critical branch (MICROVFP_SEMANTICS.md §Locking / risk #5): <c>SET REPROCESS TO 0</c>
    /// makes the lock FUNCTIONS (RLOCK/FLOCK/…) fail FAST (return <c>.F.</c> immediately) WHEN an
    /// <c>ON ERROR</c> handler is installed; with no handler the same setting means "retry forever".
    /// True ⇒ a failed lock returns <c>.F.</c> at once instead of spinning.
    /// </summary>
    public bool LockFailFast => Reprocess == 0 && OnErrorInstalled;

    // ── retained LAST-ERROR state (backs AERROR; see MICROVFP_SEMANTICS.md Nachtrag) ──
    // VFP clears the LIVE ERROR()/MESSAGE() when the ON ERROR handler returns, but the RI handler
    // (rierror) reads the error AFTER the trap; AERROR() therefore reads these RETAINED fields, which
    // hold the last trapped error until the next one fires (not cleared on handler exit).

    /// <summary>AERROR column [1] — the last trapped error number (<c>ERROR()</c>); 0 ⇒ no error yet.</summary>
    public int LastErrorNumber { get; internal set; }

    /// <summary>AERROR column [2] — the last trapped error message (<c>MESSAGE()</c>).</summary>
    public string LastErrorMessage { get; internal set; } = string.Empty;

    /// <summary>AERROR column [3] — extra detail (≈<c>SYS(2018)</c>) / a field-rule validation message;
    /// <see langword="null"/> ⇒ <c>.NULL.</c>.</summary>
    public string? LastErrorDetail { get; internal set; }

    /// <summary>AERROR column [4] — the work area the error occurred in; 0 ⇒ <c>.NULL.</c>.</summary>
    public int LastErrorArea { get; internal set; }

    /// <summary>AERROR column [5] for a TRIGGER failure (err 1539): 1=insert / 2=update / 3=delete;
    /// 0 ⇒ not a trigger failure.</summary>
    public int LastErrorTrigger { get; internal set; }

    /// <summary>AERROR column [5] for a field-rule error: the violating field number; 0 ⇒ not a field rule.</summary>
    public int LastErrorField { get; internal set; }
}
