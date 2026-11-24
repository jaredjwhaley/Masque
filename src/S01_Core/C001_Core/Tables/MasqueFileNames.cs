using System.Globalization;

namespace Masque.Core.Tables;

/// <summary>
/// Names and checks for the files Masque writes beside a data file: dated backups and temporary save files. Both keep
/// the data file's extension, so a guard that protects data files by extension protects them too; folder scans use
/// <see cref="IsWorkFile"/> to skip them.
/// </summary>
public static class MasqueFileNames
{
    /// <summary>The marker in every backup's name, before the data file's extension.</summary>
    public const string BackupMarker = ".masque-backup";

    /// <summary>The marker in every temporary save file's name, before the data file's extension.</summary>
    public const string SavingMarker = ".masque-saving";

    /// <summary>
    /// Whether a file is one of Masque's backups or temporary save files, in today's naming
    /// ("people.20261124-153000.masque-backup.csv") or the earlier one ("people.csv.20261120-153000.masque-backup").
    /// </summary>
    /// <param name="path">A file path or name.</param>
    /// <returns><see langword="true"/> for Masque's own files.</returns>
    public static bool IsWorkFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var name = Path.GetFileName(path);
        return HasMarker(name, BackupMarker) || HasMarker(name, SavingMarker);
    }

    /// <summary>"people.20261124-153000.masque-backup.csv" in local time, with a counter if that name is taken.</summary>
    /// <param name="path">The data file being backed up.</param>
    /// <returns>A backup path in the same folder that doesn't exist yet.</returns>
    public static string NewBackupPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(full)!;
        var stem = Path.GetFileNameWithoutExtension(full);
        var extension = Path.GetExtension(full);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        var backup = Path.Combine(folder, $"{stem}.{stamp}{BackupMarker}{extension}");
        for (var n = 2; File.Exists(backup); n++)
        {
            backup = Path.Combine(folder, $"{stem}.{stamp}-{n}{BackupMarker}{extension}");
        }

        return backup;
    }

    /// <summary>A hidden-style, unique temporary name beside <paramref name="path"/>, keeping its extension.</summary>
    /// <param name="path">The file about to be written.</param>
    /// <returns>The temporary file's path.</returns>
    internal static string TemporaryFor(string path)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return Path.Combine(folder, $".{Path.GetFileNameWithoutExtension(path)}.{Guid.NewGuid():N}{SavingMarker}{Path.GetExtension(path)}");
    }

    /// <summary>Creates the temporary file and lets <paramref name="write"/> fill it.</summary>
    internal static void WriteTemporary(string temporary, Action<Stream> write)
    {
        // Read and write: some writers (Excel packages) read back what they have written while saving.
        using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite);
        write(stream);
    }

    /// <summary>Refuses a file that is gone or changed on disk since it was read.</summary>
    /// <exception cref="TableFileException">The file changed.</exception>
    internal static void EnsureUnchanged(string path, FileStamp loaded)
    {
        if (!File.Exists(path) || FileStamp.Of(path) != loaded)
        {
            throw new TableFileException(
                $"{Path.GetFileName(path)} changed on disk after Masque read it, so it was not saved. Run the statement again to work on the current file.");
        }
    }

    /// <summary>The marker followed by a dot (today's naming) or ending the name (the earlier naming).</summary>
    private static bool HasMarker(string name, string marker) =>
        name.Contains(marker + ".", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(marker, StringComparison.OrdinalIgnoreCase);
}
