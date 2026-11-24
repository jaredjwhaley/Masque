using Masque.Core.Tables;

namespace Masque.Core.Tests.Tables;

/// <summary>
/// Pins down how Masque puts changed data on disk. A copy ("save as") never touches the source, never overwrites a
/// file and stays in the source's folder, so a data-file guard that covers the source covers the copy too. Backups
/// and temporary files keep their data file's extension for the same reason, and are recognised as Masque's own
/// files so a folder scan never loads them as tables.
/// </summary>
public sealed class FileSavingTests : IDisposable
{
    private const string Original = "Id,Name\n1,Ana\n";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "masque-tests", $"{Guid.NewGuid():N}");
    private readonly string _source;

    public FileSavingTests()
    {
        Directory.CreateDirectory(_folder);
        _source = Path.Combine(_folder, "people.csv");
        File.WriteAllText(_source, Original);
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static void WriteNew(Stream stream) => stream.Write("Id,Name\n1,Bo\n"u8);

    /// <summary>A copy holds the new content at the full path returned; the source keeps its own content.</summary>
    [Fact]
    public void WriteCopy_WritesTheCopyAndLeavesTheSource()
    {
        var target = Path.Combine(_folder, "people-new.csv");

        var copy = FileCopyWriter.Write(_source, FileStamp.Of(_source), target, WriteNew);

        copy.ShouldBe(target);
        File.ReadAllText(copy).ShouldBe("Id,Name\n1,Bo\n");
        File.ReadAllText(_source).ShouldBe(Original);
        Directory.GetFiles(_folder).Length.ShouldBe(2);
    }

    /// <summary>A bare file name is placed in the source's folder.</summary>
    [Fact]
    public void WriteCopy_BareName_LandsBesideTheSource()
    {
        var copy = FileCopyWriter.Write(_source, FileStamp.Of(_source), "people-new.csv", WriteNew);

        copy.ShouldBe(Path.Combine(_folder, "people-new.csv"));
        File.Exists(copy).ShouldBeTrue();
    }

    /// <summary>An existing file, the source included, is never overwritten; it keeps its content.</summary>
    [Theory]
    [InlineData("taken.csv")]
    [InlineData("people.csv")]
    public void WriteCopy_ExistingFile_IsRefused(string name)
    {
        var target = Path.Combine(_folder, name);
        if (!File.Exists(target))
        {
            File.WriteAllText(target, "someone's file");
        }

        var before = File.ReadAllText(target);

        Should.Throw<TableFileException>(() => FileCopyWriter.Write(_source, FileStamp.Of(_source), target, WriteNew))
            .Message.ShouldContain("already exists");
        File.ReadAllText(target).ShouldBe(before);
    }

    /// <summary>
    /// A copy outside the source's folder is refused, whether given as a full path or as a relative path that climbs
    /// out, so it can't land somewhere a data-file guard treats as safe to read.
    /// </summary>
    [Theory]
    [InlineData("other")]
    [InlineData("..")]
    public void WriteCopy_OtherFolder_IsRefused(string relativeFolder)
    {
        var elsewhere = Path.Combine(_folder, "other");
        Directory.CreateDirectory(elsewhere);
        var target = relativeFolder == "other" ? Path.Combine(elsewhere, "copy.csv") : Path.Combine("..", "copy.csv");

        Should.Throw<TableFileException>(() => FileCopyWriter.Write(_source, FileStamp.Of(_source), target, WriteNew))
            .Message.ShouldContain("same folder");
        Directory.GetFiles(elsewhere).ShouldBeEmpty();
        File.Exists(Path.Combine(Path.GetDirectoryName(_folder)!, "copy.csv")).ShouldBeFalse();
    }

    /// <summary>A copy must keep the source's file type, so a CSV can't be saved under an .xlsx name or the reverse.</summary>
    [Fact]
    public void WriteCopy_OtherExtension_IsRefused() =>
        Should.Throw<TableFileException>(() => FileCopyWriter.Write(_source, FileStamp.Of(_source), "people.txt", WriteNew))
            .Message.ShouldContain(".csv");

    /// <summary>A source changed on disk since it was read is not copied, since the changes were worked out on the old content.</summary>
    [Fact]
    public void WriteCopy_SourceChangedOnDisk_IsRefused()
    {
        var stamp = FileStamp.Of(_source);
        File.AppendAllText(_source, "2,Cy\n");

        Should.Throw<TableFileException>(() => FileCopyWriter.Write(_source, stamp, "people-new.csv", WriteNew))
            .Message.ShouldContain("changed");
        File.Exists(Path.Combine(_folder, "people-new.csv")).ShouldBeFalse();
    }

    /// <summary>A write that fails part way leaves no copy and no temporary file behind.</summary>
    [Fact]
    public void WriteCopy_FailedWrite_LeavesNothing()
    {
        Should.Throw<InvalidOperationException>(() => FileCopyWriter.Write(
            _source, FileStamp.Of(_source), "people-new.csv", _ => throw new InvalidOperationException("boom")));

        Directory.GetFiles(_folder).ShouldBe([_source]);
    }

    /// <summary>
    /// A backup ends in its data file's extension ("people.20261124-153000.masque-backup.csv"), so a guard that
    /// protects data files by extension protects backups too.
    /// </summary>
    [Fact]
    public void Replace_BackupKeepsTheDataExtension()
    {
        var backup = FileReplacer.Replace(_source, FileStamp.Of(_source), WriteNew);

        Path.GetDirectoryName(backup).ShouldBe(_folder);
        Path.GetFileName(backup).ShouldStartWith("people.");
        backup.ShouldEndWith(".masque-backup.csv");
        File.ReadAllText(backup).ShouldBe(Original);
        File.ReadAllText(_source).ShouldBe("Id,Name\n1,Bo\n");
    }

    /// <summary>Masque's backups and temporary save files, in the old and new naming, are recognised; data files are not.</summary>
    [Theory]
    [InlineData("people.20261124-153000.masque-backup.csv", true)]
    [InlineData("people.20261124-153000-2.masque-backup.xlsx", true)]
    [InlineData("people.csv.20261120-153000.masque-backup", true)]
    [InlineData(".people.0123abcd.masque-saving.csv", true)]
    [InlineData("people.csv", false)]
    [InlineData("masque-backups-list.xlsx", false)]
    public void IsWorkFile_RecognisesBackupsAndTemporaryFiles(string name, bool expected) =>
        MasqueFileNames.IsWorkFile(Path.Combine(_folder, name)).ShouldBe(expected);
}
