using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace BlankUpdater
{
    /// <summary>
    /// Downloads over HTTPS. Uses .NET's HttpClient first and falls back to Windows' curl.exe
    /// if the game's runtime cannot open the connection.
    /// </summary>
    internal static class Downloader
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
        private static HttpClient _client;

        public static byte[] Get(string url, Action<string> logWarning, Action<long, long> progress = null)
        {
            try
            {
                if (_client == null)
                {
                    _client = new HttpClient { Timeout = Timeout };
                    _client.DefaultRequestHeaders.UserAgent.ParseAdd($"BlankUpdater/{BuildInfo.Version}");
                }
                using (var response = _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
                {
                    response.EnsureSuccessStatusCode();
                    long total = response.Content.Headers.ContentLength ?? -1;
                    using (var stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                    using (var buffer = new MemoryStream(total > 0 ? (int)total : 64 * 1024))
                    {
                        var chunk = new byte[64 * 1024];
                        int read;
                        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                        {
                            buffer.Write(chunk, 0, read);
                            progress?.Invoke(buffer.Length, total);
                        }
                        return buffer.ToArray();
                    }
                }
            }
            catch (Exception ex)
            {
                logWarning?.Invoke($"HttpClient failed ({ex.GetBaseException().Message}), trying curl.exe");
            }

            return GetWithCurl(url);
        }

        private static byte[] GetWithCurl(string url)
        {
            string curl = Path.Combine(Environment.SystemDirectory, "curl.exe");
            if (!File.Exists(curl))
                throw new IOException("curl.exe not found, cannot download " + url);

            string temp = Path.GetTempFileName();
            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = curl,
                    Arguments = $"-fsSL --max-time {(int)Timeout.TotalSeconds} -A \"BlankUpdater/{BuildInfo.Version}\" -o \"{temp}\" \"{url}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                };
                using (var process = Process.Start(start))
                {
                    string error = process.StandardError.ReadToEnd();
                    if (!process.WaitForExit((int)Timeout.TotalMilliseconds + 5000))
                    {
                        try { process.Kill(); } catch { }
                        throw new TimeoutException("curl.exe timed out for " + url);
                    }
                    if (process.ExitCode != 0)
                        throw new IOException($"curl.exe failed ({process.ExitCode}): {error.Trim()}");
                }
                return File.ReadAllBytes(temp);
            }
            finally
            {
                try { File.Delete(temp); } catch { }
            }
        }
    }
}
