namespace Dashboard.Tests.Auditor;

/// <summary>Disposable temp directory with helpers to lay out fixture repos.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir(string? prefix = null)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), (prefix ?? "auditor-test-") + Guid.NewGuid().ToString("n")[..10]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Sub(string relative)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(full);
        return full;
    }

    public string Write(string relative, string content = "")
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
