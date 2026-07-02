using System;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// microVFP P2 table/record batch — part (3): RENAME TABLE renames ONLY the long-name catalog entry in
/// the current DBC (the NAME field of the Tables system table); the physical .dbf file is untouched.
///
/// Written TESTS-FIRST: RED until the interpreter mutates the DBC member catalog. SAFETY: runs over a
/// throwaway TEMP COPY of the public TasTrade database — the committed fixture is never touched.
/// </summary>
public sealed class MicroVfpRenameTableTableRecordTests
{
    [Fact]
    public void RenameTable_RenamesDbcMember_LeavesOtherMembersIntact()
    {
        using var dir = new MicroVfpTestSupport.TempDir("rename");
        var interp = MicroVfpTestSupport.NewTastrade(dir, out var session);

        string dbc = Path.Combine(dir.Path, "tastrade.dbc");

        // Sanity: "shippers" is a member long-name before the rename.
        Assert.Contains(session.Database!.TableNames, n => n.Equals("shippers", StringComparison.OrdinalIgnoreCase));

        interp.Execute("RENAME TABLE shippers TO shippers_x");
        session.Dispose();   // release DBC handles before re-reading the catalog from disk

        var names = DbfDatabase.OpenFoxpro(dbc).TableNames.ToArray();
        Assert.Contains(names, n => n.Equals("shippers_x", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Equals("shippers", StringComparison.OrdinalIgnoreCase));
        // Unrelated members survive the rename.
        Assert.Contains(names, n => n.Equals("customer", StringComparison.OrdinalIgnoreCase));
    }
}
