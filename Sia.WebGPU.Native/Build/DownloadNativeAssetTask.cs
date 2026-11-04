using Microsoft.Build.Framework;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System;

public sealed class DownloadNativeAssetTask : Microsoft.Build.Utilities.Task
{
    [Required]
    public string SourceUrl { get; set; }

    [Required]
    public string Digest { get; set; }

    [Required]
    public string DestinationFile { get; set; }

    public override bool Execute()
    {
        try {
            if (!Regex.IsMatch(Digest, "^sha256:[a-f0-9]{64}$"))
                throw new InvalidDataException("Invalid release asset digest.");
            DestinationFile = Path.GetFullPath(DestinationFile);
            var expected = Digest.Substring(7);
            if (File.Exists(DestinationFile) && Hash(DestinationFile) == expected) return true;
            Directory.CreateDirectory(Path.GetDirectoryName(DestinationFile));
            var temporary = DestinationFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) }) {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("Sia.WebGPU");
                    using (var input = client.GetStreamAsync(SourceUrl).GetAwaiter().GetResult())
                    using (var output = File.Create(temporary)) input.CopyTo(output);
                }
                if (Hash(temporary) != expected) throw new InvalidDataException("Checksum mismatch: " + SourceUrl);
                if (File.Exists(DestinationFile)) File.Replace(temporary, DestinationFile, null);
                else {
                    try { File.Move(temporary, DestinationFile); }
                    catch (IOException) when (File.Exists(DestinationFile) && Hash(DestinationFile) == expected) { }
                }
            }
            finally {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return true;
        }
        catch (Exception error) {
            Log.LogErrorFromException(error, true);
            return false;
        }
    }

    private static string Hash(string path)
    {
        using (var stream = File.OpenRead(path))
        using (var sha = SHA256.Create()) {
            var bytes = sha.ComputeHash(stream);
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }
    }
}
