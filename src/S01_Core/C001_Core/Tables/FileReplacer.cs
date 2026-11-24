namespace Masque.Core.Tables;

/// <summary>A file's size and last write time, taken when it was read, to tell whether it changed since.</summary>
/// <param name="Length">The size in bytes.</param>
/// <param name="LastWriteTimeUtc">When it was last written.</param>
public sealed record FileStamp(long Length, DateTime LastWriteTimeUtc)
{
    /// <summary>Takes the stamp of a file as it is now.</summary>
    /// <param name="path">An existing file.</param>
    /// <returns>Its stamp.</returns>
    public static FileStamp Of(string path)
    {
        var info = new FileInfo(path);
        return new FileStamp(info.Length, info.LastWriteTimeUtc);
    }
}

/// <summary>
/// Replaces a file with new content in one step, keeping the previous version beside it as a dated backup (named by
/// <see cref="MasqueFileNames.NewBackupPath"/>). The new content is written to a temporary file in the same folder
/// first, so a failure part way leaves the file as it was.
/// </summary>
public static class FileReplacer
{
    /// <summary>Writes new content over a file, unless the file changed since it was read.</summary>
    /// <param name="path">The file to replace.</param>
    /// <param name="loaded">The file's stamp when it was read.</param>
    /// <param name="write">Writes the complete new content to the stream it is given.</param>
    /// <returns>The path of the backup holding the previous version.</returns>
    /// <exception cref="TableFileException">The file changed on disk since it was read; nothing is written.</exception>
    public static string Replace(string path, FileStamp loaded, Action<Stream> write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(write);

        MasqueFileNames.EnsureUnchanged(path, loaded);

        var temporary = MasqueFileNames.TemporaryFor(path);
        try
        {
            MasqueFileNames.WriteTemporary(temporary, write);

            var backup = MasqueFileNames.NewBackupPath(path);
            File.Replace(temporary, path, backup);
            return backup;
        }
        catch (IOException ex) when (ex is not (FileNotFoundException or DirectoryNotFoundException))
        {
            // Most often the file is open in Excel, which locks it. The library's message is dropped: it adds nothing.
            throw new TableFileException(
                $"Could not save {Path.GetFileName(path)}: it may be open in another program (Excel locks files it has open). Close it and run the statement again.");
        }
        finally
        {
            // Gone already after a successful replace; removed here after any failure.
            File.Delete(temporary);
        }
    }
}
