using System.Linq;
using Dapper;
using Xunit;

namespace CrossVault.FoxDbf.Tests.Data;

/// <summary>
/// (7) Dapper smoke test: the provider must be a good enough <c>System.Data.Common</c> citizen for the
/// most common micro-ORM. <c>Query&lt;T&gt;</c> maps result rows to a DTO and <c>Execute</c> runs a
/// parameterized UPDATE and returns the affected count. TDD RED until the provider is implemented.
/// </summary>
public sealed class FoxDbfDapperTests
{
    private sealed class PersonDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string City { get; set; } = "";
        public decimal Amount { get; set; }
    }

    [Fact]
    public void Dapper_Query_MapsRows()
    {
        using var db = new PersonDb();
        using var conn = db.Open();

        var rows = conn.Query<PersonDto>(
            "SELECT id, name, city, amount FROM person WHERE amount > 100 ORDER BY id").ToList();

        Assert.Equal(new[] { 2, 3, 4, 5, 6, 8, 10 }, rows.Select(p => p.Id).ToArray());
        Assert.Equal("Sm", rows[0].Name.TrimEnd());
    }

    [Fact]
    public void Dapper_Execute_ParameterizedUpdate_ReturnsAffected()
    {
        using var db = new PersonDb();
        using var conn = db.Open();

        int affected = conn.Execute(
            "UPDATE person SET city = @city WHERE id = @id", new { city = "Bonn", id = 1 });

        Assert.Equal(1, affected);

        var city = conn.ExecuteScalar<string>("SELECT city FROM person WHERE id = 1");
        Assert.Equal("Bonn", city!.TrimEnd());
    }
}
