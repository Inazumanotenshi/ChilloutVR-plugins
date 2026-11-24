using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MelonLoader;

[assembly: MelonInfo(typeof(BlankUpdater.Plugin), BlankUpdater.BuildInfo.Name, BlankUpdater.BuildInfo.Version, BlankUpdater.BuildInfo.Author)]
[assembly: MelonGame(null, "ChilloutVR")]
[assembly: MelonPriority(-1000)]
// BTKUILib (quick menu page) is optional and only loaded later with the mods
[assembly: MelonOptionalDependencies("BTKUILib")]

namespace BlankUpdater
{
    internal static class BuildInfo
    {
        public const string Name = "BlankUpdater";
        public const string Version = "1.0.0"; // MAJOR.MINOR.PATCH, raise it before every upload
        public const string Author = "Inazumanotenshi";
        public const string DefaultManifestUrl = "https://download.blankinternational.com/downloads/.updater/manifest.php";
        public const string DefaultDownloadUrl = "https://download.blankinternational.com/downloads/";
    }

    /// <summary>
    /// Checks the Blank International download server for newer versions of installed mods and plugins
    /// and replaces them before ChilloutVR loads its mods.
    /// </summary>
    /// <summary>What the last check found for one mod or plugin, shown on the quick menu page.</summary>
    internal sealed class ModStatus
    {
        public string Name;
        public bool IsPlugin;
        public string Installed;
        public string Stable;
        public string Beta;
        public string Result = "Not checked";
    }

    public class Plugin : MelonPlugin
    {
        internal static Plugin Instance { get; private set; }
        internal readonly List<ModStatus> Mods = new List<ModStatus>();
        internal string LastResult = "No check yet";
        internal DateTime LastCheck;

        internal MelonPreferences_Entry<bool> EnabledEntry => _enabled;
        internal MelonPreferences_Entry<string> ChannelEntry => _channel;
        internal MelonPreferences_Entry<bool> UpdatePluginsEntry => _updatePlugins;
        internal MelonPreferences_Entry<bool> ShowWindowEntry => _showWindow;
        internal MelonPreferences_Entry<float> MinStepEntry => _minStepSeconds;
        internal string DownloadPage => _downloadUrl.Value.TrimEnd('/') + "/?path=ChilloutVR";
        private MelonPreferences_Entry<bool> _enabled;
        private MelonPreferences_Entry<string> _channel;
        private MelonPreferences_Entry<string> _channelOverrides;
        private MelonPreferences_Entry<string> _ignored;
        private MelonPreferences_Entry<bool> _updatePlugins;
        private MelonPreferences_Entry<string> _manifestUrl;
        private MelonPreferences_Entry<string> _downloadUrl;
        private MelonPreferences_Entry<bool> _showWindow;
        private MelonPreferences_Entry<float> _minStepSeconds;

        private sealed class PendingUpdate
        {
            public string File;
            public MelonInfoReader.Info Local;
            public Dictionary<string, object> Entry;
            public string RemoteVersion;
            public string Channel;
            public ModStatus Status;
        }

        private readonly List<PendingUpdate> _pending = new List<PendingUpdate>();
        private UpdaterWindow _window;
        private Stopwatch _stepWatch;

        private int _updated;
        private int _pendingRestart;
        private int _failed;

        public override void OnPreInitialization()
        {
            Instance = this;
            var category = MelonPreferences.CreateCategory("BlankUpdater", "Blank Updater");
            _enabled = category.CreateEntry("Enabled", true, "Enabled", "Check for updates when the game starts");
            _channel = category.CreateEntry("Channel", "Stable", "Channel", "Stable or Beta. Beta also gets Stable releases when they are newer");
            _channelOverrides = category.CreateEntry("ChannelOverrides", "", "Channel per mod", "Example: MP3toMP4=Beta, SizeScaledMovement=Stable");
            _ignored = category.CreateEntry("Ignored", "", "Ignored mods", "Mods that are never updated, comma separated. Example: MP3toMP4, AdvActionMenu");
            _updatePlugins = category.CreateEntry("UpdatePlugins", true, "Update plugins", "Also update plugins (including this updater). They take effect on the next start");
            _manifestUrl = category.CreateEntry("ManifestUrl", BuildInfo.DefaultManifestUrl, "Manifest URL", "Where the list of available versions comes from");
            _downloadUrl = category.CreateEntry("DownloadUrl", BuildInfo.DefaultDownloadUrl, "Download URL", "Base address the files in the manifest are relative to");
            _showWindow = category.CreateEntry("ShowWindow", true, "Show window", "Show a small progress window while checking and downloading");
            _minStepSeconds = category.CreateEntry("MinStepSeconds", 2f, "Minimum step time", "How long each step (checking, every download, the result) stays visible in the window, in seconds");
            MelonPreferences.Save();
        }

        public override void OnPreModsLoaded()
        {
            if (!_enabled.Value)
            {
                LoggerInstance.Msg("Update check disabled in MelonPreferences.cfg");
                return;
            }

            var watch = Stopwatch.StartNew();
            if (_showWindow.Value)
                _window = UpdaterWindow.TryOpen($"Channel: {_channel.Value}  ·  download.blankinternational.com");
            _window?.SetStatus("Checking for updates...");
            _window?.SetDetail("Looking at your installed mods");
            _stepWatch = Stopwatch.StartNew();

            try
            {
                Run();
            }
            catch (Exception ex)
            {
                _failed++;
                LoggerInstance.Error($"Update check failed: {ex}");
            }

            if (_window != null)
            {
                string status;
                if (_updated + _pendingRestart == 0 && _failed == 0)
                    status = "Everything is up to date";
                else
                {
                    status = $"{_updated + _pendingRestart} update(s) installed";
                    if (_failed > 0)
                        status += $", {_failed} failed (see log)";
                }
                _window.SetMode(_failed > 0 ? UpdaterMode.Error : UpdaterMode.Done);
                _window.SetStatus(status);
                _window.SetDetail(_pendingRestart > 0 ? "Plugin updates are active after the next start" : "Starting ChilloutVR...");
                _window.SetProgress(1000);
                _window.Close(MinStepMs);
                _window = null;
            }

            LastCheck = DateTime.Now;
            LastResult = _updated + _pendingRestart == 0 && _failed == 0 ? "Everything up to date" : $"{_updated + _pendingRestart} updated" + (_failed > 0 ? $", {_failed} failed" : "");
            string summary = $"Update check done in {watch.ElapsedMilliseconds} ms: {_updated} updated";
            if (_pendingRestart > 0)
                summary += $", {_pendingRestart} plugin update(s) active after the next start";
            if (_failed > 0)
                summary += $", {_failed} failed";
            LoggerInstance.Msg(summary);
        }

        public override void OnApplicationStarted()
        {
            // all mods (and BTKUILib) are loaded now
            QuickMenu.Init(LoggerInstance);
        }

        private void Run()
        {
            string gameRoot = GetGameRoot();
            string modsDir = Path.Combine(gameRoot, "Mods");
            string pluginsDir = Path.Combine(gameRoot, "Plugins");

            CleanUp(modsDir);
            CleanUp(pluginsDir);

            Dictionary<string, object> manifest;
            try
            {
                byte[] raw = Downloader.Get(_manifestUrl.Value, LoggerInstance.Warning);
                manifest = Json.Parse(Encoding.UTF8.GetString(raw)) as Dictionary<string, object>;
                if (manifest == null)
                    throw new FormatException("Manifest is not a JSON object");
            }
            catch (Exception ex)
            {
                LoggerInstance.Warning($"Could not load the update list ({ex.GetBaseException().Message}). The game starts normally");
                LastResult = "Update server not reachable";
                LastCheck = DateTime.Now;
                HoldStep();
                _window?.SetMode(UpdaterMode.Error);
                _window?.SetStatus("Update server not reachable");
                _window?.SetDetail("The game starts normally");
                _window?.Close(MinStepMs);
                _window = null;
                return;
            }

            var defaultChannel = ParseChannel(_channel.Value) ?? "stable";
            var overrides = ParseOverrides(_channelOverrides.Value);
            var ignored = new HashSet<string>(SplitList(_ignored.Value), StringComparer.OrdinalIgnoreCase);

            LoggerInstance.Msg($"Checking for updates (channel {defaultChannel})");
            CheckFolder(manifest, "mods", modsDir, defaultChannel, overrides, ignored);
            if (_updatePlugins.Value)
                CheckFolder(manifest, "plugins", pluginsDir, defaultChannel, overrides, ignored);

            HoldStep(); // the "checking" step

            for (int i = 0; i < _pending.Count; i++)
            {
                var update = _pending[i];
                _stepWatch = Stopwatch.StartNew();
                int index = i;
                _window?.SetMode(UpdaterMode.Downloading);
                _window?.SetStatus($"Downloading {update.Local.Name} {update.RemoteVersion} ({index + 1}/{_pending.Count})");
                _window?.SetDetail($"{update.Local.Version} → {update.RemoteVersion}  ·  {update.Channel}");
                _window?.SetFileProgress(0);
                _window?.SetProgress(index * 1000 / _pending.Count);
                Update(update, (done, total) =>
                {
                    if (_window == null || total <= 0)
                        return;
                    int part = (int)(done * 1000 / total);
                    _window.SetFileProgress(part / 1000f);
                    _window.SetProgress((index * 1000 + part) / _pending.Count);
                    _window.SetDetail($"{FormatSize(done)} of {FormatSize(total)}  ·  {update.Local.Version} → {update.RemoteVersion}");
                });
                HoldStep();
            }
        }

        private void CheckFolder(Dictionary<string, object> manifest, string kind, string directory, string defaultChannel,
            Dictionary<string, string> overrides, HashSet<string> ignored)
        {
            if (!Directory.Exists(directory) || !(manifest.TryGetValue(kind, out object section) && section is Dictionary<string, object> remoteMods) || remoteMods.Count == 0)
                return;

            foreach (string file in Directory.GetFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
            {
                var local = MelonInfoReader.Read(file);
                if (local == null)
                    continue;

                if (!TryFindRemote(remoteMods, local.Name, Path.GetFileNameWithoutExtension(file), out var remote))
                    continue;

                var status = new ModStatus
                {
                    Name = local.Name,
                    IsPlugin = kind == "plugins",
                    Installed = local.Version,
                    Stable = GetString(PickEntry(remote, "stable"), "version"),
                    Beta = remote.TryGetValue("beta", out object betaEntry) ? GetString(betaEntry as Dictionary<string, object>, "version") : null,
                };
                Mods.Add(status);

                if (ignored.Contains(local.Name))
                {
                    status.Result = "Ignored";
                    LoggerInstance.Msg($"{local.Name} {local.Version}: ignored");
                    continue;
                }

                string channel = overrides.TryGetValue(local.Name, out string c) ? c : defaultChannel;
                var entry = PickEntry(remote, channel);
                if (entry == null)
                {
                    status.Result = "No release on " + channel;
                    continue;
                }

                string remoteVersion = GetString(entry, "version");
                string remoteAuthor = GetString(entry, "author");
                if (remoteAuthor != null && local.Author != null && !string.Equals(remoteAuthor, local.Author, StringComparison.OrdinalIgnoreCase))
                {
                    status.Result = "Different author, not touched";
                    LoggerInstance.Warning($"{local.Name}: installed version is by \"{local.Author}\", not \"{remoteAuthor}\". Not touching it");
                    continue;
                }

                int compare = SemVer.Compare(remoteVersion, local.Version);
                if (compare < 0)
                {
                    status.Result = "Newer than the server";
                    LoggerInstance.Msg($"{local.Name} {local.Version}: newer than {channel} ({remoteVersion}), keeping it");
                    continue;
                }
                if (compare == 0)
                {
                    string localHash = Sha256(File.ReadAllBytes(file));
                    bool same = string.Equals(localHash, GetString(entry, "sha256"), StringComparison.OrdinalIgnoreCase);
                    status.Result = same ? "Up to date" : "Same version, different file";
                    if (!same)
                        LoggerInstance.Warning($"{local.Name} {local.Version}: same version as on the server but a different file. Was the version number not raised?");
                    else
                        LoggerInstance.Msg($"{local.Name} {local.Version}: up to date");
                    continue;
                }

                status.Result = "Update found";
                _pending.Add(new PendingUpdate { File = file, Local = local, Entry = entry, RemoteVersion = remoteVersion, Channel = channel, Status = status });
            }
        }

        private void Update(PendingUpdate update, Action<long, long> progress)
        {
            string file = update.File;
            var local = update.Local;
            var entry = update.Entry;
            string remoteVersion = update.RemoteVersion;
            string channel = update.Channel;
            string label = $"{local.Name} {local.Version} -> {remoteVersion} ({channel})";
            try
            {
                string url = BuildUrl(_downloadUrl.Value, GetString(entry, "path"));
                byte[] data = Downloader.Get(url, LoggerInstance.Warning, progress);

                string expectedHash = GetString(entry, "sha256");
                if (string.IsNullOrEmpty(expectedHash) || !string.Equals(Sha256(data), expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("checksum does not match, download discarded");

                var downloaded = MelonInfoReader.Read(data);
                if (downloaded == null || !string.Equals(downloaded.Name, local.Name, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("downloaded file is not " + local.Name);

                string temp = file + ".download";
                File.WriteAllBytes(temp, data);

                try
                {
                    File.Copy(temp, file, true);
                    File.Delete(temp);
                    _updated++;
                    update.Status.Installed = remoteVersion;
                    update.Status.Result = $"Updated from {local.Version}";
                    LoggerInstance.Msg($"Updated {label}");
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // The file is in use (a plugin that is already loaded, e.g. this updater).
                    // Windows still allows renaming it, so the new version is used on the next start.
                    string old = file + ".old";
                    try { if (File.Exists(old)) File.Delete(old); }
                    catch { old = $"{file}.{DateTime.Now.Ticks}.old"; }
                    File.Move(file, old);
                    File.Move(temp, file);
                    _pendingRestart++;
                    update.Status.Result = $"Updated to {remoteVersion}, active after the next start";
                    LoggerInstance.Msg($"Updated {label}, active after the next start");
                }
            }
            catch (Exception ex)
            {
                _failed++;
                update.Status.Result = "Update failed: " + ex.GetBaseException().Message;
                LoggerInstance.Error($"Could not update {label}: {ex.GetBaseException().Message}");
                try { File.Delete(file + ".download"); } catch { }
            }
        }

        /// <summary>"default", "stable", "beta" or "ignore" for one mod, from ChannelOverrides and Ignored.</summary>
        internal string GetModChoice(string name)
        {
            if (SplitList(_ignored.Value).Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                return "ignore";
            return ParseOverrides(_channelOverrides.Value).TryGetValue(name, out string channel) ? channel : "default";
        }

        internal void SetModChoice(string name, string choice)
        {
            var ignored = SplitList(_ignored.Value).Where(n => !string.Equals(n, name, StringComparison.OrdinalIgnoreCase)).ToList();
            var overrides = ParseOverrides(_channelOverrides.Value);
            overrides.Remove(name);

            if (choice == "ignore")
                ignored.Add(name);
            else if (choice == "stable" || choice == "beta")
                overrides[name] = choice;

            _ignored.Value = string.Join(", ", ignored);
            _channelOverrides.Value = string.Join(", ", overrides.Select(o => $"{o.Key}={(o.Value == "beta" ? "Beta" : "Stable")}"));
            MelonPreferences.Save();
        }

        internal string DefaultChannel => ParseChannel(_channel.Value) ?? "stable";

        private static bool TryFindRemote(Dictionary<string, object> remoteMods, string melonName, string fileName, out Dictionary<string, object> remote)
        {
            foreach (var pair in remoteMods)
            {
                if (!(pair.Value is Dictionary<string, object> candidate))
                    continue;

                string remoteName = GetString(PickEntry(candidate, "beta"), "melonName") ?? pair.Key;
                if (string.Equals(remoteName, melonName, StringComparison.OrdinalIgnoreCase) || string.Equals(pair.Key, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    remote = candidate;
                    return true;
                }
            }
            remote = null;
            return false;
        }

        // Stable only uses stable releases. Beta takes whichever of beta and stable is newer.
        private static Dictionary<string, object> PickEntry(Dictionary<string, object> remote, string channel)
        {
            remote.TryGetValue("stable", out object s);
            var stable = s as Dictionary<string, object>;
            if (channel != "beta")
                return stable;

            remote.TryGetValue("beta", out object b);
            var beta = b as Dictionary<string, object>;
            if (beta == null)
                return stable;
            if (stable == null)
                return beta;
            return SemVer.Compare(GetString(beta, "version"), GetString(stable, "version")) >= 0 ? beta : stable;
        }

        private void CleanUp(string directory)
        {
            if (!Directory.Exists(directory))
                return;
            // Only leftovers of this updater: Name.dll.old, Name.dll.<ticks>.old and Name.dll.download
            var leftovers = Directory.GetFiles(directory, "*.old").Concat(Directory.GetFiles(directory, "*.download"))
                .Where(f => Path.GetFileName(f).IndexOf(".dll.", StringComparison.OrdinalIgnoreCase) > 0);
            foreach (string file in leftovers)
            {
                try { File.Delete(file); }
                catch { }
            }
        }

        private int MinStepMs => _window == null ? 0 : (int)(Math.Max(0f, Math.Min(10f, _minStepSeconds.Value)) * 1000);

        // Keeps a step on screen for at least MinStepSeconds, but only while the window is shown.
        private void HoldStep()
        {
            if (_window == null || _stepWatch == null)
                return;
            int remaining = MinStepMs - (int)_stepWatch.ElapsedMilliseconds;
            if (remaining > 0)
                System.Threading.Thread.Sleep(remaining);
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024 * 1024)
                return $"{bytes / 1024.0:0.#} KB";
            return $"{bytes / (1024.0 * 1024.0):0.0} MB";
        }

        private static string GetGameRoot()
        {
            try
            {
                return Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
            }
            catch
            {
                return AppDomain.CurrentDomain.BaseDirectory;
            }
        }

        private static string BuildUrl(string baseUrl, string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
                throw new InvalidDataException("manifest entry has no path");
            string escaped = string.Join("/", relativePath.Split('/').Select(Uri.EscapeDataString));
            return baseUrl.TrimEnd('/') + "/" + escaped;
        }

        private static string Sha256(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                var sb = new StringBuilder(64);
                foreach (byte b in sha.ComputeHash(data))
                    sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static string GetString(Dictionary<string, object> entry, string key)
        {
            return entry != null && entry.TryGetValue(key, out object value) ? value as string : null;
        }

        private static string ParseChannel(string value)
        {
            string v = value?.Trim().ToLowerInvariant();
            return v == "beta" || v == "stable" ? v : null;
        }

        private static IEnumerable<string> SplitList(string value)
        {
            return (value ?? "").Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0);
        }

        private Dictionary<string, string> ParseOverrides(string value)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string item in SplitList(value))
            {
                int eq = item.IndexOf('=');
                string channel = eq > 0 ? ParseChannel(item.Substring(eq + 1)) : null;
                if (channel == null)
                {
                    LoggerInstance.Warning($"Ignoring channel setting \"{item}\", use Name=Stable or Name=Beta");
                    continue;
                }
                result[item.Substring(0, eq).Trim()] = channel;
            }
            return result;
        }
    }
}
