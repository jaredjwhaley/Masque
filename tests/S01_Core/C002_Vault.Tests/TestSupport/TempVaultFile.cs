namespace Masque.Vault.Tests.TestSupport;

/// <summary>A unique vault path under the temp folder, deleted (with SQLite side files) on dispose.</summary>
internal sealed class TempVaultFile : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "masque-tests", $"{Guid.NewGuid():N}", "vault.db");

    public void Dispose()
    {
        var folder = System.IO.Path.GetDirectoryName(Path)!;
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
