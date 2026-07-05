using System;
using System.Diagnostics;

namespace CrossVault.FoxDbf.MicroVfp;

internal enum VfpInsertProfileBucket
{
    StatementFetchDispatch,
    MacroScan,
    ExpressionEval,
    InsertTargetColumnResolution,
    ValueConversionEncodeHandoff,
    BeginTxWriteLease,
    IndexMaintenanceCall,
    BufferingRiChecks,
    MemoryStoreAccess,
    SqlDmlFallback,
    DirectAppend,
}

internal sealed class VfpInsertProfile
{
    [ThreadStatic]
    private static VfpInsertProfile? s_current;

    private readonly long[] _ticks = new long[Enum.GetValues<VfpInsertProfileBucket>().Length];
    private readonly long[] _counts = new long[Enum.GetValues<VfpInsertProfileBucket>().Length];

    public static VfpInsertProfile? Current
    {
        get => s_current;
        set => s_current = value;
    }

    public void Add(VfpInsertProfileBucket bucket, long ticks)
    {
        int i = (int)bucket;
        _ticks[i] += ticks;
        _counts[i]++;
    }

    public (long Ticks, long Count) Get(VfpInsertProfileBucket bucket)
    {
        int i = (int)bucket;
        return (_ticks[i], _counts[i]);
    }

    public void Clear()
    {
        Array.Clear(_ticks);
        Array.Clear(_counts);
    }

    public static long Start() => s_current is null ? 0 : Stopwatch.GetTimestamp();

    public static void Stop(VfpInsertProfileBucket bucket, long start)
    {
        if (start == 0 || s_current is not { } p) return;
        p.Add(bucket, Stopwatch.GetTimestamp() - start);
    }
}

