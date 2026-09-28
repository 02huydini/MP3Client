using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using KrokoshaCasualtiesMP;

namespace MP3Client {
    internal class MP3ClientLog {
        protected const string TAG = "[debug] ";
        public virtual void Info(string message) { MP3Client.Log.LogInfo(TAG + message); }
        public virtual void Warn(string message) { MP3Client.Log.LogWarning(TAG + message); }
        public virtual void Error(string message) { MP3Client.Log.LogError(TAG + message); }
        public virtual void SendStart(ushort syncId, string fileName, int totalBytes) { }
        public virtual void SendChunk(ushort syncId, int chunkIndex, int offset, int length, float elapsedSeconds, float sinceLastSeconds) { }
        public virtual void SendChunkFailed(ushort syncId, int chunkIndex, int offset, Exception e) { Error("send chunk FAILED syncId=" + syncId + " #" + chunkIndex + " offset=" + offset + " : " + e); }
        public virtual void SendProgress(ushort syncId, int sentBytes, int totalBytes, float elapsedSeconds) { }
        public virtual void SendDone(ushort syncId, int totalBytes, float elapsedSeconds) { }
        public virtual void SendCoroutineExit(ushort syncId, string reason) { }
        public virtual void RecvBegin(ushort syncId, int totalBytes, string ext) { }
        public virtual void RecvProgress(ushort syncId, int receivedChunks, int totalChunks, float elapsedSeconds) { }
        public virtual void RecvStalled(ushort syncId, int resendFromOffset) { Warn("recv stalled syncId=" + syncId + " - requesting resend from offset " + resendFromOffset); }
        public virtual void RecvDone(ushort syncId, int totalBytes, float elapsedSeconds) { }
        public virtual void RecvCancelled(ushort syncId) { }
        public virtual void RecvDeadlineHit(ushort syncId) { Warn("recv deadline hit syncId=" + syncId + " - finalizing with whatever arrived, likely incomplete/corrupt file"); }
        public virtual void LogItemComponents(object item) { }
        public virtual void GateDecision(bool allowed, string reason, bool networkRunning, bool isServer, bool isClient, bool autoLoadMusic, bool pendingApproval) { }
        public virtual void Trace(string stage, string detail) { }
        public virtual void UIState(string stage, string detail) { }
        public virtual void NetState(string stage, string detail) { }
        public virtual void PatchedMethodsSummary(List<MethodBase> methods) { }
        public virtual void StateDump(bool networkRunning, bool isServer, bool isClient, bool autoLoadMusic, bool autoRequestMusic, float uiVerticalOffset, bool uiOnTop, bool coloredButtons, string hostPluginStatus, bool pendingActive, string pendingRequester, string customMusicFolder, int customMusicSongCount, int gatedUIControlsCount, bool libDropdownBuilt, bool mp3NoAutoLoadNeutralized, bool deviceFolderLoadTriggered, bool wasNetworkRunning) { }
    }
    [BepInPlugin("02dumbass.mp3client", "MP3Client", "0.6.1")]
    [BepInDependency("KrokoshaCasualtiesMP", BepInDependency.DependencyFlags.HardDependency)]
    public class MP3Client : BaseUnityPlugin {
        internal static ManualLogSource Log;
        internal static MP3Client Instance;
        internal static MP3ClientLog Debug = new MP3ClientLog();
        private static readonly ConcurrentQueue<Action> mainThreadQueue = new ConcurrentQueue<Action>();
        internal static ConfigEntry<bool> AutoLoadMusic;
        internal static ConfigEntry<bool> LoadMusicOnLaunch;
        internal static ConfigEntry<bool> AutoRequestMusic;
        internal static ConfigEntry<float> UIVerticalOffset;
        internal static ConfigEntry<bool> UIOnTop;
        internal static ConfigEntry<bool> ColoredButtons;

        public void Awake() {
            Log = Logger;
            Instance = this;
            AutoLoadMusic = Config.Bind("General", "AutoLoadMusic", false, "If false, the MP3 player's on-disk device music folder is never auto-scanned. Base game bundled music is unaffected. A client can ask the host to approve a one-time load instead.");
            LoadMusicOnLaunch = Config.Bind("General", "LoadMusicOnLaunch", false, "Host only. If true, the device music folder is loaded automatically when a multiplayer session starts, with no approval prompt.");
            AutoRequestMusic = Config.Bind("General", "AutoRequestMusic", true, "Host only. If true, automatically ask to load custom music when a multiplayer session starts. The MP3load command always works regardless of this setting.");
            UIOnTop = Config.Bind("UI", "UIOnTop", false, "If true, anchors the MP3Client UI block to the top of the screen instead of the center.");
            ColoredButtons = Config.Bind("UI", "ColoredButtons", false, "If true, the Stop and Pause buttons will have red and orange background tints.");
            UIVerticalOffset = Config.Bind("UI", "UIVerticalOffset", 80f, "Pixels to shift the MP3Client dropdown/button block downward, so it clears the base game's own dropdown and label. Increase if they still overlap. Raised from 40 to 80 after 40 was reported as still overlapping.");

            TryUpgradeDebugLogger();
            Updater.ApplyPendingIfAny(Log);
            var harmony = new Harmony("mp3client");
            harmony.PatchAll(typeof(MP3Client).Assembly);
            TryReconcileMP3NoAutoLoad(harmony);
            LogPatchedMethods(harmony);
            StartCoroutine(Updater.CheckForUpdate(Log));
            StartCoroutine(DeferredRegisterCommands());
        }
        private static void LogPatchedMethods(Harmony harmony) {
            MP3Client.Debug.PatchedMethodsSummary(harmony.GetPatchedMethods().ToList());
        }
        internal static bool MP3NoAutoLoadNeutralized;
        private static void TryReconcileMP3NoAutoLoad(Harmony harmony) {
            try {
                var noAutoLoadType = AccessTools.TypeByName("MP3NoAutoLoad.MP3Menu_LoadAllMusic_BlockDevicePatch");
                if (noAutoLoadType == null) {
                    MP3Client.Debug.Trace("TryReconcileMP3NoAutoLoad", "MP3NoAutoLoad plugin not detected, nothing to reconcile");
                    return;
                }
                var prefixMethod = AccessTools.Method(noAutoLoadType, "Prefix");
                if (prefixMethod == null) {
                    Log.LogWarning("mp3client: MP3NoAutoLoad detected but its Prefix method wasn't found by reflection - it may still independently block LoadAllMusic even after MP3Client approves. See README.");
                    return;
                }
                var ourPrefixMethod = AccessTools.Method(typeof(ReconcileMP3NoAutoLoadPrefix), "Prefix");
                harmony.Patch(prefixMethod, prefix: new HarmonyMethod(ourPrefixMethod));
                MP3NoAutoLoadNeutralized = true;
                Log.LogInfo("mp3client: MP3NoAutoLoad detected - neutralized its independent LoadAllMusic gate, MP3Client's own approval flow is now the sole authority");
            } catch (Exception e) {
                Log.LogWarning("mp3client: TryReconcileMP3NoAutoLoad failed: " + e.Message);
            }
        }
        private IEnumerator DeferredRegisterCommands() {
            while (!SceneManager.GetActiveScene().isLoaded) yield return null;
            yield return null;
            TryRegisterCommands();
        }
        private static void TryUpgradeDebugLogger() {
            var richType = Type.GetType("MP3Client.MP3ClientDebug");
            if (richType == null || !typeof(MP3ClientLog).IsAssignableFrom(richType)) return;
            try {
                Debug = (MP3ClientLog)Activator.CreateInstance(richType);
            } catch (Exception e) {
                Log.LogWarning("mp3client: optional debug logger present but failed to load: " + e.Message);
            }
        }
        internal static void TryRegisterCommands() {
            var cmdType = Type.GetType("MP3Client.MP3ClientCommands");
            if (cmdType == null) return;
            var register = cmdType.GetMethod("RegisterAll", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (register == null) return;
            try {
                register.Invoke(null, null);
            } catch (Exception e) {
                Log.LogWarning("mp3client: optional console commands present but failed to register: " + e.Message);
            }
        }
        internal static void PostHudOnlyStatus(string msg) {
            KrokoshaScavMultiplayer.multiplayer_status_message = msg;
            KrokoshaScavMultiplayer.last_multiplayer_status_message_change_time = Time.realtimeSinceStartupAsDouble;
        }
        private static bool wasNetworkRunning;
        public void Update() {
            Action a;
            while (mainThreadQueue.TryDequeue(out a)) a();
            bool nowRunning = KrokoshaScavMultiplayer.network_system_is_running;
            if (nowRunning && !wasNetworkRunning) {
                ResetPerSessionState();
            }
            wasNetworkRunning = nowRunning;
        }
        private static void ResetPerSessionState() {
            Debug.Trace("ResetPerSessionState", "new session detected (network_system_is_running false->true) - resetting per-session gate/approval/UI-detection state so it doesn't carry over from a previous session in the same game process");
            HeadlessMusicLoader.ResetForNewSession();
            HostPluginCheck.ResetForNewSession();
            Patch_RegisterServerReceivers.ResetForNewSession();
            Patch_BuildSeparateSongUI.ResetForNewSession();
            Patch_GateNativeMusicListRequest.ResetForNewSession();
            if (KrokoshaScavMultiplayer.is_server) {
                HeadlessMusicLoader.EnsureBaseGamePopulated();
                if (LoadMusicOnLaunch.Value) {
                    Log.LogInfo("mp3client: LoadMusicOnLaunch is on - loading device folder music without a prompt");
                    HeadlessMusicLoader.EnsureDeviceFolderPopulated();
                } else {
                    SendPendingRequest(true);
                }
            }
        }
        internal static void SendPendingRequest(bool automatic) {
            if (KrokoshaScavMultiplayer.is_server) {
                if (automatic && !AutoRequestMusic.Value) {
                    Log.LogInfo("mp3client: host self launch-trigger skipped - AutoRequestMusic is off");
                    return;
                }
                Log.LogInfo("mp3client: host self-triggered load, automatic=" + automatic);
                Patch_RegisterServerReceivers.Server_HandleLoadRequest(null);
            } else {
                Log.LogInfo("mp3client: sending pending request to host, automatic=" + automatic);
                var writer = Net.CreateWriter(Protocol.MSG_ASK_LOAD);
                writer.Put(automatic);
                var dm = DeliveryMethod.ReliableOrdered;
                Net.Client_Send(in dm, in writer);
            }
        }
        internal static void DumpState() {
            Debug.StateDump(
                KrokoshaScavMultiplayer.network_system_is_running,
                KrokoshaScavMultiplayer.is_server,
                KrokoshaScavMultiplayer.is_client,
                AutoLoadMusic.Value,
                AutoRequestMusic.Value,
                UIVerticalOffset.Value,
                UIOnTop.Value,
                ColoredButtons.Value,
                HostPluginCheck.CurrentStatus.ToString(),
                Patch_RegisterServerReceivers.pendingActive,
                Patch_RegisterServerReceivers.pendingRequester.HasValue ? Patch_RegisterServerReceivers.pendingRequester.Value.ToString() : "none",
                CustomMusicFolder.GetPath(),
                CustomMusicFolder.Scan().Count,
                LocalLibraryUI.HostGatedControls.Count,
                LocalLibraryUI.CurrentLibDropdown != null,
                MP3NoAutoLoadNeutralized,
                HeadlessMusicLoader.DeviceFolderLoadTriggered,
                wasNetworkRunning
            );
        }
        internal static void RunOnMainThread(Action a) {
            mainThreadQueue.Enqueue(a);
        }
    }
    internal static class ReconcileMP3NoAutoLoadPrefix {
        internal static bool Prefix(ref bool __result) {
            __result = true;
            return false;
        }
    }
}