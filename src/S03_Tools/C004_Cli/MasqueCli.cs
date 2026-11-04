using System.Text.Json;
using Masque.Core;
using Masque.Core.Config;
using Masque.Core.Detection;
using Masque.Csv;
using Masque.Vault;

namespace Masque.Cli;

/// <summary>
/// The masque command. Rule for everything printed here: headers, categories, paths and counts only, never a
/// data value, because the output may be read by an AI assistant.
/// </summary>
public static class MasqueCli
{
    /// <summary>Runs the command.</summary>
    /// <param name="args">The command-line arguments.</param>
    /// <param name="output">Normal output.</param>
    /// <param name="error">Error output.</param>
    /// <param name="keyProtector">Vault key protection; null uses the OS secret store. Tests pass a fake.</param>
    /// <returns>One of <see cref="ExitCodes"/>.</returns>
    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error, IKeyProtector? keyProtector = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (CliOptions.IsHelp(args))
        {
            output.WriteLine(CliOptions.Usage);
            return ExitCodes.Success;
        }

        if (!CliOptions.TryParse(args, out var options, out var message))
        {
            error.WriteLine(message);
            error.WriteLine();
            error.WriteLine(CliOptions.Usage);
            return ExitCodes.Usage;
        }

        try
        {
            return Mask(options!, output, keyProtector);
        }
        catch (Exception ex) when (ex is CsvTableException or VaultAccessException or PlatformNotSupportedException
            or TokenSpaceExhaustedException or IOException or UnauthorizedAccessException or JsonException)
        {
            // These messages are written to name files, rows and causes, never data.
            error.WriteLine($"Error: {ex.Message}");
            return ExitCodes.Error;
        }
        catch (Exception ex)
        {
            // Unknown errors may carry data in their message, so only the type is shown.
            error.WriteLine($"Unexpected error ({ex.GetType().Name}). Details are hidden so that no data is printed.");
            return ExitCodes.Error;
        }
    }

    private static int Mask(CliOptions options, TextWriter output, IKeyProtector? keyProtector)
    {
        if (!File.Exists(options.InputPath))
        {
            throw new FileNotFoundException($"File not found: {options.InputPath}");
        }

        var sample = CsvTable.ReadSample(options.InputPath);

        // Without a header row, the "headers" are real data, and printing them would leak it.
        if (sample.Headers.Any(ValuePatternDetector.LooksLikePersonalValue))
        {
            throw new CsvTableException(
                "The first row looks like data, not column headers. Add a header row and try again.");
        }

        var store = new ConfigStore(options.ConfigFolder);
        var fingerprint = SchemaFingerprint.Compute(sample.Headers);
        var config = store.Load(fingerprint) ?? Propose(fingerprint, sample, store);

        WritePlan(output, options.InputPath, fingerprint, config);

        if (!config.Confirmed)
        {
            if (!options.Accept)
            {
                output.WriteLine();
                output.WriteLine("This is a new set of columns. Review the proposed categories in:");
                output.WriteLine($"  {store.PathFor(fingerprint)}");
                output.WriteLine($"Change any category if needed ({string.Join(", ", Enum.GetNames<PiiCategory>())}),");
                output.WriteLine("then run the same command again with --accept.");
                return ExitCodes.NeedsReview;
            }

            config = config with { Confirmed = true };
            store.Save(config);
            output.WriteLine("Config confirmed; files with these columns will run without review from now on.");
        }

        var outputPath = options.OutputPath ?? CliOptions.DefaultOutputPath(options.InputPath);
        var categories = sample.Headers.Select(config.CategoryFor).ToList();

        using var vault = SqliteTokenVault.Open(options.VaultPath, keyProtector ?? KeyProtectors.ForCurrentPlatform());
        var tokenizer = Tokenizer.CreateDefault(vault, CryptoRandomSource.Shared);
        var rows = CsvTable.Mask(options.InputPath, outputPath, categories, tokenizer);

        output.WriteLine($"Masked {rows} rows: {outputPath}");
        return ExitCodes.Success;
    }

    /// <summary>Classifies each column and saves the result as an unconfirmed config for review.</summary>
    private static MaskConfig Propose(string fingerprint, CsvSample sample, ConfigStore store)
    {
        var classifier = ColumnClassifier.CreateDefault();
        var columns = sample.Headers
            .Select((header, i) => classifier.Classify(i, header, sample.ColumnSamples[i]))
            .Select(plan => new ColumnRule(plan.Header, plan.Category, plan.Reason))
            .ToList();

        var config = new MaskConfig(fingerprint, Confirmed: false, columns);
        store.Save(config);
        return config;
    }

    private static void WritePlan(TextWriter output, string inputPath, string fingerprint, MaskConfig config)
    {
        output.WriteLine($"Columns in {Path.GetFileName(inputPath)} (shape {fingerprint}, {(config.Confirmed ? "confirmed" : "proposed")}):");

        var width = config.Columns.Count == 0 ? 0 : config.Columns.Max(c => c.Header.Length);
        foreach (var column in config.Columns)
        {
            output.WriteLine($"  {column.Header.PadRight(width)}  {column.Category,-11}  {column.Reason}");
        }
    }
}
