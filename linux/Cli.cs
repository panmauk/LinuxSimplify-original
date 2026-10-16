using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using LinuxSimplify.Models;
using LinuxSimplify.Services;

namespace LinuxSimplify
{
    /// <summary>
    /// Headless command-line modes:
    ///   --version
    ///   --scan                  hardware scan + detected USB drives as JSON
    ///   --list-usb              USB drives only, JSON
    ///   --analyze               distro compatibility for this machine, JSON
    ///   --resolve [--check]     live URL resolution for every distro; --check also HEADs each ISO and looks up its checksum
    ///   --test-flash ISO FILE [HELPER]   exercise the flasher pipeline against a regular FILE (never a device)
    /// </summary>
    public static class Cli
    {
        public const int NotACliCommand = -1000;

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public static int Run(string[] args)
        {
            switch (args[0])
            {
                case "--version":
                    Console.WriteLine("linuxsimplify " + (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "1.0.0"));
                    return 0;

                case "--scan":
                {
                    var scanner = new HardwareScanner();
                    var hw = scanner.ScanHardware();
                    Console.WriteLine(JsonSerializer.Serialize(new { hardware = hw, usbDrives = scanner.GetUsbDrives() }, Json));
                    return 0;
                }

                case "--list-usb":
                    Console.WriteLine(JsonSerializer.Serialize(new HardwareScanner().GetUsbDrives(), Json));
                    return 0;

                case "--analyze":
                {
                    var hw = new HardwareScanner().ScanHardware();
                    var result = new CompatibilityAnalyzer(windowsMigration: false).AnalyzeCompatibility(hw);
                    Console.WriteLine(JsonSerializer.Serialize(result.Select(d => new { d.Name, d.CompatibilityStatus, d.Notes, d.DownloadUrl }), Json));
                    return 0;
                }

                case "--resolve":
                    return ResolveAsync(args.Contains("--check")).GetAwaiter().GetResult();

                case "--test-flash":
                    if (args.Length < 3) { Console.Error.WriteLine("usage: --test-flash ISO TARGET_FILE [HELPER_SCRIPT]"); return 2; }
                    return TestFlashAsync(args[1], args[2], args.Length > 3 ? args[3] : Path.Combine(AppContext.BaseDirectory, "linuxsimplify-flash")).GetAwaiter().GetResult();

                default:
                    return NotACliCommand;
            }
        }

        private static async Task<int> TestFlashAsync(string iso, string target, string helper)
        {
            if (target.StartsWith("/dev/")) { Console.Error.WriteLine("refusing: --test-flash is for regular files only"); return 2; }
            var progress = new SyncProgress<string>(m => Console.WriteLine("status: " + m));
            bool ok = await new UsbFlasher().FlashIsoToFileForTestingAsync(iso, target, helper, progress);
            Console.WriteLine(ok ? "RESULT: success" : "RESULT: failure");
            return ok ? 0 : 1;
        }

        /// <summary>IProgress that runs inline (Progress&lt;T&gt; posts to a SynchronizationContext, which a console app doesn't have).</summary>
        private sealed class SyncProgress<T> : IProgress<T>
        {
            private readonly Action<T> a;
            public SyncProgress(Action<T> a) { this.a = a; }
            public void Report(T value) { lock (this) a(value); }
        }

        private static async Task<int> ResolveAsync(bool check)
        {
            var resolved = await new DistroUrlResolver().ResolveAllAsync();
            // The analyzer's built-in list shows what the app falls back to when resolution fails
            var fallback = new CompatibilityAnalyzer(false).GetAllDistrosWithoutHardware().ToDictionary(d => d.Name);

            using (var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(40) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("LinuxSimplify/1.0");
                var dl = new IsoDownloader();
                int failures = 0;

                foreach (var kv in resolved.OrderBy(k => k.Key))
                {
                    var r = kv.Value;
                    Console.WriteLine($"== {kv.Key}: {(r.Resolved ? "resolved" : "NOT RESOLVED")}");
                    if (!r.Resolved) { failures++; Console.WriteLine($"   fallback: {fallback[kv.Key].DownloadUrl}"); continue; }

                    var targets = new List<(string label, string url, string sumUrl, string hash)> { ("iso", r.DownloadUrl, r.Sha256Url, r.ExpectedHash) };
                    if (!string.IsNullOrEmpty(r.NvidiaDownloadUrl)) targets.Add(("nvidia", r.NvidiaDownloadUrl, r.NvidiaSha256Url, r.NvidiaExpectedHash));

                    foreach (var t in targets)
                    {
                        Console.WriteLine($"   {t.label}: {t.url}");
                        Console.WriteLine($"   checksum: {(string.IsNullOrEmpty(t.hash) ? (string.IsNullOrEmpty(t.sumUrl) ? "(none)" : t.sumUrl) : "published directly by source")} [{r.ChecksumType}]");
                        if (!check) continue;

                        // 1) the ISO really exists (HEAD, following redirects)
                        try
                        {
                            using (var resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, t.url)))
                            {
                                long? len = resp.Content.Headers.ContentLength;
                                Console.WriteLine($"   HEAD -> {(int)resp.StatusCode} {resp.RequestMessage?.RequestUri?.Host} size={(len.HasValue ? (len.Value / 1048576) + " MiB" : "?")}");
                                if (!resp.IsSuccessStatusCode || (len ?? 0) < 100L * 1024 * 1024) { failures++; Console.WriteLine("   !! ISO not reachable / implausibly small"); }
                            }
                        }
                        catch (Exception ex) { failures++; Console.WriteLine("   !! HEAD failed: " + ex.Message); }

                        // 2) an expected checksum can be obtained for exactly this file name
                        string expected = null;
                        if (!string.IsNullOrEmpty(t.hash)) expected = t.hash;
                        else if (!string.IsNullOrEmpty(t.sumUrl))
                        {
                            string content = await dl.DownloadChecksumFileAsync(t.sumUrl);
                            expected = content == null ? null : IsoDownloader.FindExpectedHash(content, IsoDownloader.RemoteFileName(t.url), r.ChecksumType);
                        }
                        if (string.IsNullOrEmpty(expected)) { failures++; Console.WriteLine("   !! no checksum entry found for this ISO"); }
                        else Console.WriteLine($"   expected {r.ChecksumType}: {expected}");
                    }
                }
                Console.WriteLine(failures == 0 ? "ALL OK" : $"{failures} PROBLEM(S)");
                return failures == 0 ? 0 : 1;
            }
        }
    }
}
