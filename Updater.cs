using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Logging;
using UnityEngine.Networking;

namespace MP3Client {
    internal static class UpdaterConfig {
        public const string RELEASE_URL = "https://api.github.com/repos/02huydini/MP3Client/releases/latest";
        public const string PLUGIN_VERSION = "0.6.1";
        public const string TAG = "MP3Client";
    }
    internal static class Updater {
        private static string DllPath { get { return Assembly.GetExecutingAssembly().Location; } }
        private static string PendingPath { get { return DllPath + ".pending"; } }
        private static string BackupPath { get { return DllPath + ".bak"; } }
        private static bool Promote() {
            if (!File.Exists(PendingPath)) return false;
            if (File.Exists(BackupPath)) File.Delete(BackupPath);
            File.Move(DllPath, BackupPath);
            try {
                File.Move(PendingPath, DllPath);
            } catch (Exception) {
                File.Move(BackupPath, DllPath);
                throw;
            }
            return true;
        }
        public static void ApplyPendingIfAny(ManualLogSource log) {
            string tag = UpdaterConfig.TAG;
            if (!File.Exists(PendingPath)) return;
            try {
                if (Promote()) {
                    CleanupBackup(log, tag);
                    log.LogInfo("[" + tag + "] applied pending update, now running the version downloaded last session.");
                }
            } catch (Exception e) {
                log.LogWarning("[" + tag + "] failed to apply pending update: " + e.Message);
            }
        }
        private static void CleanupBackup(ManualLogSource log, string tag) {
            foreach (string path in new[] { BackupPath, PendingPath + ".tmp", Path.Combine(Paths.ConfigPath, "mp3client.cfg") }) {
                try {
                    if (File.Exists(path)) File.Delete(path);
                } catch (Exception e) {
                    log.LogWarning("[" + tag + "] failed to remove " + Path.GetFileName(path) + ": " + e.Message);
                }
            }
        }
        public static IEnumerator CheckForUpdate(ManualLogSource log) {
            string tag = UpdaterConfig.TAG;
            string releaseUrl = UpdaterConfig.RELEASE_URL;
            string currentVersion = UpdaterConfig.PLUGIN_VERSION;
            if (File.Exists(PendingPath)) yield break;
            using (UnityWebRequest www = UnityWebRequest.Get(releaseUrl)) {
                www.SetRequestHeader("User-Agent", "bepinex-plugin-updater");
                www.SetRequestHeader("Accept", "application/vnd.github+json");
                yield return www.SendWebRequest();
                if (www.result != UnityWebRequest.Result.Success) {
                    log.LogWarning("[" + tag + "] update check failed: " + www.error);
                    yield break;
                }
                string json = www.downloadHandler.text;
                string remoteTag = ExtractField(json, "tag_name");
                if (string.IsNullOrEmpty(remoteTag)) yield break;
                string remoteVersion = remoteTag.TrimStart('v', 'V');
                Version local, remote;
                if (!Version.TryParse(currentVersion, out local) || !Version.TryParse(remoteVersion, out remote)) yield break;
                if (remote.CompareTo(local) <= 0) yield break;
                log.LogInfo("[" + tag + "] running " + currentVersion + ", found " + remoteVersion + " available.");
                string dllUrl = ExtractField(json, "browser_download_url", ".dll");
                if (string.IsNullOrEmpty(dllUrl)) {
                    log.LogWarning("[" + tag + "] update " + remoteVersion + " found but release has no .dll asset.");
                    yield break;
                }
                using (UnityWebRequest dl = UnityWebRequest.Get(dllUrl)) {
                    yield return dl.SendWebRequest();
                    if (dl.result != UnityWebRequest.Result.Success) {
                        log.LogWarning("[" + tag + "] update download failed: " + dl.error);
                        yield break;
                    }
                    try {
                        string tempPath = PendingPath + ".tmp";
                        File.WriteAllBytes(tempPath, dl.downloadHandler.data);
                        if (File.Exists(PendingPath)) File.Delete(PendingPath);
                        File.Move(tempPath, PendingPath);
                    } catch (Exception e) {
                        log.LogWarning("[" + tag + "] failed to save update: " + e.Message);
                        try { if (File.Exists(PendingPath + ".tmp")) File.Delete(PendingPath + ".tmp"); } catch (Exception) { }
                        yield break;
                    }
                    bool installedNow = false;
                    try {
                        installedNow = Promote();
                    } catch (Exception e) {
                        log.LogWarning("[" + tag + "] immediate promote failed, will retry next launch: " + e.Message);
                    }
                    if (installedNow) {
                        CleanupBackup(log, tag);
                        log.LogInfo("[" + tag + "] update " + remoteVersion + " installed, will be active next launch.");
                    } else {
                        log.LogInfo("[" + tag + "] update " + remoteVersion + " downloaded, will be applied on next launch.");
                    }
                }
            }
        }
        private static string ExtractField(string json, string key, string suffix = null) {
            var pattern = "\"" + key + "\"\\s*:\\s*\"([^\"]+" + (suffix != null ? Regex.Escape(suffix) : "") + ")\"";
            var m = Regex.Match(json, pattern);
            return m.Success ? m.Groups[1].Value : null;
        }
    }
}