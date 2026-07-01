using System.Runtime.CompilerServices;

// CrossVault.FoxDbf.Tests.Internal reuses this project's internal test helpers (MicroVfpTestSupport,
// its TempDir/CopyDatabase/NewTastrade/NewFromSource) instead of duplicating them.
[assembly: InternalsVisibleTo("CrossVault.FoxDbf.Tests.Internal")]
