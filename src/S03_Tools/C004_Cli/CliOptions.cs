using Masque.Core;

namespace Masque.Cli;

/// <summary>Process exit codes, so scripts and AI assistants can tell outcomes apart.</summary>
public static class ExitCodes
{
    /// <summary>The masked copy was written (or help was shown).</summary>
    public const int Success = 0;

    /// <summary>Something went wrong; the message says what.</summary>
    public const int Error = 1;

    /// <summary>A new file shape needs a person to review its proposed column categories.</summary>
    public const int NeedsReview = 2;

    /// <summary>The command line was wrong.</summary>
    public const int Usage = 64;
}

/// <summary>Parsed command-line options.</summary>
/// <param name="InputPath">The CSV file to mask.</param>
/// <param name="OutputPath">Where to write the masked copy; null means next to the input.</param>
/// <param name="Accept">Confirm a proposed column config and continue.</param>
/// <param name="VaultPath">The vault file.</param>
/// <param name="ConfigFolder">The folder of saved column configs.</param>
public sealed record CliOptions(string InputPath, string? OutputPath, bool Accept, string VaultPath, string ConfigFolder)
{
    /// <summary>Help text shown for --help and after a usage error.</summary>
    public static string Usage { get; } = $"""
        Usage: masque <file.csv> [options]

        De-identifies a CSV file on this computer. Writes <file>.masque.csv with every
        sensitive value replaced by a fake token; the same real value always gets the
        same token. No data value is ever printed.

        The first time Masque sees a new set of columns, it proposes a category for each
        one and stops so you can review the saved config. Run again with --accept to
        confirm it; files with the same columns then run without stopping.

        Options:
          --out <path>      Where to write the masked copy (default: <file>.masque.csv)
          --accept          Confirm the proposed column categories and continue
          --vault <path>    Token vault (default: {MasquePaths.DefaultVault})
          --configs <dir>   Saved column configs (default: {MasquePaths.DefaultConfigs})
          -h, --help        Show this help

        Exit codes: 0 done, 1 error, 2 review needed, 64 bad arguments.
        """;

    /// <summary>True when the arguments ask for help.</summary>
    /// <param name="args">The command-line arguments.</param>
    /// <returns>Whether --help or -h is present.</returns>
    public static bool IsHelp(IReadOnlyList<string> args) => args.Any(a => a is "--help" or "-h");

    /// <summary>The default output path: the input's name with ".masque" before the extension.</summary>
    /// <param name="inputPath">The input file.</param>
    /// <returns>For "data.csv", "data.masque.csv" in the same folder.</returns>
    public static string DefaultOutputPath(string inputPath)
    {
        var folder = Path.GetDirectoryName(inputPath) ?? string.Empty;
        return Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(inputPath)}.masque{Path.GetExtension(inputPath)}");
    }

    /// <summary>Parses the command line.</summary>
    /// <param name="args">The command-line arguments.</param>
    /// <param name="options">The options, when parsing succeeds.</param>
    /// <param name="error">What was wrong, when parsing fails.</param>
    /// <returns>Whether the arguments were valid.</returns>
    public static bool TryParse(IReadOnlyList<string> args, out CliOptions? options, out string? error)
    {
        options = null;
        string? input = null, output = null, vault = null, configs = null;
        var accept = false;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--accept":
                    accept = true;
                    break;
                case "--out" or "--vault" or "--configs":
                    if (i + 1 >= args.Count)
                    {
                        error = $"{arg} needs a value.";
                        return false;
                    }

                    var value = args[++i];
                    if (arg == "--out") output = value;
                    else if (arg == "--vault") vault = value;
                    else configs = value;
                    break;
                case not null when arg.StartsWith('-'):
                    error = $"Unknown option {arg}.";
                    return false;
                default:
                    if (input is not null)
                    {
                        error = "Give one input file at a time.";
                        return false;
                    }

                    input = arg;
                    break;
            }
        }

        if (input is null)
        {
            error = "No input file given.";
            return false;
        }

        options = new CliOptions(input, output, accept, vault ?? MasquePaths.DefaultVault, configs ?? MasquePaths.DefaultConfigs);
        error = null;
        return true;
    }
}
