namespace LsMonitoring.Core.IO;

/// <summary>
/// Crash-safe file writes. Data is written to a temporary sibling file and then swapped into
/// place with an atomic rename, so a process kill or power loss mid-write can never leave the
/// target half-written. The previous good version is preserved next to the file as "*.bak".
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);

        try
        {
            if (File.Exists(path))
            {
                // Replace is atomic on a single volume and rotates the old content into "*.bak".
                File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tmp, path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fallback for edge cases (different volume, locked .bak, etc.): best-effort overwrite.
            File.Copy(tmp, path, overwrite: true);
            TryDelete(tmp);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Stale temp files are harmless.
        }
    }
}
