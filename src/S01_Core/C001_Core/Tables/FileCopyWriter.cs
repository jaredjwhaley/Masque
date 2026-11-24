namespace Masque.Core.Tables;

/// <summary>
/// Writes changed data to a new file beside its source ("save as"), leaving the source untouched. The copy must stay
/// in the source's folder and keep its file type, so whatever guards the source (a data-file guard that works by
/// folder and extension, say) guards the copy too; and it never overwrites a file.
/// </summary>
public static class FileCopyWriter
{
    /// <summary>Writes a copy, unless the source changed since it was read or the copy's name breaks a rule.</summary>
    /// <param name="sourcePath">The file the data was read from.</param>
    /// <param name="loaded">The source's stamp when it was read.</param>
    /// <param name="copyPath">The copy's file name, or a path; a relative path is taken from the source's folder.</param>
    /// <param name="write">Writes the complete new content to the stream it is given.</param>
    /// <returns>The copy's full path.</returns>
    /// <exception cref="TableFileException">
    /// The source changed on disk, the copy would land in another folder, change the file type or overwrite a file.
    /// Nothing is written.
    /// </exception>
    public static string Write(string sourcePath, FileStamp loaded, string copyPath, Action<Stream> write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentException.ThrowIfNullOrWhiteSpace(copyPath);
        ArgumentNullException.ThrowIfNull(write);

        var source = Path.GetFullPath(sourcePath);
        var folder = Path.GetDirectoryName(source)!;
        var sourceName = Path.GetFileName(source);

        // Path.GetFullPath resolves "..", so a relative name that climbs out of the folder is caught below.
        var copy = Path.GetFullPath(copyPath.Trim(), folder);
        var copyName = Path.GetFileName(copy);

        if (!string.Equals(Path.GetDirectoryName(copy), folder, StringComparison.OrdinalIgnoreCase))
        {
            throw new TableFileException(
                $"saveAs must name a file in the same folder as {sourceName}, so the copy stays as protected as the source. Nothing was saved.");
        }

        var extension = Path.GetExtension(source);
        if (!string.Equals(Path.GetExtension(copy), extension, StringComparison.OrdinalIgnoreCase))
        {
            throw new TableFileException($"saveAs must end in {extension}, like {sourceName}. Nothing was saved.");
        }

        if (File.Exists(copy) || Directory.Exists(copy))
        {
            throw new TableFileException($"{copyName} already exists; Masque never overwrites a file. Choose another name. Nothing was saved.");
        }

        MasqueFileNames.EnsureUnchanged(source, loaded);

        var temporary = MasqueFileNames.TemporaryFor(copy);
        try
        {
            MasqueFileNames.WriteTemporary(temporary, write);

            // overwrite: false, so a file created by someone else in the meantime is never replaced.
            File.Move(temporary, copy, overwrite: false);
            return copy;
        }
        catch (IOException ex) when (ex is not (FileNotFoundException or DirectoryNotFoundException))
        {
            throw new TableFileException($"Could not save {copyName} (the name may have just been taken, or the folder is locked). Nothing was saved.");
        }
        finally
        {
            // Gone already after a successful move; removed here after any failure.
            File.Delete(temporary);
        }
    }
}
