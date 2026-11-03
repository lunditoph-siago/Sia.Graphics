using Microsoft.Build.Framework;
using System.IO.Compression;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System;

public sealed class ExtractNativeArchiveTask : Microsoft.Build.Utilities.Task
{
    [Required]
    public string ArchiveFile { get; set; }

    [Required]
    public string DestinationDirectory { get; set; }

    [Required]
    public ITaskItem[] RequiredFiles { get; set; }

    public override bool Execute()
    {
        try {
            DestinationDirectory = Path.GetFullPath(DestinationDirectory);
            if (Directory.GetParent(DestinationDirectory) == null)
                throw new InvalidDataException("The archive output must not be a filesystem root.");
            DestinationDirectory = DestinationDirectory.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (IsComplete(DestinationDirectory)) return true;
            var parent = Path.GetDirectoryName(DestinationDirectory);
            Directory.CreateDirectory(parent);
            var extracted = Extract(ArchiveFile, parent);
            try {
                if (!IsComplete(extracted)) throw new InvalidDataException("Incomplete native archive: " + ArchiveFile);
                if (Directory.Exists(DestinationDirectory)) Directory.Delete(DestinationDirectory, true);
                Directory.Move(extracted, DestinationDirectory);
            }
            finally {
                Directory.Delete(Path.GetDirectoryName(extracted), true);
            }
            return true;
        }
        catch (Exception error) {
            Log.LogErrorFromException(error, true);
            return false;
        }
    }

    private static string Extract(string archive, string root)
    {
        var directory = Path.Combine(root, "extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try {
            using (var file = File.OpenRead(archive))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress)) {
                var tar = Type.GetType("System.Formats.Tar.TarFile, System.Formats.Tar", true);
                var method = tar.GetMethod(
                    "ExtractToDirectory", new[] { typeof(Stream), typeof(string), typeof(bool) });
                method.Invoke(null, new object[] { gzip, directory, true });
            }

            if (Directory.GetFiles(directory).Length != 0) throw new InvalidDataException("Unexpected archive layout.");
            return Directory.GetDirectories(directory).Single();
        }
        catch {
            Directory.Delete(directory, true);
            throw;
        }
    }

    private bool IsComplete(string directory) =>
        Directory.Exists(directory) && RequiredFiles.All(item => HasNonEmptyFile(directory, item.ItemSpec));

    private static bool HasNonEmptyFile(string directory, string pattern)
    {
        pattern = pattern.Replace('\\', '/');
        if (!pattern.Contains("*")) {
            var path = Path.Combine(directory, pattern.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(path) && new FileInfo(path).Length > 0;
        }
        var expression = "^" + Regex.Escape(pattern).Replace(@"\*", "[^/]*") + "$";
        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Any(path =>
            Regex.IsMatch(path.Substring(directory.Length + 1).Replace('\\', '/'), expression) &&
            new FileInfo(path).Length > 0);
    }
}
