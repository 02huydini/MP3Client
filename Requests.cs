using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.Networking;
using KrokoshaCasualtiesMP;

namespace MP3Client {
    internal static class Protocol {
        public const ushort MSG_BEGIN = 0x5A00;
        public const ushort MSG_CHUNK = 0x5A01;
        public const ushort MSG_END = 0x5A02;
        public const ushort MSG_RESEND_REQUEST = 0x5A03;
        public const ushort MSG_CANCEL = 0x5A04;
        public const ushort MSG_PLUGIN_PING = 0x5A05;
        public const ushort MSG_PLUGIN_PONG = 0x5A06;
        public const ushort MSG_ASK_LOAD = 0x5A07;
        public const ushort MSG_PAUSE = 0x5A08;
        public const ushort MSG_LOAD_APPROVED = 0x5A09;
        public const float PLUGIN_CHECK_TIMEOUT_SECONDS = 3f;
        public const int CHUNK_SIZE = 32000;
        public const int CHUNKS_PER_FRAME = 8;
        public const float STALL_TIMEOUT_SECONDS = 6f;
        public const float OVERALL_DEADLINE_SECONDS = 300f;
        public const string BROWSE_LABEL = "Play your own song?";
    }
    [HarmonyPatch]
    internal static class Patch_RegisterClientReceivers {
        static MethodBase TargetMethod() {
            var t = AccessTools.TypeByName("KrokoshaCasualtiesMP.ClientMain");
            return AccessTools.Method(t, "_RegisterClientReceivers");
        }
        static void Postfix() {
            MP3Client.Debug.NetState("Patch_RegisterClientReceivers.Postfix", "_RegisterClientReceivers returned, registering our client message handlers (MSG_RESEND_REQUEST, MSG_PLUGIN_PONG, MSG_LOAD_APPROVED)");
            var regMethod = typeof(Net).GetMethod("RegisterClientReceiver", BindingFlags.NonPublic | BindingFlags.Static);
            try {
                regMethod.Invoke(null, new object[] { Protocol.MSG_RESEND_REQUEST, (KrokoshaScavMultiplayer.KrokoshaHandleNamedMessageDelegate)OnResendRequest });
                regMethod.Invoke(null, new object[] { Protocol.MSG_PLUGIN_PONG, (KrokoshaScavMultiplayer.KrokoshaHandleNamedMessageDelegate)OnPluginPong });
                regMethod.Invoke(null, new object[] { Protocol.MSG_LOAD_APPROVED, (KrokoshaScavMultiplayer.KrokoshaHandleNamedMessageDelegate)OnLoadApproved });
            } catch (TargetInvocationException e) {
                MP3Client.Log.LogWarning("mp3client: Patch_RegisterClientReceivers.Postfix registration failed: " + e.InnerException);
            }
        }
        static void OnPluginPong(knetid sender, ref NetDataReader reader) {
            MP3Client.Debug.NetState("OnPluginPong", "received from sender=" + sender);
            HostPluginCheck.OnPong();
        }
        static void OnLoadApproved(knetid sender, ref NetDataReader reader) {
            MP3Client.Debug.NetState("OnLoadApproved", "received MSG_LOAD_APPROVED from sender=" + sender);
            MP3Client.Debug.Trace("OnLoadApproved", "host approved custom music load");
            KrokoshaScavMultiplayer.DoMultiplayerStatusMessageLog("mp3client: host approved - loading device folder music now.");
            Patch_GateNativeMusicListRequest.ClientApproved = true;
            Patch_GateNativeMusicListRequest.TrySendNativeMusicListRequest();
        }
        static void OnResendRequest(knetid sender, ref NetDataReader reader) {
            ushort syncId = reader.GetUShort();
            int fromOffset = reader.GetInt();
            MP3Client.Debug.NetState("OnResendRequest", "sender=" + sender + " syncId=" + syncId + " fromOffset=" + fromOffset);
            if (!Patch_InterceptBrowsePlay.pendingFiles.TryGetValue(syncId, out var cachedData)) {
                MP3Client.Debug.NetState("OnResendRequest", "syncId=" + syncId + " not found in pendingFiles, ignoring");
                return;
            }
            if (fromOffset >= cachedData.Bytes.Length) { MP3Client.Debug.NetState("OnResendRequest", "fromOffset=" + fromOffset + " >= length " + cachedData.Bytes.Length + ", ignoring"); return; }
            MP3Client.Instance.StartCoroutine(Patch_InterceptBrowsePlay.SendFileCoroutine(syncId, cachedData.Ext, cachedData.Bytes, fromOffset, false));
        }
    }
    internal class Transfer {
        public string Extension;
        public int TotalLength;
        public byte[] Data;
        public bool[] ChunkReceived;
        public int ReceivedChunkCount;
        public bool EndReceived;
        public bool Finalized;
        public bool WaitLoopStarted;
        public bool Cancelled;
        public knetid Sender;
    }
    [HarmonyPatch]
    internal static class Patch_RegisterServerReceivers {
        static readonly Dictionary<ushort, Transfer> transfers = new Dictionary<ushort, Transfer>();
        static readonly Dictionary<ushort, Coroutine> decoding = new Dictionary<ushort, Coroutine>();
        static MethodBase TargetMethod() {
            var t = AccessTools.TypeByName("KrokoshaCasualtiesMP.ServerMain");
            return AccessTools.Method(t, "_RegisterServerReceivers");
        }
        static void Postfix() {
            MP3Client.Debug.NetState("Patch_RegisterServerReceivers.Postfix", "_RegisterServerReceivers returned, registering our server message handlers");
            RegisterHandlers();
        }
        static void RegisterHandlers() {
            var regMethod = typeof(Net).GetMethod("RegisterServerReceiver", BindingFlags.NonPublic | BindingFlags.Static);
            TryRegister(regMethod, Protocol.MSG_BEGIN, (KrokoshaScavMultiplayer.KrokoshaHandleNamedMessageDelegate)OnBegin);
            TryRegister(regMethod, Protocol.MSG_CHUNK, (KrokoshaScavMultiplayer.KrokoshaHandleNamedMessageDelegate)OnChunk);
            TryRegister(regMethod, Protocol.MSG_END, (KrokoshaScavMultiplayer.KrokoshaHandleNamedMessageDelegate)OnEnd);
            TryRegister(regMethod, Protocol.MSG_CANCEL, (KrokoshaScavMultiplayer.KrokoshaHandleNamedMessageDelegate)OnCancel);
            TryRegister(regMethod, Protocol.MSG_PAUSE, (KrokoshaScavMultiplayer.KrokoshaHandleNamedMessageDelegate)OnPause);
            TryRegister(regMethod, Protocol.MSG_PLUGIN_PING, (KrokoshaScavMultiplayer.KrokoshaHandleNamedMessageDelegate)OnPluginPing);
            TryRegister(regMethod, Protocol.MSG_ASK_LOAD, (KrokoshaScavMultiplayer.KrokoshaHandleNamedMessageDelegate)OnPendingRequest);
            MP3Client.Debug.NetState("Patch_RegisterServerReceivers.RegisterHandlers", "done registering MSG_BEGIN/CHUNK/END/CANCEL/PAUSE/PLUGIN_PING/ASK_LOAD");
        }
        static void OnPluginPing(knetid sender, ref NetDataReader reader) {
            MP3Client.Debug.NetState("OnPluginPing", "received from sender=" + sender + ", replying with MSG_PLUGIN_PONG");
            var writer = Net.CreateWriter(Protocol.MSG_PLUGIN_PONG);
            var dm = DeliveryMethod.ReliableOrdered;
            IEnumerable<knetid> targets = new List<knetid> { sender };
            Net.Server_SendToClients(in dm, in writer, in targets);
        }
        static void TryRegister(MethodInfo regMethod, ushort id, KrokoshaScavMultiplayer.KrokoshaHandleNamedMessageDelegate del) {
            try {
                regMethod.Invoke(null, new object[] { id, del });
            } catch (TargetInvocationException) { }
        }
        internal static knetid? pendingRequester;
        internal static bool pendingActive;
        internal static void ResetForNewSession() {
            pendingRequester = null;
            pendingActive = false;
        }
        internal static void DismissPendingPromptIfAny(string reason) {
            if (!pendingActive) return;
            MP3Client.Debug.Trace("DismissPendingPromptIfAny", "clearing pending request (" + reason + ") - music is already loading via a different path");
            pendingActive = false;
            pendingRequester = null;
            TryHideNativePrompt();
        }
        private static void TryHideNativePrompt() {
            try {
                var promptType = AccessTools.TypeByName("UIChoicePrompt");
                if (promptType == null) return;
                foreach (string name in new[] { "HidePrompt", "ClosePrompt", "Hide", "Close", "Dismiss", "CancelPrompt" }) {
                    var m = AccessTools.Method(promptType, name);
                    if (m != null && m.GetParameters().Length == 0) { m.Invoke(null, null); return; }
                }
                MP3Client.Debug.Trace("TryHideNativePrompt", "no known hide method found on UIChoicePrompt - prompt state is cleared, but the visual will linger until timeout or manually clicked");
            } catch (Exception e) {
                MP3Client.Debug.Trace("TryHideNativePrompt", "couldn't hide the native prompt automatically: " + e.Message);
            }
        }
        static void OnPendingRequest(knetid sender, ref NetDataReader reader) {
            bool automatic;
            reader.Get(out automatic);
            MP3Client.Debug.NetState("OnPendingRequest", "received MSG_ASK_LOAD from " + sender + " automatic=" + automatic);
            Server_HandleLoadRequest(sender);
        }
        internal static void Server_HandleLoadRequest(knetid? sender) {
            string who = sender.HasValue ? sender.Value.ToString() : "host (self)";
            if (pendingActive) {
                MP3Client.Debug.Trace("Server_HandleLoadRequest", "load request from " + who + " supersedes previous pending request - prompt already showing, not re-queuing");
                pendingRequester = sender;
                return;
            }
            pendingRequester = sender;
            pendingActive = true;
            MP3Client.Debug.Trace("Server_HandleLoadRequest", "load request from " + who + " is now PENDING - showing prompt to host");
            KrokoshaScavMultiplayer.DoMultiplayerStatusMessageLog("mp3client: [ALERT] music load requested - type MP3accept or MP3reject, or use the prompt.");
            MP3Client.PostHudOnlyStatus("mp3client: pending music-load request - MP3accept / MP3reject");
            ShowPendingPrompt();
        }
        static void ShowPendingPrompt() {
            try {
                var prompt = new UIChoicePrompt.Prompt();
                prompt.name = "MP3Client";
                prompt.text = "A player wants to load custom music. Load it now?";
                prompt.timeout = 30f;
                prompt.defaultcanignore = true;
                var accept = new UIChoicePrompt.Prompt.PromptChoice();
                accept.text = "Accept";
                accept.action = delegate () {
                    MP3Client.Debug.Trace("ShowPendingPrompt", "Accept clicked");
                    AcceptPendingRequest();
                };
                var reject = new UIChoicePrompt.Prompt.PromptChoice();
                reject.text = "Reject";
                reject.action = delegate () {
                    MP3Client.Debug.Trace("ShowPendingPrompt", "Reject clicked");
                    RejectPendingRequest();
                };
                prompt.choices = new UIChoicePrompt.Prompt.PromptChoice[] { accept, reject };
                UIChoicePrompt.ShowPrompt(prompt);
            } catch (Exception e) {
                MP3Client.Log.LogWarning("mp3client: ShowPendingPrompt failed, falling back to MP3accept/MP3reject console commands: " + e.Message);
            }
        }
        internal static void ForceLoad() {
            if (!KrokoshaScavMultiplayer.is_server) {
                MP3Client.Debug.Trace("ForceLoad", "not the host, ignoring");
                return;
            }
            MP3Client.Debug.Trace("ForceLoad", "bypassing the pending-request system entirely - loading device folder music now");
            pendingActive = false;
            pendingRequester = null;
            HeadlessMusicLoader.EnsureDeviceFolderPopulated();
            KrokoshaScavMultiplayer.DoMultiplayerStatusMessageLog("mp3client: loadcustommusic - bypassed the pending system, loading device folder music now.");
        }
        internal static void AcceptPendingRequest() {
            if (!pendingActive) {
                MP3Client.Debug.Trace("AcceptPendingRequest", "no pending request");
                KrokoshaScavMultiplayer.DoMultiplayerStatusMessageLog("mp3client: MP3accept - no pending request.");
                return;
            }
            knetid? requester = pendingRequester;
            string who = requester.HasValue ? requester.Value.ToString() : "host (self)";
            MP3Client.Debug.Trace("AcceptPendingRequest", "approving pending request from " + who);
            pendingActive = false;
            pendingRequester = null;
            if (requester.HasValue) {
                HeadlessMusicLoader.EnsureDeviceFolderPopulated();
                SendLoadApproved(requester.Value);
                SendLoadResult(requester, true);
            } else {
                HeadlessMusicLoader.EnsureDeviceFolderPopulated();
            }
            KrokoshaScavMultiplayer.DoMultiplayerStatusMessageLog("mp3client: MP3accept - approved.");
        }
        internal static void RejectPendingRequest() {
            if (!pendingActive) {
                MP3Client.Debug.Trace("RejectPendingRequest", "no pending request");
                KrokoshaScavMultiplayer.DoMultiplayerStatusMessageLog("mp3client: MP3reject - no pending request.");
                return;
            }
            knetid? requester = pendingRequester;
            string who = requester.HasValue ? requester.Value.ToString() : "host (self)";
            MP3Client.Debug.Trace("RejectPendingRequest", "denying pending request from " + who);
            pendingActive = false;
            pendingRequester = null;
            KrokoshaScavMultiplayer.DoMultiplayerStatusMessageLog("mp3client: MP3reject - request denied.");
            SendLoadResult(requester, false);
        }
        static void SendLoadApproved(knetid target) {
            var writer = Net.CreateWriter(Protocol.MSG_LOAD_APPROVED);
            var dm = DeliveryMethod.ReliableOrdered;
            IEnumerable<knetid> targets = new List<knetid> { target };
            Net.Server_SendToClients(in dm, in writer, in targets);
        }
        static void SendLoadResult(knetid? requester, bool accepted) {
            if (!requester.HasValue) return;
            string msg = accepted ? "Host accepted your request - loading music now." : "Host rejected your request to load music.";
            try {
                var serverMainType = AccessTools.TypeByName("KrokoshaCasualtiesMP.ServerMain");
                var announceMethod = serverMainType != null ? AccessTools.Method(serverMainType, "Server_AnnounceAlert") : null;
                if (announceMethod == null) {
                    MP3Client.Log.LogWarning("mp3client: Server_AnnounceAlert not found, load-result alert not sent");
                    return;
                }
                var targets = new List<knetid> { requester.Value };
                announceMethod.Invoke(null, new object[] { msg, true, true, targets });
            } catch (Exception e) {
                MP3Client.Log.LogWarning("mp3client: SendLoadResult failed: " + e.Message);
            }
        }
        static void OnPause(knetid sender, ref NetDataReader reader) {
            ushort syncId = reader.GetUShort();
            MP3Client.Debug.NetState("OnPause", "received MSG_PAUSE from sender=" + sender + " syncId=" + syncId);
            TogglePausePlayingAudio(syncId);
        }
        internal static void TogglePausePlayingAudio(ushort syncId) {
            SyncInfo si;
            if (!NetObjectRegistry.TryGetSyncInfo((knetid)syncId, out si)) return;
            var itemProp = typeof(SyncInfo).GetProperty("item", BindingFlags.Public | BindingFlags.Instance);
            object itemObj = itemProp.GetValue(si);
            if (itemObj == null) return;
            var streamerType = AccessTools.TypeByName("KrokoshaCasualtiesMP.MP3PlayerServerAudioStreamer");
            var getCompMethod = typeof(GameObject).GetMethod("GetComponent", new[] { typeof(Type) });
            object streamerInst = getCompMethod.Invoke(((Component)itemObj).gameObject, new object[] { streamerType });
            if (streamerInst == null) return;
            var sourceField = AccessTools.Field(streamerType, "musicplayersource");
            var source = sourceField.GetValue(streamerInst) as AudioSource;
            if (source == null || source.clip == null) return;
            if (source.isPlaying) {
                source.Pause();
                MP3Client.Debug.Trace("TogglePausePlayingAudio", "paused syncId=" + syncId);
            } else if (source.time > 0f) {
                source.UnPause();
                MP3Client.Debug.Trace("TogglePausePlayingAudio", "resumed syncId=" + syncId);
            }
        }
        static void OnBegin(knetid sender, ref NetDataReader reader) {
            ushort syncId = reader.GetUShort();
            int totalLen = reader.GetInt();
            string ext = reader.GetString();
            int chunkCount = (totalLen + Protocol.CHUNK_SIZE - 1) / Protocol.CHUNK_SIZE;
            transfers[syncId] = new Transfer {
                Extension = ext,
                TotalLength = totalLen,
                Sender = sender,
                Data = new byte[totalLen],
                ChunkReceived = new bool[Math.Max(chunkCount, 1)]
            };
            MP3Client.Debug.RecvBegin(syncId, totalLen, ext);
        }
        static void OnChunk(knetid sender, ref NetDataReader reader) {
            ushort syncId = reader.GetUShort();
            int offset = reader.GetInt();
            byte[] chunk = reader.GetBytesWithLength();
            Transfer tr;
            if (!transfers.TryGetValue(syncId, out tr)) return;
            if (offset < 0 || offset + chunk.Length > tr.Data.Length) return;
            Array.Copy(chunk, 0, tr.Data, offset, chunk.Length);
            int chunkIndex = offset / Protocol.CHUNK_SIZE;
            if (chunkIndex >= 0 && chunkIndex < tr.ChunkReceived.Length && !tr.ChunkReceived[chunkIndex]) {
                tr.ChunkReceived[chunkIndex] = true;
                tr.ReceivedChunkCount++;
            }
            TryFinalize(syncId, tr);
        }
        static void OnEnd(knetid sender, ref NetDataReader reader) {
            ushort syncId = reader.GetUShort();
            Transfer tr;
            if (!transfers.TryGetValue(syncId, out tr)) return;
            tr.EndReceived = true;
            if (!TryFinalize(syncId, tr) && !tr.WaitLoopStarted) {
                tr.WaitLoopStarted = true;
                MP3Client.Instance.StartCoroutine(WaitThenFinalize(syncId, tr));
            }
        }
        static bool TryFinalize(ushort syncId, Transfer tr) {
            if (tr.Cancelled) return true;
            if (tr.Finalized) return true;
            if (!tr.EndReceived) return false;
            if (tr.ReceivedChunkCount < tr.ChunkReceived.Length) return false;
            tr.Finalized = true;
            transfers.Remove(syncId);
            FinalizeTransfer(syncId, tr);
            return true;
        }
        static int FirstMissingChunkOffset(Transfer tr) {
            for (int i = 0; i < tr.ChunkReceived.Length; i++) {
                if (!tr.ChunkReceived[i]) return i * Protocol.CHUNK_SIZE;
            }
            return tr.TotalLength;
        }
        static IEnumerator WaitThenFinalize(ushort syncId, Transfer tr) {
            int lastCount = tr.ReceivedChunkCount;
            float lastProgressTime = Time.unscaledTime;
            float overallDeadline = Time.unscaledTime + Protocol.OVERALL_DEADLINE_SECONDS;
            while (Time.unscaledTime < overallDeadline) {
                if (TryFinalize(syncId, tr)) yield break;
                if (tr.ReceivedChunkCount > lastCount) {
                    lastCount = tr.ReceivedChunkCount;
                    lastProgressTime = Time.unscaledTime;
                } else if (Time.unscaledTime - lastProgressTime > Protocol.STALL_TIMEOUT_SECONDS) {
                    int resendFrom = FirstMissingChunkOffset(tr);
                    RequestResend(tr.Sender, syncId, resendFrom);
                    lastProgressTime = Time.unscaledTime;
                }
                yield return null;
            }
            if (!tr.Finalized) {
                tr.Finalized = true;
                transfers.Remove(syncId);
                FinalizeTransfer(syncId, tr);
            }
        }
        static void RequestResend(knetid target, ushort syncId, int fromOffset) {
            var writer = Net.CreateWriter(Protocol.MSG_RESEND_REQUEST);
            writer.Put(syncId);
            writer.Put(fromOffset);
            var dm = DeliveryMethod.ReliableOrdered;
            IEnumerable<knetid> targets = new List<knetid> { target };
            Net.Server_SendToClients(in dm, in writer, in targets);
        }
        static void FinalizeTransfer(ushort syncId, Transfer tr) {
            byte[] full = tr.Data;
            string tempPath = Path.Combine(Path.GetTempPath(), "mp3client_" + syncId + tr.Extension);
            File.WriteAllBytes(tempPath, full);
            var co = MP3Client.Instance.StartCoroutine(DecodeAndPlay(tempPath, tr.Extension, syncId));
            decoding[syncId] = co;
        }
        static void OnCancel(knetid sender, ref NetDataReader reader) {
            ushort syncId = reader.GetUShort();
            Transfer tr;
            if (transfers.TryGetValue(syncId, out tr)) {
                tr.Cancelled = true;
                transfers.Remove(syncId);
            }
            Coroutine co;
            if (decoding.TryGetValue(syncId, out co)) {
                MP3Client.Instance.StopCoroutine(co);
                decoding.Remove(syncId);
            }
            MP3Client.Debug.RecvCancelled(syncId);
        }
        static IEnumerator DecodeAndPlay(string path, string ext, ushort syncId) {
            float startTime = Time.unscaledTime;
            try {
                AudioType type = AudioType.MPEG;
                if (ext == ".wav") type = AudioType.WAV;
                else if (ext == ".ogg") type = AudioType.OGGVORBIS;
                string url = "file:///" + path.Replace("\\", "/");
                using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(url, type)) {
                    yield return www.SendWebRequest();
                    if (!string.IsNullOrEmpty(www.error)) {
                        MP3Client.Log.LogError("mp3client: decode failed: " + www.error);
                        try { File.Delete(path); } catch { }
                        yield break;
                    }
                    AudioClip clip = DownloadHandlerAudioClip.GetContent(www);
                    try { File.Delete(path); } catch { }
                    SyncInfo si;
                    if (!NetObjectRegistry.TryGetSyncInfo((knetid)syncId, out si)) yield break;
                    var itemProp = typeof(SyncInfo).GetProperty("item", BindingFlags.Public | BindingFlags.Instance);
                    object itemObj = itemProp.GetValue(si);
                    var playMethod = AccessTools.TypeByName("KrokoshaCasualtiesMP.MP3Menu_Play_MultiplayerPatch")
                        .GetMethod("Server_PlayThisSongOnThisMp3Player", BindingFlags.Public | BindingFlags.Static);
                    playMethod.Invoke(null, new object[] { clip, itemObj });
                    MP3Client.Debug.RecvDone(syncId, clip != null ? clip.samples * Math.Max(clip.channels, 1) * 2 : 0, Time.unscaledTime - startTime);
                }
            } finally {
                decoding.Remove(syncId);
            }
        }
    }
}