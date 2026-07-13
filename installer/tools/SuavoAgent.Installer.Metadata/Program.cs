using SuavoAgent.Installer.Metadata;

try
{
    var request = CommandLine.Parse(args);
    _ = InstallerMetadataGenerator.Generate(request);
    return 0;
}
catch (Exception exception) when (exception is
           ArgumentException or
           IOException or
           UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Installer metadata generation failed: {exception.Message}");
    return 2;
}

internal static class CommandLine
{
    public static InstallerMetadataRequest Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count % 2 != 0 ||
            Enumerable.Range(0, arguments.Count / 2)
                .Select(index => arguments[index * 2])
                .Any(static option => option is not
                    ("--version" or "--timestamp" or "--output-dir" or "--binary")))
        {
            throw new ArgumentException(
                "Installer metadata arguments must use recognized option/value pairs.");
        }

        var version = SingleValue(arguments, "--version");
        var timestamp = SingleValue(arguments, "--timestamp");
        var outputDirectory = SingleValue(arguments, "--output-dir");
        var binarySpecs = Enumerable.Range(0, arguments.Count / 2)
            .Where(index => string.Equals(arguments[index * 2], "--binary", StringComparison.Ordinal))
            .Select(index => arguments[(index * 2) + 1])
            .ToArray();
        var parsedBinaries = binarySpecs.Select(ParseBinary).ToArray();
        if (parsedBinaries.Select(static item => item.Name).Distinct(StringComparer.Ordinal).Count() !=
            parsedBinaries.Length)
        {
            throw new ArgumentException("Duplicate --binary names are not allowed.");
        }
        var binaryPaths = parsedBinaries.ToDictionary(
            static item => item.Name,
            static item => item.Path,
            StringComparer.Ordinal);
        return new InstallerMetadataRequest(version, timestamp, outputDirectory, binaryPaths);
    }

    private static string SingleValue(IReadOnlyList<string> arguments, string option)
    {
        var matches = Enumerable.Range(0, arguments.Count / 2)
            .Where(index => string.Equals(arguments[index * 2], option, StringComparison.Ordinal))
            .Select(index => arguments[(index * 2) + 1])
            .ToArray();
        return matches.Length == 1 &&
               !string.IsNullOrWhiteSpace(matches[0]) &&
               !matches[0].StartsWith("--", StringComparison.Ordinal)
            ? matches[0]
            : throw new ArgumentException($"Exactly one {option} value is required.");
    }

    private static BinaryPath ParseBinary(string specification)
    {
        var separator = specification.IndexOf('=');
        if (separator <= 0 || separator == specification.Length - 1)
            throw new ArgumentException("Each --binary value must use Name=Path.");
        return new BinaryPath(
            specification[..separator],
            specification[(separator + 1)..]);
    }

    private sealed record BinaryPath(string Name, string Path);
}
