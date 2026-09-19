namespace Cdmw.ArchiveLite.Core;

/// <summary>Publishes a prepared mesh and its companions as one rollback-capable operation.</summary>
internal static class NativeExportPublication
{
    public static void Publish(
        IReadOnlyList<(string Staged, string Destination)> files,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (staged, destination) in files)
        {
            if (!File.Exists(staged)) throw new IOException($"Export output is missing: {staged}");
            if (!destinations.Add(Path.GetFullPath(destination)))
                throw new IOException($"Export outputs share a destination: {destination}");
            if (Directory.Exists(destination) || (!overwrite && File.Exists(destination)))
                throw new IOException($"Export destination already exists: {destination}");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var backupRoot = Path.Combine(Path.GetDirectoryName(files[0].Destination)!, $".cdmw-rollback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(backupRoot);
        var backups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var published = new List<string>();
        var keepBackups = false;
        try
        {
            foreach (var (_, destination) in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(destination)) continue;
                var backup = Path.Combine(backupRoot, backups.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                File.Copy(destination, backup);
                backups.Add(destination, backup);
            }
            // Cancellation cannot split the publication after this point.
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var (staged, destination) in files)
            {
                File.Move(staged, destination, overwrite);
                published.Add(destination);
            }
        }
        catch (Exception failure)
        {
            var errors = new List<Exception>();
            foreach (var destination in published.AsEnumerable().Reverse())
            {
                try
                {
                    if (backups.TryGetValue(destination, out var backup)) File.Move(backup, destination, true);
                    else File.Delete(destination);
                }
                catch (Exception restoreFailure) when (restoreFailure is IOException or UnauthorizedAccessException)
                {
                    errors.Add(restoreFailure);
                }
            }
            if (errors.Count > 0)
            {
                keepBackups = true;
                errors.Insert(0, failure);
                throw new IOException($"Export publication failed; recovery files were preserved at {backupRoot}.", new AggregateException(errors));
            }
            throw;
        }
        finally
        {
            if (!keepBackups)
            {
                try { Directory.Delete(backupRoot, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
