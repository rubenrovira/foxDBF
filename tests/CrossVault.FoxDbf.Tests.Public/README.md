# CrossVault.FoxDbf.Tests.Public

The public test suite for this repo — fully self-contained, synthetic test data only (no real
customer data, no Visual FoxPro IDE dependency). Safe to run anywhere `dotnet test` runs, including
this repo's own CI.

There's a second, private test project (`CrossVault.FoxDbf.Tests.Internal`) that isn't part of this
public mirror — it covers scenarios that need a local Visual FoxPro 9 installation as an oracle or
real production-derived fixtures, neither of which can ship publicly.

```bash
dotnet test CrossVault.FoxDbf.Tests.Public.csproj
```
