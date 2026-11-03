using Microsoft.Build.Framework;
using System.IO;
using System.Linq;
using System.Net.Http;
using System;

public sealed class DownloadWebGpuHeaderTask : Microsoft.Build.Utilities.Task
{
    [Required]
    public string HeaderUrl { get; set; }

    [Required]
    public string OutputFile { get; set; }

    public override bool Execute()
    {
        try {
            OutputFile = Path.GetFullPath(OutputFile);
            Directory.CreateDirectory(Path.GetDirectoryName(OutputFile));
            var temporary = OutputFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
                using (var input = client.GetStreamAsync(HeaderUrl).GetAwaiter().GetResult())
                using (var output = File.Create(temporary)) input.CopyTo(output);
                if (!File.ReadAllText(temporary).Contains("WGPUInstance"))
                    throw new InvalidDataException("Invalid WebGPU header.");
                if (!File.Exists(OutputFile)) File.Move(temporary, OutputFile);
                else if (!File.ReadAllBytes(temporary).SequenceEqual(File.ReadAllBytes(OutputFile)))
                    File.Replace(temporary, OutputFile, null);
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
}
