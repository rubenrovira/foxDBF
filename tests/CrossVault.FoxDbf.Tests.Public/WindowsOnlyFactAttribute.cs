using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// A [Fact] that runs only on Windows. The VFP byte-range lock model (RLOCK/FLOCK, shared-mode
/// coexistence with a real Visual FoxPro process, mandatory file-sharing violations) is Windows
/// semantics by definition: .NET's <see cref="System.IO.FileStream.Lock"/>/<c>Unlock</c> throw
/// <see cref="System.PlatformNotSupportedException"/> on Unix, and Unix has no mandatory sharing
/// locks — so lock-interop pins are meaningless there. Everything else (read/write/index/SQL/
/// interpreter semantics) stays cross-platform and keeps running on the Linux CI leg.
/// </summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!System.OperatingSystem.IsWindows())
            Skip = "Windows-only: VFP byte-range/mandatory lock semantics (FileStream.Lock is PlatformNotSupported on Unix).";
    }
}
