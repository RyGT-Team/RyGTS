// SPDX-License-Identifier: GPL-2.0-or-later
// FEAT-GTS-272: the launcher's updater. Three channels: Stable (the newest GitHub release), Prerelease
// (the newest GitHub release, pre-releases included) and Nightly (the newest successful CI build,
// downloaded through nightly.link because GitHub's own artifact download needs a signed-in
// account). It checks the package and installs its program files over this folder, keeping a
// backup to roll back to. User data is never written. The same file is in RyGTS and ryGT7; only the
// settings block below differs.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace RyGT
{
    /// A build the launcher can update to.
    class ReleaseInfo
    {
        /// "2026-11-10" for a release, "ci-1a2b3c4d" for a nightly.
        public string Version, Tag, Name, Notes, ZipName, ZipUrl, Sha256;
        /// A nightly: the download is a GitHub Actions artifact (a zip holding the package zip and
        /// its .sha256), which also gives the checksum.
        public bool Artifact;
        /// A GitHub pre-release.
        public bool Prerelease;

        /// The first lines of the notes' "What's new" (or of the notes), for the home view.
        public string Summary(int maxLength)
        {
            string text = Notes ?? "";
            int at = text.IndexOf("What's new", StringComparison.OrdinalIgnoreCase);
            if (at >= 0) text = text.Substring(at + 10);
            var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("- ") || l.StartsWith("* ")).ToList();
            string summary = string.Join(" " + (char)183 + " ", lines.Select(l => Regex.Replace(l.Substring(2), @"\*\*|`", "")));
            if (summary.Length == 0) summary = Regex.Replace(text, @"\s+|\*\*|`|#", " ").Trim();
            return summary.Length <= maxLength ? summary : summary.Substring(0, maxLength - 1).TrimEnd() + (char)0x2026;
        }
    }

    static class Updater
    {
        // ---- This launcher (the rest of the file is the same in RyGTS and ryGT7).
        const string Repository = "RyGT-Team/RyGTS", Workflow = "ci-package-release.yml", Branch = "development";
        const string ArtifactPrefix = "ci-", NightlyPrefix = "RyGTS-ci-", PackagePrefix = "RyGTS-", TagPrefix = "gts-";
        const string Product = "RyGTS", LauncherExe = "RyGTS.exe", GameProcess = "GTSport";
        static readonly string[] ProtectedFiles = { "RyGT.ini", "GTSport.exe", "GTSport.exe.converted", "setup-out.txt" };
        // ----

        public const string Stable = "stable", Prerelease = "prerelease", Nightly = "nightly";
        public static readonly string[] Channels = { Stable, Prerelease, Nightly };

        // Folders and files that hold the player's data or the converted game: an update never
        // writes, moves or deletes anything under them, whatever a package lists.
        static readonly string[] ProtectedFolders = { "user", "shaders", "app0", "sys", "_sd", "convert", "data", "logs", "temp",
                                                       "temp0", "download0", "update", "dlss", "Photos", "screenshots", "decals",
                                                       "launcher\\profiles" };
        // Written last, so a failed update still reports the old version.
        static readonly string[] VersionFiles = { "BUILD_INFO.txt", "BUILD_INFO.json", "manifest.json", "SHA256SUMS.txt" };

        public static string UpdateFolder(string root) { return Path.Combine(root, "update"); }

        /// The installed version: the version this updater installed (update\installed.json), while
        /// the installed build is still the one it recorded (a release can be a nightly repackaged,
        /// whose manifest still says "RyGT7-ci-..."); else a package's manifest.json name
        /// ("RyGT7-2026-11-10", "RyGT7-ci-1a2b3c4d"), or BUILD_INFO.txt's commit as a nightly. Null
        /// for a build that didn't come from a package.
        public static string InstalledVersion(string root)
        {
            try
            {
                string identity = BuildIdentity(root);
                if (identity == null) return null;
                string recordPath = Path.Combine(UpdateFolder(root), "installed.json");
                if (File.Exists(recordPath))
                {
                    var record = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(recordPath)) as Dictionary<string, object>;
                    object version, recorded;
                    if (record != null && record.TryGetValue("version", out version) && record.TryGetValue("build", out recorded) &&
                        string.Equals(recorded as string, identity, StringComparison.OrdinalIgnoreCase) && version is string)
                        return (string)version;
                }
                if (identity.StartsWith("commit:")) return "ci-" + identity.Substring(7, 8);
                var match = Regex.Match(identity, "^" + Regex.Escape(Product) + @"-(.+?)(-prerelease)?$");
                return match.Success ? match.Groups[1].Value : null;
            }
            catch { return null; }
        }

        /// What tells installed builds apart: manifest.json's name, else "commit:" + BUILD_INFO.txt's
        /// commit. Null when there is neither.
        static string BuildIdentity(string root)
        {
            try
            {
                string manifestPath = Path.Combine(root, "manifest.json");
                if (File.Exists(manifestPath))
                {
                    var manifest = new JavaScriptSerializer { MaxJsonLength = 16 << 20 }.DeserializeObject(File.ReadAllText(manifestPath)) as Dictionary<string, object>;
                    object name;
                    if (manifest != null && manifest.TryGetValue("name", out name) && !string.IsNullOrEmpty(name as string)) return (string)name;
                }
            }
            catch { }
            string commit = BuildCommit(root);
            return commit != null ? "commit:" + commit : null;
        }

        /// BUILD_INFO.txt's commit (CI and release packages built by the pipeline), or null.
        static string BuildCommit(string root)
        {
            try
            {
                string path = Path.Combine(root, "BUILD_INFO.txt");
                if (!File.Exists(path)) return null;
                var info = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
                object commit;
                if (info == null || !info.TryGetValue("commit", out commit)) return null;
                string text = commit as string ?? "";
                return Regex.IsMatch(text, "^[0-9a-f]{8,40}$") ? text : null;
            }
            catch { return null; }
        }

        /// A version as shown to the player: a nightly ("ci-1a2b3c4d") as "nightly 1a2b3c4d".
        public static string Display(string version)
        {
            if (version == null) return "";
            return version.StartsWith("ci-") ? "nightly " + version.Substring(3) : version;
        }

        /// Releases are dates with an optional letter for a hotfix (2026-10-27b), so they sort as
        /// text. A nightly is offered when it is another commit than the installed build (the newest
        /// successful run is the one offered), and a release over a nightly and the other way round.
        public static bool IsNewer(string candidate, string installed)
        {
            if (installed == null || candidate == null) return false;
            if (candidate.StartsWith("ci-") || installed.StartsWith("ci-")) return !string.Equals(candidate, installed, StringComparison.Ordinal);
            return string.CompareOrdinal(candidate, installed) > 0;
        }

        static WebClient Client()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var client = new WebClient();
            client.Headers[HttpRequestHeader.UserAgent] = Product + "-launcher";
            return client;
        }

        static object Json(string url)
        {
            string text;
            using (var client = Client())
            {
                client.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                text = client.DownloadString(url);
            }
            return new JavaScriptSerializer { MaxJsonLength = 16 << 20 }.DeserializeObject(text);
        }

        /// The newest build on the channel, or null. Public repository: GitHub lists releases, runs
        /// and artifacts without signing in (60 requests an hour per address).
        public static ReleaseInfo Latest(string channel)
        {
            return channel == Nightly ? LatestNightly() : LatestRelease(channel == Prerelease);
        }

        /// The newest GitHub release (not a draft; not a pre-release unless asked) with a package zip.
        static ReleaseInfo LatestRelease(bool prereleases)
        {
            var releases = Json("https://api.github.com/repos/" + Repository + "/releases?per_page=20") as object[];
            if (releases == null) return null;
            foreach (Dictionary<string, object> release in releases.OfType<Dictionary<string, object>>())
            {
                bool pre = true.Equals(release["prerelease"]);
                if (true.Equals(release["draft"]) || (pre && !prereleases)) continue;
                string tag = release["tag_name"] as string ?? "";
                var info = new ReleaseInfo
                {
                    Tag = tag, Version = tag.StartsWith(TagPrefix) ? tag.Substring(TagPrefix.Length) : tag, Prerelease = pre,
                    Name = release["name"] as string ?? tag, Notes = release["body"] as string ?? ""
                };
                string shaUrl = null;
                var assets = release["assets"] as object[] ?? new object[0];
                foreach (Dictionary<string, object> asset in assets.OfType<Dictionary<string, object>>())
                {
                    string name = asset["name"] as string ?? "";
                    if (name.StartsWith(PackagePrefix) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && info.ZipUrl == null)
                    {
                        info.ZipName = name;
                        info.ZipUrl = asset["browser_download_url"] as string;
                    }
                }
                if (info.ZipUrl == null) continue;
                foreach (Dictionary<string, object> asset in assets.OfType<Dictionary<string, object>>())
                    if (string.Equals(asset["name"] as string, info.ZipName + ".sha256", StringComparison.OrdinalIgnoreCase))
                        shaUrl = asset["browser_download_url"] as string;
                // The checksum: the .sha256 asset, else a SHA-256 in the notes.
                if (shaUrl != null)
                {
                    try { using (var client = Client()) info.Sha256 = client.DownloadString(shaUrl).Split(' ')[0].Trim().ToLowerInvariant(); }
                    catch { }
                }
                if (info.Sha256 == null)
                {
                    var sha = Regex.Match(info.Notes, @"\b([0-9a-fA-F]{64})\b");
                    info.Sha256 = sha.Success ? sha.Groups[1].Value.ToLowerInvariant() : null;
                }
                return info;
            }
            return null;
        }

        /// The newest successful CI build on the branch with a package artifact that has not expired.
        static ReleaseInfo LatestNightly()
        {
            var runs = Json("https://api.github.com/repos/" + Repository + "/actions/workflows/" + Workflow +
                            "/runs?branch=" + Branch + "&status=success&per_page=5") as Dictionary<string, object>;
            object list;
            if (runs == null || !runs.TryGetValue("workflow_runs", out list)) return null;
            foreach (Dictionary<string, object> run in ((object[])list).OfType<Dictionary<string, object>>())
            {
                string id = Convert.ToString(run["id"]), sha = run["head_sha"] as string ?? "";
                if (sha.Length < 8) continue;
                var artifacts = Json("https://api.github.com/repos/" + Repository + "/actions/runs/" + id + "/artifacts") as Dictionary<string, object>;
                object items;
                if (artifacts == null || !artifacts.TryGetValue("artifacts", out items)) continue;
                foreach (Dictionary<string, object> artifact in ((object[])items).OfType<Dictionary<string, object>>())
                {
                    string name = artifact["name"] as string ?? "";
                    if (!name.StartsWith(ArtifactPrefix + sha, StringComparison.Ordinal) || true.Equals(artifact["expired"])) continue;
                    string when = (run["created_at"] as string ?? "").Replace('T', ' ').TrimEnd('Z');
                    return new ReleaseInfo
                    {
                        Artifact = true, Version = "ci-" + sha.Substring(0, 8), Tag = sha, Name = Product + " nightly " + sha.Substring(0, 8),
                        Notes = "Built " + (when.Length >= 16 ? when.Substring(0, 16) + " UTC" : when) + " from " + Branch + ".",
                        ZipName = NightlyPrefix + sha.Substring(0, 8) + ".zip",
                        ZipUrl = "https://nightly.link/" + Repository + "/actions/runs/" + id + "/" + name + ".zip"
                    };
                }
            }
            return null;
        }

        static string Sha256Of(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        static bool IsProtected(string relative)
        {
            string path = relative.Replace('/', '\\').TrimStart('\\');
            if (path.Contains("..") || Path.IsPathRooted(path)) return true;
            foreach (var folder in ProtectedFolders)
                if (path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            return ProtectedFiles.Any(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
        }

        /// Leftovers of an update: the download and staging folders, and the previous launcher exe,
        /// which couldn't be deleted while it was running.
        public static void CleanUp(string root)
        {
            foreach (string name in new[] { "staging", "download", "trash" })
            {
                try { string dir = Path.Combine(UpdateFolder(root), name); if (Directory.Exists(dir)) Directory.Delete(dir, true); }
                catch { }
            }
        }

        static void CheckGameClosed(string root)
        {
            foreach (var process in Process.GetProcessesByName(GameProcess))
            {
                try
                {
                    if (string.Equals(Path.GetDirectoryName(process.MainModule.FileName), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Close the game before updating.");
                }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }

        static void Download(string url, string path, string what, Action<string, double> progress)
        {
            using (var client = Client())
            {
                var done = new System.Threading.ManualResetEvent(false);
                Exception failure = null;
                client.DownloadProgressChanged += (s, e) =>
                    progress("Downloading " + what + ": " + (e.BytesReceived >> 20) +
                             (e.TotalBytesToReceive > 0 ? " of " + (e.TotalBytesToReceive >> 20) : "") + " MB",
                             e.TotalBytesToReceive > 0 ? e.BytesReceived / (double)e.TotalBytesToReceive : -1);
                client.DownloadFileCompleted += (s, e) => { failure = e.Error; done.Set(); };
                client.DownloadFileAsync(new Uri(url), path + ".part");
                done.WaitOne();
                if (failure != null) throw failure;
            }
            File.Move(path + ".part", path);
        }

        /// The package's files and their SHA-256, from manifest.json ("files": path, sha256) or
        /// SHA256SUMS.txt ("<sha256>  <path>").
        static List<KeyValuePair<string, string>> PackageFiles(string packageRoot)
        {
            var files = new List<KeyValuePair<string, string>>();
            string manifestPath = Path.Combine(packageRoot, "manifest.json");
            string sumsPath = Path.Combine(packageRoot, "SHA256SUMS.txt");
            if (File.Exists(manifestPath))
            {
                var manifest = (Dictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = 16 << 20 }.DeserializeObject(File.ReadAllText(manifestPath));
                foreach (Dictionary<string, object> file in ((object[])manifest["files"]).OfType<Dictionary<string, object>>())
                    files.Add(new KeyValuePair<string, string>((string)file["path"], ((string)file["sha256"]).ToLowerInvariant()));
                files.Add(new KeyValuePair<string, string>("manifest.json", null));
            }
            else if (File.Exists(sumsPath))
            {
                foreach (var line in File.ReadAllLines(sumsPath))
                {
                    var match = Regex.Match(line, @"^([0-9a-fA-F]{64}) [ *](.+)$");
                    if (match.Success) files.Add(new KeyValuePair<string, string>(match.Groups[2].Value, match.Groups[1].Value.ToLowerInvariant()));
                }
                files.Add(new KeyValuePair<string, string>("SHA256SUMS.txt", null));
            }
            else throw new InvalidDataException("The package lists no files (no manifest.json or SHA256SUMS.txt).");
            return files;
        }

        /// Downloads the build, checks it and installs its program files over root. progress gets
        /// a line of text and a fraction (or -1). The replaced files go to update\backup-<old version>.
        public static void Install(string root, ReleaseInfo release, Action<string, double> progress)
        {
            if (!release.Artifact && string.IsNullOrEmpty(release.Sha256))
                throw new InvalidDataException("The release gives no SHA-256 for its package, so it can't be checked.");
            CheckGameClosed(root);
            CleanUp(root);
            string folder = UpdateFolder(root), download = Path.Combine(folder, "download"), staging = Path.Combine(folder, "staging");
            Directory.CreateDirectory(download);
            string zip = Path.Combine(download, release.ZipName);
            string expected = release.Sha256;

            if (release.Artifact)
            {
                // A nightly: the artifact holds the package zip and its .sha256.
                string artifact = Path.Combine(download, "artifact.zip");
                Download(release.ZipUrl, artifact, release.ZipName, progress);
                progress("Unpacking the download...", -1);
                string unpacked = Path.Combine(download, "artifact");
                ZipFile.ExtractToDirectory(artifact, unpacked);
                string inner = Directory.GetFiles(unpacked, NightlyPrefix + "*.zip", SearchOption.AllDirectories).FirstOrDefault();
                if (inner == null || !File.Exists(inner + ".sha256")) throw new InvalidDataException("The nightly has no package zip and checksum.");
                expected = File.ReadAllText(inner + ".sha256").Split(' ')[0].Trim().ToLowerInvariant();
                File.Move(inner, zip);
            }
            else
            {
                Download(release.ZipUrl, zip, release.ZipName, progress);
            }
            if (string.IsNullOrEmpty(expected) || !Regex.IsMatch(expected, "^[0-9a-f]{64}$"))
                throw new InvalidDataException("The package's SHA-256 is not given, so it can't be checked.");

            progress("Checking the download...", -1);
            string sha = Sha256Of(zip);
            if (sha != expected)
                throw new InvalidDataException("The download is damaged (SHA-256 " + sha + ", expected " + expected + ").");

            progress("Unpacking...", -1);
            ZipFile.ExtractToDirectory(zip, staging);
            // The package's top folder: the one holding manifest.json or SHA256SUMS.txt.
            string listing = Directory.GetFiles(staging, "manifest.json", SearchOption.AllDirectories)
                                      .Concat(Directory.GetFiles(staging, "SHA256SUMS.txt", SearchOption.AllDirectories))
                                      .OrderBy(p => p.Length).FirstOrDefault();
            if (listing == null) throw new InvalidDataException("The package lists no files.");
            string packageRoot = Path.GetDirectoryName(listing);
            var files = new List<string>();
            foreach (var file in PackageFiles(packageRoot))
            {
                string relative = file.Key.Replace('/', '\\');
                if (IsProtected(relative)) continue;
                string source = Path.Combine(packageRoot, relative);
                if (!File.Exists(source) || (file.Value != null && Sha256Of(source) != file.Value))
                    throw new InvalidDataException("The package's " + relative + " doesn't match its checksum list.");
                files.Add(relative);
            }
            files = files.Where(f => !VersionFiles.Contains(f, StringComparer.OrdinalIgnoreCase))
                         .Concat(files.Where(f => VersionFiles.Contains(f, StringComparer.OrdinalIgnoreCase))).ToList();

            string installed = InstalledVersion(root) ?? "unknown";
            string backup = Path.Combine(folder, "backup-" + installed);
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            Directory.CreateDirectory(backup);
            var replaced = new List<string>();
            var added = new List<string>();
            try
            {
                int n = 0;
                foreach (string relative in files)
                {
                    progress("Installing " + relative, ++n / (double)files.Count);
                    string target = Path.Combine(root, relative), source = Path.Combine(packageRoot, relative);
                    if (File.Exists(target))
                    {
                        string saved = Path.Combine(backup, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(saved));
                        // A move, not a copy: the launcher exe is running and can only be renamed.
                        File.Move(target, saved);
                        replaced.Add(relative);
                    }
                    else
                    {
                        added.Add(relative);
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Copy(source, target);
                }
            }
            catch
            {
                Restore(root, backup, replaced, added);
                Directory.Delete(backup, true);
                throw;
            }
            WriteRecord(backup, installed, release.Version, replaced, added);
            RecordInstalled(root, release.Version);
            CleanUp(root);
            progress("Updated to " + Display(release.Version) + ".", 1);
        }

        /// What was installed, for packages whose files don't name its version (a nightly repackaged as
        /// a release, or BUILD_INFO.txt + SHA256SUMS.txt): valid while the build identity is the same.
        static void RecordInstalled(string root, string version)
        {
            string identity = BuildIdentity(root), path = Path.Combine(UpdateFolder(root), "installed.json");
            try
            {
                if (identity == null || version == null || version == "unknown") { if (File.Exists(path)) File.Delete(path); return; }
                var record = new Dictionary<string, object> { { "version", version }, { "build", identity } };
                File.WriteAllText(path, new JavaScriptSerializer().Serialize(record));
            }
            catch { }
        }

        static void WriteRecord(string backup, string from, string to, List<string> replaced, List<string> added)
        {
            var record = new Dictionary<string, object> { { "from", from }, { "to", to }, { "replaced", replaced }, { "added", added } };
            File.WriteAllText(Path.Combine(backup, "rollback.json"), new JavaScriptSerializer().Serialize(record));
        }

        static void Restore(string root, string backup, List<string> replaced, List<string> added)
        {
            string trash = Path.Combine(UpdateFolder(root), "trash");
            Directory.CreateDirectory(trash);
            foreach (string relative in added.Concat(replaced))
            {
                string target = Path.Combine(root, relative);
                if (!File.Exists(target)) continue;
                // Moved aside rather than deleted: one of them may be the running launcher.
                string aside = Path.Combine(trash, Guid.NewGuid().ToString("N"));
                File.Move(target, aside);
            }
            foreach (string relative in replaced)
            {
                string saved = Path.Combine(backup, relative), target = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Move(saved, target);
            }
        }

        /// The update that can be rolled back (the newest backup), as its folder, "from" and "to".
        public static Tuple<string, string, string> LastUpdate(string root)
        {
            string folder = UpdateFolder(root);
            if (!Directory.Exists(folder)) return null;
            var newest = Directory.GetDirectories(folder, "backup-*")
                                  .Where(d => File.Exists(Path.Combine(d, "rollback.json")))
                                  .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, "rollback.json"))).FirstOrDefault();
            if (newest == null) return null;
            var record = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Path.Combine(newest, "rollback.json")));
            return Tuple.Create(newest, (string)record["from"], (string)record["to"]);
        }

        /// Puts the files of the last update back as they were before it.
        public static void RollBack(string root)
        {
            CheckGameClosed(root);
            var last = LastUpdate(root);
            if (last == null) throw new InvalidOperationException("There is no update to roll back.");
            var record = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(Path.Combine(last.Item1, "rollback.json")));
            var replaced = ((object[])record["replaced"]).Cast<string>().ToList();
            var added = ((object[])record["added"]).Cast<string>().ToList();
            Restore(root, last.Item1, replaced, added);
            Directory.Delete(last.Item1, true);
            RecordInstalled(root, last.Item2);
        }

        /// Starts the (new) launcher and closes this one.
        public static void Restart(string root)
        {
            Process.Start(new ProcessStartInfo(Path.Combine(root, LauncherExe)) { WorkingDirectory = root, UseShellExecute = false });
            System.Windows.Forms.Application.Exit();
        }
    }
}
