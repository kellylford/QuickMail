using System.IO;
using QuickMail.Fixtures;

// Fixture generator CLI (#180 Phase 1):
//   dotnet run --project tools/QuickMail.Fixtures -- --out <dir>
// Writes a complete, deterministic QuickMail profile (accounts.json, mail.db,
// contacts.json, flags.json, views.json, rules.json, templates.json, config.ini)
// through the app's REAL persistence services — never hand-rolled SQL/JSON —
// so schema migrations can never desync the fixtures (spec Decision A).

string? outDir = null;
for (var i = 0; i < args.Length - 1; i++)
{
    if (string.Equals(args[i], "--out", StringComparison.OrdinalIgnoreCase))
        outDir = args[i + 1];
}
if (string.IsNullOrWhiteSpace(outDir))
{
    Console.Error.WriteLine("Usage: QuickMail.Fixtures --out <profileDir>");
    return 64;
}

try
{
    var summary = await DefaultFixtureSet.WriteAsync(Path.GetFullPath(outDir));
    Console.WriteLine(summary);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Fixture generation failed: {ex}");
    return 1;
}
