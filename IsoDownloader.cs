using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LinuxSimplify.Models;

namespace LinuxSimplify.Services
{
    public enum ChecksumStatus
    {
        Verified,     // checksum found and matches
        Mismatch,     // checksum found and does NOT match — file is corrupt or tampered with
        Unavailable   // nothing to compare against (no checksum published / could not fetch / no entry for this file)
    }

    public class IsoDownloader
    {
        // One shared client for the whole process (per-request clients exhaust sockets and skip connection reuse).
        // Timeout is infinite on purpose: the 4 hour value used before only covered the headers when
        // ResponseHeadersRead is used, so a stalled body would hang forever — stalls are caught per read instead.
        private static readonly HttpClient httpClient = CreateClient();
        private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);
        private const int BufferSize = 1024 * 1024;

        private static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            var c = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            // An honest, non-browser User-Agent: dl.fedoraproject.org puts browser-like clients ("Mozilla/5.0") behind an
            // anti-bot JavaScript challenge and answers with an HTML page instead of the CHECKSUM file.
            c.DefaultRequestHeaders.UserAgent.ParseAdd("LinuxSimplify/1.0");
            c.DefaultRequestHeaders.Add("Accept", "*/*");
            return c;
        }

        private sealed class DiskException : Exception
        {
            public DiskException(string m, Exception inner) : base(m, inner) { }
        }

        private static DownloadState Clone(DownloadState s) => new DownloadState
        {
            CurrentAction = s.CurrentAction,
            BytesDownloaded = s.BytesDownloaded,
            TotalBytes = s.TotalBytes,
            Percentage = s.Percentage,
            ErrorMessage = s.ErrorMessage,
            LastSuccessfulAction = s.LastSuccessfulAction
        };

        /// <summary>
        /// Downloads <paramref name="url"/> to <paramref name="destinationPath"/>.
        /// Data goes to "&lt;destination&gt;.part" first and is renamed only when complete, so a file with the
        /// final name is never a truncated download. Network drops/stalls are retried and resumed (HTTP Range).
        /// Throws OperationCanceledException when <paramref name="cancellationToken"/> is cancelled
        /// (the partial file is removed).
        /// </summary>
        public async Task<bool> DownloadIsoAsync(string url, string destinationPath, IProgress<DownloadState> progressReporter, CancellationToken cancellationToken)
        {
            var state = new DownloadState { CurrentAction = "Checking disk space..." };
            void Report() => progressReporter?.Report(Clone(state));
            Report();

            string fullDest = Path.GetFullPath(destinationPath);
            string partPath = fullDest + ".part";
            string dir = Path.GetDirectoryName(fullDest);

            try { Directory.CreateDirectory(dir); }
            catch (Exception ex)
            {
                state.ErrorMessage = $"Couldn't create download folder: {ex.Message}";
                state.CurrentAction = "Download failed";
                Report();
                return false;
            }

            const int MaxAttempts = 4;
            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    state.CurrentAction = attempt == 0 ? "Connecting..." : $"Connecting... (retry {attempt})";
                    Report();

                    long existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

                    using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);

                        using (var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
                        {
                            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                            {
                                // Our partial file is unusable (or already complete but unrenamed) — start over
                                TryDelete(partPath);
                                continue;
                            }

                            if (!response.IsSuccessStatusCode)
                            {
                                state.ErrorMessage = $"Server returned {(int)response.StatusCode}";
                                state.CurrentAction = "Server error";
                                Report();
                                int code = (int)response.StatusCode;
                                bool retryable = code >= 500 || code == 408 || code == 429;
                                if (!retryable || attempt == MaxAttempts - 1) { TryDelete(partPath); return false; }
                                await Task.Delay(3000, cancellationToken);
                                continue;
                            }

                            // 206 = server honoured the Range; anything else means "here's the whole file"
                            bool resumed = response.StatusCode == HttpStatusCode.PartialContent
                                           && response.Content.Headers.ContentRange?.From == existing;
                            if (!resumed) existing = 0;

                            // With a transfer-encoding like gzip the announced length is the compressed size — can't be compared with bytes read
                            long? contentLength = response.Content.Headers.ContentEncoding.Count > 0 ? null : response.Content.Headers.ContentLength;
                            long total = contentLength.HasValue ? existing + contentLength.Value : -1;

                            // Disk space: now that the real size is known, require exactly what is still missing (+ margin).
                            // Without a size, fall back to a conservative 6 GB.
                            long needed = total > 0 ? total - existing + 64L * 1024 * 1024 : 6L * 1024 * 1024 * 1024;
                            long free = GetFreeSpace(dir);
                            if (free >= 0 && free < needed)
                            {
                                state.ErrorMessage = $"Need about {FormatBytes(needed)} free, but only {FormatBytes(free)} is available in {dir}";
                                state.CurrentAction = "Insufficient disk space";
                                Report();
                                TryDelete(partPath);
                                return false;
                            }
                            if (free >= 0) state.LastSuccessfulAction = $"{FormatBytes(free)} available";

                            state.TotalBytes = total;
                            state.CurrentAction = "Downloading...";
                            state.LastSuccessfulAction = resumed ? "Resumed" : "Connected";
                            Report();

                            long totalRead = existing;
                            using (var contentStream = await response.Content.ReadAsStreamAsync())
                            using (var fileStream = OpenPart(partPath, resumed))
                            {
                                var buffer = new byte[BufferSize];
                                using (var stallCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                                {
                                    while (true)
                                    {
                                        stallCts.CancelAfter(StallTimeout);
                                        int bytesRead;
                                        try { bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, stallCts.Token); }
                                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                                        {
                                            throw new IOException("The connection stalled (no data for 30 seconds)");
                                        }
                                        if (bytesRead <= 0) break;

                                        try { await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken); }
                                        catch (IOException ex) { throw new DiskException($"Couldn't write to disk: {ex.Message}", ex); }

                                        totalRead += bytesRead;
                                        state.BytesDownloaded = totalRead;
                                        state.Percentage = total > 0 ? (int)Math.Min(100, (totalRead * 100) / total) : 0;
                                        state.CurrentAction = total > 0
                                            ? $"Downloading... {FormatBytes(totalRead)} / {FormatBytes(total)}"
                                            : $"Downloading... {FormatBytes(totalRead)}";
                                        Report();
                                    }
                                }
                                try { await fileStream.FlushAsync(cancellationToken); }
                                catch (IOException ex) { throw new DiskException($"Couldn't write to disk: {ex.Message}", ex); }
                            }

                            // A connection that closes early looks like a normal end of stream — compare against the announced size
                            if (total > 0 && totalRead != total)
                                throw new IOException($"Download ended early ({FormatBytes(totalRead)} of {FormatBytes(total)})");
                        }
                    }

                    var fi = new FileInfo(partPath);
                    if (!fi.Exists || fi.Length == 0)
                    {
                        if (attempt < MaxAttempts - 1) continue;
                        state.ErrorMessage = "The server sent an empty file";
                        state.CurrentAction = "Download failed";
                        Report();
                        return false;
                    }

                    try { File.Move(partPath, fullDest, true); }
                    catch (IOException ex)
                    {
                        state.ErrorMessage = $"Couldn't save the ISO: {ex.Message}";
                        state.CurrentAction = "Download failed";
                        Report();
                        return false;
                    }

                    state.Percentage = 100;
                    state.LastSuccessfulAction = $"Downloaded {FormatBytes(fi.Length)}";
                    state.CurrentAction = "Downloaded";
                    Report();
                    return true;
                }
                catch (OperationCanceledException)
                {
                    TryDelete(partPath);
                    throw;
                }
                catch (DiskException ex)
                {
                    state.ErrorMessage = ex.Message;
                    state.CurrentAction = "Download failed";
                    Report();
                    return false; // retrying a full / read-only disk is pointless; keep the partial data for the next try
                }
                catch (Exception ex)
                {
                    // Network trouble (reset, stall, DNS, early EOF): keep the .part file and resume
                    state.ErrorMessage = ex.Message;
                    Report();
                    if (attempt < MaxAttempts - 1)
                    {
                        await Task.Delay(3000, cancellationToken);
                        continue;
                    }
                    state.CurrentAction = "Download failed";
                    Report();
                    TryDelete(partPath);
                    return false;
                }
            }

            state.CurrentAction = "Download failed";
            Report();
            TryDelete(partPath);
            return false;
        }

        private static FileStream OpenPart(string path, bool append)
        {
            return new FileStream(path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, true);
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        /// <summary>Free bytes on the filesystem that holds <paramref name="directory"/>, or -1 if unknown.</summary>
        private static long GetFreeSpace(string directory)
        {
            try
            {
                // On Windows DriveInfo wants a drive root; on Unix any path works (statfs) and
                // Path.GetPathRoot would be "/" — i.e. the wrong filesystem when ~/Downloads is on its own mount.
                string path = OperatingSystem.IsWindows() ? Path.GetPathRoot(directory) : directory;
                return new DriveInfo(path).AvailableFreeSpace;
            }
            catch { return -1; }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        }

        /// <summary>Downloads a (small) checksum file. Returns null if it can't be fetched after a few tries.</summary>
        public async Task<string> DownloadChecksumFileAsync(string url)
        {
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                    using (var resp = await httpClient.GetAsync(url, cts.Token))
                    {
                        // A checksum file is plain text. An HTML answer is a captcha / challenge / error page, not data.
                        string type = resp.Content.Headers.ContentType?.MediaType ?? "";
                        if (resp.IsSuccessStatusCode && !type.Equals("text/html", StringComparison.OrdinalIgnoreCase))
                            return await resp.Content.ReadAsStringAsync();
                    }
                }
                catch { }
                if (i < 2) await Task.Delay(1000 * (i + 1));
            }
            return null;
        }

        public async Task<string> DownloadSha256FileAsync(string url)
        {
            return await DownloadChecksumFileAsync(url);
        }

        public async Task<string> ComputeChecksumAsync(string filePath, string algorithm, IProgress<string> statusReporter)
        {
            return await Task.Run(() =>
            {
                try
                {
                    statusReporter?.Report("Verifying file integrity...");
                    using (HashAlgorithm hasher = string.Equals(algorithm, "sha512", StringComparison.OrdinalIgnoreCase)
                               ? (HashAlgorithm)SHA512.Create() : SHA256.Create())
                    using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize))
                        return Convert.ToHexString(hasher.ComputeHash(stream)).ToLowerInvariant();
                }
                catch { return null; }
            });
        }

        public async Task<string> ComputeSha256Async(string filePath, IProgress<string> statusReporter)
        {
            return await ComputeChecksumAsync(filePath, "sha256", statusReporter);
        }

        /// <summary>
        /// Verifies a downloaded ISO. <paramref name="expectedHash"/> (published directly by the source) wins over the
        /// checksum file at <paramref name="checksumUrl"/>. The file name used to look up the entry is the one in
        /// <paramref name="downloadUrl"/>, not the local file name.
        /// </summary>
        public async Task<ChecksumStatus> VerifyIsoAsync(string isoPath, string downloadUrl, string checksumUrl, string checksumType, string expectedHash)
        {
            string algo = string.IsNullOrEmpty(checksumType) ? "sha256" : checksumType;
            string expected = null;

            if (!string.IsNullOrWhiteSpace(expectedHash))
            {
                expected = expectedHash.Trim().ToLowerInvariant();
            }
            else if (!string.IsNullOrWhiteSpace(checksumUrl))
            {
                string content = await DownloadChecksumFileAsync(checksumUrl);
                if (content == null) return ChecksumStatus.Unavailable;
                expected = FindExpectedHash(content, RemoteFileName(downloadUrl), algo);
            }
            if (string.IsNullOrEmpty(expected)) return ChecksumStatus.Unavailable;

            string actual = await ComputeChecksumAsync(isoPath, algo, null);
            if (actual == null) return ChecksumStatus.Unavailable;
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase) ? ChecksumStatus.Verified : ChecksumStatus.Mismatch;
        }

        /// <summary>File name part of a URL, URL-decoded ("https://x/y/a%20b.iso" -> "a b.iso").</summary>
        public static string RemoteFileName(string url)
        {
            try
            {
                var uri = new Uri(url);
                return Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
            }
            catch { return Path.GetFileName(url ?? ""); }
        }

        private static readonly Regex BsdLine = new Regex(@"^\s*(?:SHA-?\d+|MD5|BLAKE2\w*)\s*\(\s*(?<name>.+?)\s*\)\s*=\s*(?<hash>[0-9a-fA-F]{32,128})\s*$", RegexOptions.IgnoreCase);
        private static readonly Regex GnuLine = new Regex(@"^\s*(?<hash>[0-9a-fA-F]{32,128})\s+[ *]?(?<name>\S.*?)\s*$");
        private static readonly Regex BareLine = new Regex(@"^\s*(?<hash>[0-9a-fA-F]{32,128})\s*$");

        /// <summary>
        /// Finds the hash for <paramref name="fileName"/> in a checksum file. Understands GNU format
        /// ("hash  file" / "hash *file"), BSD format ("SHA256 (file) = hash", as used by Fedora, including the
        /// PGP clear-signing wrapper) and a lone bare hash. Hash length must match the algorithm
        /// (64 hex chars for sha256, 128 for sha512). Returns lowercase hex, or null if no entry matches.
        /// </summary>
        public static string FindExpectedHash(string checksumFileContent, string fileName, string algorithm = "sha256")
        {
            if (string.IsNullOrWhiteSpace(checksumFileContent) || string.IsNullOrWhiteSpace(fileName)) return null;
            int hexLen = string.Equals(algorithm, "sha512", StringComparison.OrdinalIgnoreCase) ? 128 : 64;
            fileName = Path.GetFileName(fileName.Replace('\\', '/'));

            var bare = new List<string>();
            int named = 0;
            foreach (var raw in checksumFileContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("-----") || line.StartsWith("Hash:")) continue;

                Match m = BsdLine.Match(line);
                if (!m.Success) m = GnuLine.Match(line);
                if (m.Success)
                {
                    string hash = m.Groups["hash"].Value;
                    if (hash.Length != hexLen) continue;
                    named++;
                    string name = Path.GetFileName(m.Groups["name"].Value.Trim().Replace('\\', '/'));
                    if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
                        return hash.ToLowerInvariant();
                    continue;
                }

                m = BareLine.Match(line);
                if (m.Success && m.Groups["hash"].Value.Length == hexLen) bare.Add(m.Groups["hash"].Value.ToLowerInvariant());
            }

            // A checksum file that is nothing but one anonymous hash (single-file ".sha256" sidecars)
            if (named == 0 && bare.Count == 1) return bare[0];
            return null;
        }

        /// <summary>
        /// True only if the checksum file has an entry for <paramref name="isoFileName"/> and it equals
        /// <paramref name="computedHash"/> (case-insensitive). No "hash appears anywhere" fallback.
        /// </summary>
        public bool VerifySha256(string computedHash, string checksumFileContent, string isoFileName)
        {
            if (string.IsNullOrWhiteSpace(computedHash)) return false;
            string expected = FindExpectedHash(checksumFileContent, isoFileName, computedHash.Length == 128 ? "sha512" : "sha256");
            return expected != null && string.Equals(expected, computedHash.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
