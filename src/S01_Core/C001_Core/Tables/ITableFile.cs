namespace Masque.Core.Tables;

/// <summary>
/// A CSV or Excel file read as tables, which can save changes made to a copy of those tables back to the file.
/// </summary>
public interface ITableFile
{
    /// <summary>The file's full path as given when it was read.</summary>
    string Path { get; }

    /// <summary>The tables the file held when it was read, in file order.</summary>
    IReadOnlyList<TableData> Tables { get; }

    /// <summary>
    /// Applies the changes to the file. Everything is checked before the file is touched; the file is then replaced
    /// in one step, with the previous version kept beside it as a backup.
    /// </summary>
    /// <param name="changes">One entry per table in <see cref="Tables"/>, in the same order.</param>
    /// <returns>The path of the backup holding the previous version.</returns>
    /// <exception cref="TableFileException">
    /// The file changed on disk since it was read, or a change can't be saved safely. The file is left as it was.
    /// </exception>
    string Save(IReadOnlyList<TableChanges> changes);

    /// <summary>
    /// Writes the file with the changes applied to a new file beside it ("save as"), leaving this file untouched and
    /// making no backup. The same checks as <see cref="Save"/> run first; the copy's rules are
    /// <see cref="FileCopyWriter"/>'s (same folder, same extension, never overwrite).
    /// </summary>
    /// <param name="changes">One entry per table in <see cref="Tables"/>, in the same order.</param>
    /// <param name="copyPath">The copy's file name, or a path in this file's folder.</param>
    /// <returns>The copy's full path.</returns>
    /// <exception cref="TableFileException">
    /// This file changed on disk since it was read, a change can't be saved safely, or the copy's name breaks a rule.
    /// No file is written.
    /// </exception>
    string SaveCopy(IReadOnlyList<TableChanges> changes, string copyPath);
}

/// <summary>
/// A change couldn't be saved to a CSV or Excel file. The message names the file and the reason, never data, so it is
/// safe to show an AI assistant.
/// </summary>
/// <param name="message">What went wrong.</param>
public sealed class TableFileException(string message) : Exception(message);
