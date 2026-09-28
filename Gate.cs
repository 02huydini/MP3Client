using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;
using UnityEngine.Networking;
using KrokoshaCasualtiesMP;

namespace MP3Client {
    [HarmonyPatch]
    internal static class Patch_GateDeviceMusicLoad {
        internal static float BypassUntil;
        static MethodBase TargetMethod() {
            var t = UITypes.MP3Menu;
            return AccessTools.Method(t, "LoadAllMusic");
        }
        [HarmonyPriority(Priority.Last)]
        static bool Prefix(ref IEnumerator __result) {
            if (!KrokoshaScavMultiplayer.network_system_is_running) {
                MP3Client.Debug.GateDecision(true, "no active session (singleplayer) - nobody to gate against", KrokoshaScavMultiplayer.network_system_is_running, KrokoshaScavMultiplayer.is_server, KrokoshaScavMultiplayer.is_client, MP3Client.AutoLoadMusic.Value, HeadlessMusicLoader.DeviceFolderLoadTriggered);
                return true;
            }
            if (MP3Client.AutoLoadMusic.Value) {
                MP3Client.Debug.GateDecision(true, "AutoLoadMusic config is true", KrokoshaScavMultiplayer.network_system_is_running, KrokoshaScavMultiplayer.is_server, KrokoshaScavMultiplayer.is_client, MP3Client.AutoLoadMusic.Value, HeadlessMusicLoader.DeviceFolderLoadTriggered);
                return true;
            }
            if (Time.unscaledTime < BypassUntil) {
                MP3Client.Debug.GateDecision(true, "within the Server_ForceLoadMusic bypass window (loadcustommusic command or native msg-10136 auto-recovery)", KrokoshaScavMultiplayer.network_system_is_running, KrokoshaScavMultiplayer.is_server, KrokoshaScavMultiplayer.is_client, MP3Client.AutoLoadMusic.Value, HeadlessMusicLoader.DeviceFolderLoadTriggered);
                return true;
            }
            MP3Client.Debug.GateDecision(false, "device folder music loads through the approval flow (headless), not the native path - accept the pending prompt, run MP3load/MP3accept, or enable AutoLoadMusic", KrokoshaScavMultiplayer.network_system_is_running, KrokoshaScavMultiplayer.is_server, KrokoshaScavMultiplayer.is_client, MP3Client.AutoLoadMusic.Value, HeadlessMusicLoader.DeviceFolderLoadTriggered);
            __result = Noop();
            return false;
        }
        private static IEnumerator Noop() {
            yield break;
        }
    }
    [HarmonyPatch(typeof(MP3PlayerServerAudioStreamer), "Server_ForceLoadMusic")]
    internal static class Patch_BypassForceLoadMusic {
        static void Prefix() {
            Patch_GateDeviceMusicLoad.BypassUntil = Time.unscaledTime + 2f;
            MP3Client.Debug.Trace("Patch_BypassForceLoadMusic", "Server_ForceLoadMusic invoked (loadcustommusic command or native list-request auto-recovery) - opening a 2s bypass window for the device folder gate, since this call path is already host-only/authorized upstream of here");
            Patch_RegisterServerReceivers.DismissPendingPromptIfAny("loadcustommusic/native auto-recovery bypass fired");
        }
    }
    [HarmonyPatch(typeof(MP3Menu_UpdateList_MultiplayerPatch), "Prefix")]
    internal static class Patch_GateNativeMusicListRequest {
        private const ushort NATIVE_MSG_REQUEST_MUSIC_LIST = 10136;
        internal static bool ClientApproved;
        internal static void ResetForNewSession() {
            ClientApproved = false;
        }
        [HarmonyPriority(Priority.First)]
        static bool Prefix(object __instance, ref bool __result) {
            if (!KrokoshaScavMultiplayer.network_system_is_running) return true;
            if (KrokoshaScavMultiplayer.is_server) return true;
            if (!KrokoshaScavMultiplayer.rules.EnableMP3Sync) { MP3Client.Debug.NetState("Patch_GateNativeMusicListRequest", "EnableMP3Sync is false, passing through to native unchanged"); return true; }
            var dropdownListField = AccessTools.Field(UITypes.MP3Menu, "dropdownList");
            if (dropdownListField != null && dropdownListField.GetValue(null) != null) { MP3Client.Debug.NetState("Patch_GateNativeMusicListRequest", "dropdownList already populated, passing through to native unchanged (native will no-op)"); return true; }
            bool approved = MP3Client.AutoLoadMusic.Value || ClientApproved;
            if (approved) { MP3Client.Debug.NetState("Patch_GateNativeMusicListRequest", "already approved, passing through to native - it will send the request"); return true; }
            MP3Client.Debug.NetState("Patch_GateNativeMusicListRequest", "suppressed native 'request server music list' (msg " + NATIVE_MSG_REQUEST_MUSIC_LIST + ") - no approval yet, routing through our own pending-request system instead");
            MP3Client.SendPendingRequest(false);
            __result = true;
            return false;
        }
        internal static void TrySendNativeMusicListRequest() {
            try {
                var writer = Net.CreateWriter(NATIVE_MSG_REQUEST_MUSIC_LIST);
                writer.Put(1);
                var dm = DeliveryMethod.ReliableUnordered;
                Net.Client_Send(in dm, in writer);
                MP3Client.Debug.NetState("TrySendNativeMusicListRequest", "sent native 'request server music list' (msg " + NATIVE_MSG_REQUEST_MUSIC_LIST + ") now that approval was granted");
            } catch (Exception e) {
                MP3Client.Log.LogWarning("mp3client: TrySendNativeMusicListRequest failed: " + e.Message);
            }
        }
    }
    internal static class HeadlessMusicLoader {
        internal static bool DeviceFolderLoadTriggered;
        internal static void ResetForNewSession() {
            DeviceFolderLoadTriggered = false;
        }
        internal static void EnsureBaseGamePopulated() {
            try {
                var menuType = UITypes.MP3Menu;
                var dropdownListField = AccessTools.Field(menuType, "dropdownList");
                if (dropdownListField != null && dropdownListField.GetValue(null) != null) {
                    MP3Client.Debug.Trace("EnsureBaseGamePopulated", "MP3Menu.clips/dropdownList already populated (a real menu was opened this session, or this already ran), nothing to do");
                    return;
                }
                MP3Client.Debug.Trace("EnsureBaseGamePopulated", "populating MP3Menu.clips/dropdownList with base game music headlessly, ungated, so any client can play base-dropdown songs without the host ever opening their own menu or approving device-folder music");
                var clipsField = AccessTools.Field(menuType, "clips");
                var clips = Resources.LoadAll<AudioClip>("Sounds/music").ToList();
                clipsField.SetValue(null, clips);
                PopulatePlaceholderDropdownList(menuType, dropdownListField, clips);
            } catch (Exception e) {
                MP3Client.Log.LogWarning("mp3client: EnsureBaseGamePopulated failed: " + e.Message);
            }
        }
        internal static void EnsureDeviceFolderPopulated() {
            DeviceFolderLoadTriggered = true;
            try {
                EnsureBaseGamePopulated();
                var menuType = UITypes.MP3Menu;
                string folder = Path.Combine(Application.dataPath, "custommusic");
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                MP3Client.Instance.StartCoroutine(HeadlessLoadAllMusic(menuType, folder));
            } catch (Exception e) {
                MP3Client.Log.LogWarning("mp3client: EnsureDeviceFolderPopulated failed: " + e.Message);
            }
        }
        private static void PopulatePlaceholderDropdownList(Type menuType, FieldInfo dropdownListField, List<AudioClip> clips) {
            try {
                var optionDataType = AccessTools.TypeByName("TMPro.TMP_Dropdown+OptionData");
                var ctor = optionDataType != null ? optionDataType.GetConstructor(new[] { typeof(string) }) : null;
                var listType = typeof(List<>).MakeGenericType(optionDataType ?? typeof(object));
                var typedList = Activator.CreateInstance(listType);
                var addMethod = listType.GetMethod("Add");
                if (ctor != null) {
                    foreach (var clip in clips) {
                        var option = ctor.Invoke(new object[] { clip != null ? clip.name : "" });
                        addMethod.Invoke(typedList, new object[] { option });
                    }
                }
                dropdownListField.SetValue(null, typedList);
            } catch (Exception e) {
                MP3Client.Log.LogWarning("mp3client: PopulatePlaceholderDropdownList failed, dropdownList stays null (clips is still populated - the important part): " + e.Message);
            }
        }
        private static IEnumerator HeadlessLoadAllMusic(Type menuType, string folderPath) {
            var loadingField = AccessTools.Field(menuType, "loadingMusic");
            var clipsField = AccessTools.Field(menuType, "clips");
            var dropdownListField = AccessTools.Field(menuType, "dropdownList");
            var optionDataType = AccessTools.TypeByName("TMPro.TMP_Dropdown+OptionData");
            var optionCtor = optionDataType != null ? optionDataType.GetConstructor(new[] { typeof(string) }) : null;
            if (loadingField != null) loadingField.SetValue(null, true);
            string[] files = Array.Empty<string>();
            try {
                files = Directory.GetFiles(folderPath, "*.*");
            } catch (Exception e) {
                MP3Client.Log.LogWarning("mp3client: HeadlessLoadAllMusic - couldn't list " + folderPath + ": " + e.Message);
            }
            var clipsListObj = clipsField.GetValue(null) as System.Collections.IList;
            var dropdownListObj = dropdownListField != null ? dropdownListField.GetValue(null) as System.Collections.IList : null;
            foreach (string file in files) {
                if (!(file.EndsWith(".mp3") || file.EndsWith(".wav") || file.EndsWith(".ogg"))) continue;
                string uri = "file://" + file;
                using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(uri, AudioType.UNKNOWN)) {
                    yield return www.SendWebRequest();
                    if (www.result == UnityWebRequest.Result.Success) {
                        AudioClip content = DownloadHandlerAudioClip.GetContent(www);
                        clipsListObj.Add(content);
                        if (dropdownListObj != null && optionCtor != null) dropdownListObj.Add(optionCtor.Invoke(new object[] { Path.GetFileName(file) }));
                    } else {
                        MP3Client.Log.LogWarning("mp3client: HeadlessLoadAllMusic - failed to load " + file);
                    }
                }
            }
            if (loadingField != null) loadingField.SetValue(null, false);
            MP3Client.Debug.Trace("HeadlessLoadAllMusic", "finished, MP3Menu.clips now has " + clipsListObj.Count + " total entries, dropdownList now has " + (dropdownListObj != null ? dropdownListObj.Count.ToString() : "n/a") + " total entries");
        }
    }
}