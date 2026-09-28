using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using KrokoshaCasualtiesMP;

namespace MP3Client {
    internal class MP3ClientDebug : MP3ClientLog {
        public override void SendStart(ushort syncId, string fileName, int totalBytes) {
            Info("send start syncId=" + syncId + " file=\"" + fileName + "\" bytes=" + totalBytes);
        }
        public override void SendChunk(ushort syncId, int chunkIndex, int offset, int length, float elapsedSeconds, float sinceLastSeconds) {
            Info("send chunk syncId=" + syncId + " #" + chunkIndex + " offset=" + offset + " len=" + length + " t=" + elapsedSeconds.ToString("F2") + "s (+" + sinceLastSeconds.ToString("F2") + "s)");
        }
        public override void SendProgress(ushort syncId, int sentBytes, int totalBytes, float elapsedSeconds) {
            float rateKBs = elapsedSeconds > 0f ? (sentBytes / 1024f) / elapsedSeconds : 0f;
            Info("send progress syncId=" + syncId + " " + sentBytes + "/" + totalBytes + " bytes, " + elapsedSeconds.ToString("F1") + "s elapsed, " + rateKBs.ToString("F1") + " KB/s avg");
        }
        public override void SendDone(ushort syncId, int totalBytes, float elapsedSeconds) {
            float rateKBs = elapsedSeconds > 0f ? (totalBytes / 1024f) / elapsedSeconds : 0f;
            Info("send done syncId=" + syncId + " " + totalBytes + " bytes in " + elapsedSeconds.ToString("F1") + "s, " + rateKBs.ToString("F1") + " KB/s avg");
        }
        public override void SendCoroutineExit(ushort syncId, string reason) {
            Info("send coroutine exit syncId=" + syncId + " reason=" + reason);
        }
        public override void RecvBegin(ushort syncId, int totalBytes, string ext) {
            Info("recv begin syncId=" + syncId + " ext=" + ext + " bytes=" + totalBytes);
        }
        public override void RecvProgress(ushort syncId, int receivedChunks, int totalChunks, float elapsedSeconds) {
            Info("recv progress syncId=" + syncId + " chunks " + receivedChunks + "/" + totalChunks + ", " + elapsedSeconds.ToString("F1") + "s elapsed");
        }
        public override void RecvDone(ushort syncId, int totalBytes, float elapsedSeconds) {
            float rateKBs = elapsedSeconds > 0f ? (totalBytes / 1024f) / elapsedSeconds : 0f;
            Info("recv done syncId=" + syncId + " " + totalBytes + " bytes in " + elapsedSeconds.ToString("F1") + "s, " + rateKBs.ToString("F1") + " KB/s avg");
        }
        public override void RecvCancelled(ushort syncId) {
            Info("recv cancelled syncId=" + syncId);
        }
        public override void GateDecision(bool allowed, string reason, bool networkRunning, bool isServer, bool isClient, bool autoLoadMusic, bool pendingApproval) {
            string verdict = allowed ? "ALLOWED" : "BLOCKED";
            Info("gate " + verdict + " device folder music load - " + reason);
            Info("gate state: network_system_is_running=" + networkRunning + " is_server=" + isServer + " is_client=" + isClient + " AutoLoadMusic=" + autoLoadMusic + " pendingApproval=" + pendingApproval);
            if (!allowed) Info("gate hint: run MP3load to ask the host, or set AutoLoadMusic=true in the config to skip the gate entirely");
        }
        public override void Trace(string stage, string detail) {
            Info("trace [" + stage + "] " + detail);
        }
        public override void UIState(string stage, string detail) {
            Info("ui [" + stage + "] " + detail);
        }
        public override void NetState(string stage, string detail) {
            Info("net [" + stage + "] " + detail);
        }
        public override void LogItemComponents(object item) {
            var comp = item as Component;
            if (comp == null) {
                Warn("item is not a Component, cannot enumerate - type=" + (item != null ? item.GetType().FullName : "null"));
                return;
            }
            var all = comp.GetComponents<Component>();
            var names = new string[all.Length];
            for (int i = 0; i < all.Length; i++) names[i] = all[i] != null ? all[i].GetType().FullName : "null";
            Info("item components after play: [" + string.Join(", ", names) + "]");
        }
        public override void PatchedMethodsSummary(List<MethodBase> methods) {
            Trace("Awake", "Harmony patched " + methods.Count + " method(s):");
            foreach (var m in methods) {
                Trace("Awake", "  " + (m.DeclaringType != null ? m.DeclaringType.FullName : "?") + "." + m.Name);
            }
        }
        public override void StateDump(bool networkRunning, bool isServer, bool isClient, bool autoLoadMusic, bool autoRequestMusic, float uiVerticalOffset, bool uiOnTop, bool coloredButtons, string hostPluginStatus, bool pendingActive, string pendingRequester, string customMusicFolder, int customMusicSongCount, int gatedUIControlsCount, bool libDropdownBuilt, bool mp3NoAutoLoadNeutralized, bool deviceFolderLoadTriggered, bool wasNetworkRunning) {
            Trace("DumpState", "--- MP3Client state dump ---");
            Trace("DumpState", "network_system_is_running=" + networkRunning + " is_server=" + isServer + " is_client=" + isClient);
            Trace("DumpState", "config AutoLoadMusic=" + autoLoadMusic + " AutoRequestMusic=" + autoRequestMusic + " UIVerticalOffset=" + uiVerticalOffset + " UIOnTop=" + uiOnTop + " ColoredButtons=" + coloredButtons);
            Trace("DumpState", "host plugin check status=" + hostPluginStatus);
            Trace("DumpState", "pending request active=" + pendingActive + " requester=" + pendingRequester);
            Trace("DumpState", "custommusic folder=" + customMusicFolder + " songs=" + customMusicSongCount);
            Trace("DumpState", "gated UI controls tracked=" + gatedUIControlsCount + " libDropdown built=" + libDropdownBuilt);
            Trace("DumpState", "MP3NoAutoLoad neutralized=" + mp3NoAutoLoadNeutralized);
            Trace("DumpState", "deviceFolderLoadTriggered=" + deviceFolderLoadTriggered + " wasNetworkRunning=" + wasNetworkRunning);
            Trace("DumpState", "--- end state dump ---");
        }
    }
    internal static class MP3ClientCommands {
        internal static void RegisterAll() {
            Con.RegisterCommand(new Command("MP3stop", "MP3Client - stop your current mp3 transfer/playback.", delegate (string[] splited) {
                if (!KrokoshaScavMultiplayer.is_client) return;
                bool cancelled = LocalLibraryUI.CancelActiveGlobal();
                ushort playerSyncId;
                if (Patch_InterceptBrowsePlay.TryGetCurrentPlayerSyncId(out playerSyncId)) Patch_InterceptBrowsePlay.SendCancel(playerSyncId);
                KrokoshaScavMultiplayer.DoMultiplayerStatusMessageLog(cancelled ? "mp3client: MP3stop - dropped current transfer and stopped playback." : "mp3client: MP3stop - stopped playback.");
            }, null, Array.Empty<ValueTuple<string, string>>()));
            Con.RegisterCommand(new Command("MP3load", "MP3Client - CLIENT ONLY - ask the host to load their music now.", delegate (string[] splited) {
                if (!KrokoshaScavMultiplayer.is_client || KrokoshaScavMultiplayer.is_server) return;
                MP3Client.SendPendingRequest(false);
                KrokoshaScavMultiplayer.DoMultiplayerStatusMessageLog("mp3client: MP3load - sent pending request to host.");
            }, null, Array.Empty<ValueTuple<string, string>>()));
            Con.RegisterCommand(new Command("MP3accept", "MP3Client - HOST ONLY - approve the pending music-load request.", delegate (string[] splited) {
                if (!KrokoshaScavMultiplayer.is_server) return;
                Patch_RegisterServerReceivers.AcceptPendingRequest();
            }, null, Array.Empty<ValueTuple<string, string>>()));
            Con.RegisterCommand(new Command("MP3reject", "MP3Client - HOST ONLY - deny the pending music-load request.", delegate (string[] splited) {
                if (!KrokoshaScavMultiplayer.is_server) return;
                Patch_RegisterServerReceivers.RejectPendingRequest();
            }, null, Array.Empty<ValueTuple<string, string>>()));
            Con.RegisterCommand(new Command("MP3state", "MP3Client - dump full plugin state to the log for debugging.", delegate (string[] splited) {
                MP3Client.DumpState();
            }, null, Array.Empty<ValueTuple<string, string>>()));
        }
    }
}