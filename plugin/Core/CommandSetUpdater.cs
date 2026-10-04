using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace revit_mcp_plugin.Core
{
    /// <summary>
    /// A command set version unpacked under Commands\.managed\&lt;year&gt;\&lt;version&gt;\.
    /// </summary>
    public class ManagedCommandSet
    {
        public string Version { get; set; }
        public string Directory { get; set; }
        public string AssemblyPath { get; set; }
        public List<string> CommandNames { get; set; } = new List<string>();
    }

    /// <summary>
    /// Keeps the command set in sync with the office MCP server, so new tools
    /// reach every PC without reinstalling the plugin.
    /// <para>
    /// When update.json (next to the plugin DLL) names a server, the latest
    /// command set for this Revit year is downloaded into its own version
    /// folder and every command in it is loaded - no Settings step. Each
    /// version gets a new folder because a loaded DLL stays locked and cannot
    /// be unloaded, so a new version applies from the next Revit session. If
    /// the server is unreachable the last downloaded version is used.
    /// </para>
    /// </summary>
    public class CommandSetUpdater
    {
        public const string ManagedFolderName = ".managed";
        public const string SettingsFileName = "update.json";
        public const string AssemblyFileName = "RevitMCPCommandSet.dll";

        private static readonly Regex SafeName = new Regex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$");

        // The version loaded in this Revit process; a second copy cannot be loaded
        private static ManagedCommandSet _sessionCommandSet;

        private readonly string _pluginDirectory;
        private readonly string _commandsDirectory;
        private readonly Action<string> _log;

        public CommandSetUpdater(string pluginDirectory, string commandsDirectory, Action<string> log)
        {
            _pluginDirectory = pluginDirectory;
            _commandsDirectory = commandsDirectory;
            _log = log ?? (_ => { });
        }

        private class UpdateSettings
        {
            [JsonProperty("serverUrl")]
            public string ServerUrl { get; set; }
        }

        private class Manifest
        {
            [JsonProperty("version")]
            public string Version { get; set; }

            [JsonProperty("file")]
            public string File { get; set; }

            [JsonProperty("sha256")]
            public string Sha256 { get; set; }
        }

        private class CurrentPointer
        {
            [JsonProperty("version")]
            public string Version { get; set; }
        }

        private class CommandListJson
        {
            [JsonProperty("commands")]
            public List<CommandItem> Commands { get; set; } = new List<CommandItem>();
        }

        private class CommandItem
        {
            [JsonProperty("commandName")]
            public string CommandName { get; set; }
        }

        /// <summary>
        /// The server URL from update.json, or null when updates are not configured
        /// (the plugin then uses the commands enabled in Settings, as before).
        /// </summary>
        public string ReadServerUrl()
        {
            var path = Path.Combine(_pluginDirectory, SettingsFileName);
            if (!File.Exists(path))
                return null;
            try
            {
                var settings = JsonConvert.DeserializeObject<UpdateSettings>(File.ReadAllText(path));
                var url = settings?.ServerUrl?.Trim().TrimEnd('/');
                return string.IsNullOrEmpty(url) ? null : url;
            }
            catch (Exception ex)
            {
                _log($"Ignoring invalid {SettingsFileName}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Updates from the server when possible and returns the command set to
        /// load, or null when updates are not configured or nothing is installed.
        /// </summary>
        public ManagedCommandSet EnsureLatest(string revitYear)
        {
            if (_sessionCommandSet != null)
                return _sessionCommandSet;

            var serverUrl = ReadServerUrl();
            if (serverUrl == null)
                return null;

            var root = Path.Combine(_commandsDirectory, ManagedFolderName, revitYear);
            Directory.CreateDirectory(root);

            try
            {
                Update(serverUrl, revitYear, root);
            }
            catch (Exception ex)
            {
                _log($"Command set update from {serverUrl} failed, using the installed version: {ex.Message}");
            }

            var current = ReadCurrent(root);
            if (current == null)
            {
                _log("No command set has been downloaded yet; falling back to the commands enabled in Settings.");
                return null;
            }

            DeleteOldVersions(root, current.Version);
            _sessionCommandSet = current;
            return current;
        }

        private void Update(string serverUrl, string revitYear, string root)
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) })
            {
                var manifestUrl = $"{serverUrl}/commandset/{revitYear}/manifest.json";
                var manifest = JsonConvert.DeserializeObject<Manifest>(GetString(http, manifestUrl));
                if (manifest == null || !IsSafe(manifest.Version) || !IsSafe(manifest.File) || string.IsNullOrEmpty(manifest.Sha256))
                    throw new InvalidDataException($"Invalid manifest at {manifestUrl}");

                var current = ReadCurrent(root);
                if (current != null && current.Version == manifest.Version)
                {
                    _log($"Command set {manifest.Version} is up to date.");
                    return;
                }

                var target = Path.Combine(root, manifest.Version);
                if (!IsComplete(target))
                {
                    var bytes = GetBytes(http, $"{serverUrl}/commandset/{revitYear}/{manifest.File}");
                    var actual = Sha256Hex(bytes);
                    if (!string.Equals(actual, manifest.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Checksum mismatch for {manifest.File}");
                    Unpack(bytes, root, target);
                }

                WriteCurrent(root, manifest.Version);
                _log($"Command set updated to {manifest.Version}" + (current != null ? $" (was {current.Version})" : "") + ".");
            }
        }

        private static string GetString(HttpClient http, string url)
        {
            using (var response = http.GetAsync(url).GetAwaiter().GetResult())
            {
                response.EnsureSuccessStatusCode();
                return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
        }

        private static byte[] GetBytes(HttpClient http, string url)
        {
            using (var response = http.GetAsync(url).GetAwaiter().GetResult())
            {
                response.EnsureSuccessStatusCode();
                return response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            }
        }

        /// <summary>Extracts to a temp folder first so a half-written version is never used.</summary>
        private static void Unpack(byte[] zipBytes, string root, string target)
        {
            var staging = Path.Combine(root, ".staging-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var stream = new MemoryStream(zipBytes))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    // ExtractToDirectory rejects entries that escape the folder
                    archive.ExtractToDirectory(staging);
                }
                if (!IsComplete(staging))
                    throw new InvalidDataException($"The package has no command.json or {AssemblyFileName}");

                if (Directory.Exists(target))
                    Directory.Delete(target, true);
                try
                {
                    Directory.Move(staging, target);
                }
                catch (IOException)
                {
                    // Another Revit instance unpacked the same version first
                    if (!IsComplete(target))
                        throw;
                }
            }
            finally
            {
                if (Directory.Exists(staging))
                    TryDelete(staging);
            }
        }

        private static bool IsComplete(string directory)
        {
            return File.Exists(Path.Combine(directory, "command.json"))
                && File.Exists(Path.Combine(directory, AssemblyFileName));
        }

        private ManagedCommandSet ReadCurrent(string root)
        {
            var pointerPath = Path.Combine(root, "current.json");
            if (!File.Exists(pointerPath))
                return null;
            try
            {
                var version = JsonConvert.DeserializeObject<CurrentPointer>(File.ReadAllText(pointerPath))?.Version;
                if (!IsSafe(version))
                    return null;
                var directory = Path.Combine(root, version);
                if (!IsComplete(directory))
                    return null;

                var commands = JsonConvert.DeserializeObject<CommandListJson>(
                    File.ReadAllText(Path.Combine(directory, "command.json")));
                return new ManagedCommandSet
                {
                    Version = version,
                    Directory = directory,
                    AssemblyPath = Path.Combine(directory, AssemblyFileName),
                    CommandNames = (commands?.Commands ?? new List<CommandItem>())
                        .Select(c => c.CommandName)
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .Distinct()
                        .ToList()
                };
            }
            catch (Exception ex)
            {
                _log($"Cannot read the installed command set: {ex.Message}");
                return null;
            }
        }

        private static void WriteCurrent(string root, string version)
        {
            var pointerPath = Path.Combine(root, "current.json");
            var temp = pointerPath + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(new CurrentPointer { Version = version }));
            if (File.Exists(pointerPath))
                File.Replace(temp, pointerPath, null);
            else
                File.Move(temp, pointerPath);
        }

        /// <summary>Removes superseded versions; folders still loaded by another Revit stay until next time.</summary>
        private static void DeleteOldVersions(string root, string keep)
        {
            foreach (var directory in Directory.GetDirectories(root))
            {
                if (!string.Equals(Path.GetFileName(directory), keep, StringComparison.OrdinalIgnoreCase))
                    TryDelete(directory);
            }
        }

        private static void TryDelete(string directory)
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch (Exception)
            {
                // Locked by a running Revit; retried on the next update
            }
        }

        private static bool IsSafe(string name)
        {
            return !string.IsNullOrEmpty(name) && name.Length <= 128 && SafeName.IsMatch(name) && !name.Contains("..");
        }

        private static string Sha256Hex(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
