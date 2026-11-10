using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ScreenLine
{
    internal sealed class UpdateRelease
    {
        public string Version, Notes, Url, DownloadUrl, Sha256;
        public long Size;
    }

    internal sealed class AppUpdates : IDisposable
    {
        public const string Repository = "https://github.com/Renly1994/PicRope";
        public const string Endpoint = "https://api.github.com/repos/Renly1994/PicRope/releases/latest";
        public const string BackupDownloadUrl = "https://pan.quark.cn/s/d1bdfe7b8d19";
        public static readonly string CurrentVersion = Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
            .Cast<AssemblyInformationalVersionAttribute>().Single().InformationalVersion;
        internal static readonly string Cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PicRope", "Updates");
        readonly HttpClient client;

        public AppUpdates() : this(new HttpClientHandler()) { }
        internal AppUpdates(HttpMessageHandler handler)
        {
            System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;
            client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PicRope/" + CurrentVersion);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }

        public async Task<UpdateRelease> CheckAsync(CancellationToken cancellation)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                using (var response = await client.GetAsync(Endpoint, timeout.Token).ConfigureAwait(false))
                {
                    if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
                    response.EnsureSuccessStatusCode();
                    return Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false), CurrentVersion);
                }
            }
        }

        internal static UpdateRelease Parse(string json, string current)
        {
            var release = new JavaScriptSerializer().Deserialize<ReleaseData>(json);
            if (release == null) throw new InvalidDataException("更新信息不完整。");
            if (release.draft || release.prerelease) return null;
            string version = (release.tag_name ?? "").TrimStart('v', 'V');
            if (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+$")) throw new InvalidDataException("更新版本号无效。");
            if (new Version(version) <= new Version(current)) return null;
            string file = "PicRope-" + version + "-windows-x64.zip";
            var asset = (release.assets ?? new AssetData[0]).SingleOrDefault(item => item.name == file);
            if (asset == null) throw new InvalidDataException("新版本暂未提供 Windows 安装包，请稍后重试。");
            string expected = Repository + "/releases/download/" + release.tag_name + "/" + file;
            if (asset.browser_download_url != expected || asset.size <= 0 || !Regex.IsMatch(asset.digest ?? "", @"^sha256:[a-fA-F0-9]{64}$"))
                throw new InvalidDataException("更新包地址或校验信息无效，请稍后重试。");
            return new UpdateRelease { Version = version, Notes = release.body ?? "", Url = Repository + "/releases/tag/" + release.tag_name,
                DownloadUrl = expected, Size = asset.size, Sha256 = asset.digest.Substring(7) };
        }

        public async Task<string> DownloadAsync(UpdateRelease release, IProgress<int> progress, CancellationToken cancellation)
        {
            string work = Path.Combine(Cache, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                string archive = Path.Combine(work, "update.zip");
                using (var response = await client.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    if (response.RequestMessage != null && response.RequestMessage.RequestUri.Scheme != "https")
                        throw new InvalidDataException("更新下载未使用安全连接。");
                    using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = File.Create(archive))
                    {
                        byte[] buffer = new byte[65536]; long total = 0; int length;
                        while ((length = await input.ReadAsync(buffer, 0, buffer.Length, cancellation).ConfigureAwait(false)) > 0)
                        {
                            total += length;
                            if (total > release.Size) throw new InvalidDataException("更新包大小不符合发布信息。");
                            await output.WriteAsync(buffer, 0, length, cancellation).ConfigureAwait(false);
                            progress.Report((int)(total * 100 / release.Size));
                        }
                        if (total != release.Size) throw new InvalidDataException("更新包未下载完整，请重试。");
                    }
                }
                await Task.Run(delegate
                {
                    cancellation.ThrowIfCancellationRequested();
                    using (var input = File.OpenRead(archive))
                    using (var hash = SHA256.Create())
                        if (!String.Equals(BitConverter.ToString(hash.ComputeHash(input)).Replace("-", ""), release.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("更新包校验失败，请重新下载。");
                    Extract(archive, Path.Combine(work, "contents"), release.Version);
                    cancellation.ThrowIfCancellationRequested();
                    File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "update.ps1"), Path.Combine(work, "apply-update.ps1"));
                }, cancellation).ConfigureAwait(false);
                return work;
            }
            catch { Discard(work); throw; }
        }

        internal static void Extract(string archive, string destination, string version)
        {
            string root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
            using (var zip = ZipFile.OpenRead(archive))
            {
                foreach (var entry in zip.Entries)
                {
                    string name = entry.FullName.Replace('\\', '/');
                    string file = Path.GetFullPath(Path.Combine(root, name));
                    if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase) || name.Contains(':'))
                        throw new InvalidDataException("更新包包含无效路径。");
                    if (!name.StartsWith("app/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.StartsWith("app/data/", StringComparison.OrdinalIgnoreCase) || name.StartsWith("app/test-data/", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("更新包不能覆盖用户图片和设置。");
                    if (String.IsNullOrEmpty(entry.Name)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    entry.ExtractToFile(file);
                }
            }
            string executable = Path.Combine(root, "app", "PicRope.exe");
            if (!File.Exists(executable) || FileVersionInfo.GetVersionInfo(executable).ProductVersion != version
                || !File.Exists(Path.Combine(root, "app", "editor", "index.html")) || !File.Exists(Path.Combine(root, "app", "update.ps1")))
                throw new InvalidDataException("更新包不完整或版本不匹配。");
        }

        public static void Install(string work)
        {
            var start = new ProcessStartInfo("powershell.exe") {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + Quote(Path.Combine(work, "apply-update.ps1"))
                    + " -WorkPath " + Quote(work) + " -TargetPath " + Quote(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'))
                    + " -ParentProcessId " + Process.GetCurrentProcess().Id
            };
            using (var process = Process.Start(start)) { if (process == null) throw new IOException("无法启动更新安装程序。"); }
        }

        public static void Discard(string work)
        {
            string path = Path.GetFullPath(work);
            if (!path.StartsWith(Path.GetFullPath(Cache) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
            try { if (Directory.Exists(path)) Directory.Delete(path, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        static string Quote(string value) { return "\"" + value + "\""; }
        public void Dispose() { client.Dispose(); }

        public sealed class ReleaseData
        {
            public string tag_name { get; set; }
            public string body { get; set; }
            public bool draft { get; set; }
            public bool prerelease { get; set; }
            public AssetData[] assets { get; set; }
        }
        public sealed class AssetData
        {
            public string name { get; set; }
            public string browser_download_url { get; set; }
            public string digest { get; set; }
            public long size { get; set; }
        }
    }
}
