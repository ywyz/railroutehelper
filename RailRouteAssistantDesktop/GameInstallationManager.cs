using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RailRouteAssistantDesktop
{
    /// <summary>
    /// 一体包首次运行安装器：发现/询问 Rail Route 目录，安装随包附带的 BepInEx
    /// 与插件，记住路径，然后通过 Steam 自动启动游戏。
    /// </summary>
    internal static class GameInstallationManager
    {
        private const string SteamAppId = "1124180";
        private const string GameExecutableName = "Rail Route.exe";
        private const string ElevatedInstallArgument = "--install-elevated";

        private static readonly string PayloadDirectory = Path.Combine(AppContext.BaseDirectory, "payload");
        private static readonly string SettingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RailRouteAssistant");
        private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

        public static bool PrepareInstallationAndLaunch(string[] args)
        {
            // 普通开发输出没有 payload，不弹安装窗口，也不改变原有调试体验。
            if (!HasCompletePayload()) return true;

            bool elevatedInstall = args.Length >= 2 &&
                string.Equals(args[0], ElevatedInstallArgument, StringComparison.OrdinalIgnoreCase);
            string gameDirectory = elevatedInstall ? args[1] : ResolveGameDirectory();
            if (string.IsNullOrEmpty(gameDirectory)) return false;

            if (!IsGameDirectory(gameDirectory))
            {
                MessageBox.Show(
                    "所选目录中没有找到 Rail Route.exe，请选择 Steam 中 Rail Route 的安装目录。",
                    "Rail Route 调度助手",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return false;
            }

            SaveSettings(gameDirectory);

            try
            {
                bool installed = InstallPayloadIfNeeded(gameDirectory);
                if (installed)
                {
                    MessageBox.Show(
                        "BepInEx 与调度助手插件安装完成。游戏将自动启动；首次启动时 BepInEx 会生成配置文件。",
                        "Rail Route 调度助手",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
            }
            catch (UnauthorizedAccessException) when (!elevatedInstall)
            {
                return RelaunchElevated(gameDirectory);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"安装插件失败：{ex.Message}\n\n请关闭正在运行的游戏后重试。",
                    "Rail Route 调度助手",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return false;
            }

            LaunchGame(gameDirectory);
            return true;
        }

        private static string ResolveGameDirectory()
        {
            var saved = LoadSettings()?.GameDirectory;
            if (IsGameDirectory(saved)) return Path.GetFullPath(saved);

            string detected = FindSteamGameDirectory();
            if (IsGameDirectory(detected))
            {
                var choice = MessageBox.Show(
                    $"检测到 Rail Route：\n{detected}\n\n是否在此目录安装 BepInEx 与调度助手插件？",
                    "Rail Route 调度助手 - 首次安装",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (choice == DialogResult.Yes) return Path.GetFullPath(detected);
            }

            using var dialog = new FolderBrowserDialog
            {
                Description = "请选择包含 Rail Route.exe 的游戏安装目录",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false,
                SelectedPath = detected ?? string.Empty
            };
            return dialog.ShowDialog() == DialogResult.OK ? Path.GetFullPath(dialog.SelectedPath) : null;
        }

        private static string FindSteamGameDirectory()
        {
            foreach (var steamRoot in GetSteamRoots())
            {
                foreach (var library in GetSteamLibraries(steamRoot))
                {
                    string candidate = Path.Combine(library, "steamapps", "common", "Rail Route");
                    if (IsGameDirectory(candidate)) return candidate;
                }
            }
            return null;
        }

        private static IEnumerable<string> GetSteamRoots()
        {
            var candidates = new List<string>();
            AddRegistryPath(candidates, Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
            AddRegistryPath(candidates, Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
            AddRegistryPath(candidates, Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));

            return candidates
                .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static void AddRegistryPath(List<string> paths, RegistryKey root, string subKey, string valueName)
        {
            try
            {
                using var key = root.OpenSubKey(subKey);
                if (key?.GetValue(valueName) is string path) paths.Add(path.Replace('/', '\\'));
            }
            catch { }
        }

        private static IEnumerable<string> GetSteamLibraries(string steamRoot)
        {
            var libraries = new List<string> { steamRoot };
            string vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdfPath))
            {
                try
                {
                    string vdf = File.ReadAllText(vdfPath);
                    foreach (Match match in Regex.Matches(vdf, "\\\"path\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase))
                        libraries.Add(match.Groups[1].Value.Replace("\\\\", "\\"));
                }
                catch { }
            }

            return libraries
                .Where(Directory.Exists)
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static bool HasCompletePayload()
        {
            return File.Exists(Path.Combine(PayloadDirectory, "BepInEx", "core", "BepInEx.dll")) &&
                File.Exists(Path.Combine(PayloadDirectory, "BepInEx", "plugins", "RailRouteAssistant.dll")) &&
                File.Exists(Path.Combine(PayloadDirectory, "winhttp.dll")) &&
                File.Exists(Path.Combine(PayloadDirectory, "doorstop_config.ini"));
        }

        private static bool InstallPayloadIfNeeded(string gameDirectory)
        {
            string sourcePlugin = Path.Combine(PayloadDirectory, "BepInEx", "plugins", "RailRouteAssistant.dll");
            string targetPlugin = Path.Combine(gameDirectory, "BepInEx", "plugins", "RailRouteAssistant.dll");
            bool hasBepInEx =
                File.Exists(Path.Combine(gameDirectory, "BepInEx", "core", "BepInEx.dll")) &&
                File.Exists(Path.Combine(gameDirectory, "winhttp.dll")) &&
                File.Exists(Path.Combine(gameDirectory, "doorstop_config.ini"));
            bool pluginNeedsUpdate = !File.Exists(targetPlugin) || !FilesEqual(sourcePlugin, targetPlugin);
            if (hasBepInEx && !pluginNeedsUpdate) return false;

            if (!hasBepInEx)
            {
                CopyDirectory(PayloadDirectory, gameDirectory, overwrite: false);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPlugin));
            File.Copy(sourcePlugin, targetPlugin, overwrite: true);
            return true;
        }

        private static void CopyDirectory(string sourceDirectory, string targetDirectory, bool overwrite)
        {
            foreach (string directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(sourceDirectory, directory);
                Directory.CreateDirectory(Path.Combine(targetDirectory, relative));
            }

            foreach (string file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(sourceDirectory, file);
                string target = Path.Combine(targetDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                if (overwrite || !File.Exists(target)) File.Copy(file, target, overwrite);
            }
        }

        private static bool FilesEqual(string left, string right)
        {
            var leftInfo = new FileInfo(left);
            var rightInfo = new FileInfo(right);
            if (leftInfo.Length != rightInfo.Length) return false;

            using var sha = SHA256.Create();
            using var leftStream = File.OpenRead(left);
            byte[] leftHash = sha.ComputeHash(leftStream);
            using var rightStream = File.OpenRead(right);
            byte[] rightHash = sha.ComputeHash(rightStream);
            return leftHash.SequenceEqual(rightHash);
        }

        private static bool RelaunchElevated(string gameDirectory)
        {
            try
            {
                string executable = Environment.ProcessPath;
                Process.Start(new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = $"{ElevatedInstallArgument} \"{gameDirectory}\"",
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = AppContext.BaseDirectory
                });
                return false;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"安装需要管理员权限，但未能完成提权：{ex.Message}",
                    "Rail Route 调度助手",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return false;
            }
        }

        private static void LaunchGame(string gameDirectory)
        {
            if (Process.GetProcessesByName("Rail Route").Length > 0) return;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = $"steam://rungameid/{SteamAppId}",
                    UseShellExecute = true
                });
            }
            catch
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(gameDirectory, GameExecutableName),
                    WorkingDirectory = gameDirectory,
                    UseShellExecute = true
                });
            }
        }

        private static bool IsGameDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try
            {
                return File.Exists(Path.Combine(path, GameExecutableName)) &&
                    Directory.Exists(Path.Combine(path, "Rail Route_Data", "Managed"));
            }
            catch { return false; }
        }

        private static Settings LoadSettings()
        {
            try
            {
                return File.Exists(SettingsPath)
                    ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath))
                    : null;
            }
            catch { return null; }
        }

        private static void SaveSettings(string gameDirectory)
        {
            Directory.CreateDirectory(SettingsDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(
                new Settings { GameDirectory = Path.GetFullPath(gameDirectory) },
                new JsonSerializerOptions { WriteIndented = true }));
        }

        private sealed class Settings
        {
            public string GameDirectory { get; set; }
        }
    }
}
