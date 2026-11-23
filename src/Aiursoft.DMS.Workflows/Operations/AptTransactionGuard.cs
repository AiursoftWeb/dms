using System.Text.Json;
using Aiursoft.DMS.Contracts;

namespace Aiursoft.DMS.Workflows.Operations;

public sealed record AptTransactionHook(string Executable, string Command);

public static class AptTransactionGuard
{
    public const string ExpectedEnvironment = "DMS_APT_EXPECTED_CHANGES";
    public const string ArchitectureEnvironment = "DMS_APT_ARCHITECTURE";
    public const string Confirmed = "DMS verified the approved APT transaction.";
    public const string Rejected = "DMS rejected the APT transaction:";

    public static int Verify(TextReader reader, TextWriter diagnostics, string expectedJson, string architecture)
    {
        try
        {
            var expected = JsonSerializer.Deserialize<AptPackageChange[]>(expectedJson)
                ?? throw new InvalidDataException("No approved APT changes were supplied.");
            if (reader.ReadLine() != "VERSION 3") throw new InvalidDataException("APT transaction hook protocol 3 is required.");
            string? line;
            do { line = reader.ReadLine(); } while (line is { Length: > 0 });
            if (line == null) throw new InvalidDataException("APT did not supply its transaction actions.");

            var actual = new List<AptPackageChange>();
            var configure = new List<(string Package, string Version)>();
            var purge = expected.Any(change => change.Action == AptPackageAction.Purge);
            while ((line = reader.ReadLine()) != null)
            {
                // Version 3 has eight fixed fields, followed by the action or the complete .deb path.
                var fields = line.Split(' ', 9);
                if (fields.Length != 9 || fields.Take(8).Any(string.IsNullOrEmpty))
                    throw new InvalidDataException("APT sent an unrecognized transaction action.");
                var before = fields[1] == "-" ? null : fields[1];
                var after = fields[5] == "-" ? null : fields[5];
                var package = PackageName(fields[0] + ":" + (after == null ? fields[2] : fields[6]), architecture);
                var action = fields[8];
                if (action == "**CONFIGURE**" && after != null)
                    configure.Add((package, after));
                else if (action == "**REMOVE**" && after == null)
                    actual.Add(new AptPackageChange(purge ? AptPackageAction.Purge : AptPackageAction.Remove, package, before, null));
                else if (action.StartsWith('/') && after != null)
                    actual.Add(new AptPackageChange(before == null ? AptPackageAction.Install : AptPackageAction.Upgrade, package, before, after));
                else
                    throw new InvalidDataException("APT sent an unsupported transaction action.");
            }

            var difference = Difference(expected, actual);
            if (difference.Length != 0 || actual.Count != expected.Length)
                throw new InvalidDataException("The transaction differs from the approved changes.\n" + difference);

            var unapprovedConfigurations = configure.Where(c => !expected.Any(change =>
                change.Package == c.Package && change.ToVersion == c.Version)).ToArray();
            if (unapprovedConfigurations.Length != 0)
                throw new InvalidDataException("APT would configure a package outside the approved plan.\n" +
                    string.Join('\n', unapprovedConfigurations.Select(c => $"Current only: Configure {c.Package} ({c.Version})")));
            diagnostics.WriteLine(Confirmed);
            return 0;
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException)
        {
            diagnostics.WriteLine($"{Rejected} {exception.Message}");
            return 1;
        }
    }

    internal static string PackageName(string package, string architecture) =>
        package.EndsWith(":" + architecture, StringComparison.Ordinal) || package.EndsWith(":all", StringComparison.Ordinal)
            ? package[..package.LastIndexOf(':')]
            : package;

    internal static string Difference(IReadOnlyCollection<AptPackageChange> expected, IReadOnlyCollection<AptPackageChange> actual) =>
        string.Join('\n', expected.Except(actual).Select(change => "Approved only: " + Describe(change))
            .Concat(actual.Except(expected).Select(change => "Current only: " + Describe(change))));

    internal static string Describe(IEnumerable<AptPackageChange> changes) => string.Join('\n', changes.Select(Describe));

    private static string Describe(AptPackageChange change) =>
        $"{change.Action} {change.Package} ({change.FromVersion ?? "-"} -> {change.ToVersion ?? "-"})";
}
