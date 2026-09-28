using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;
using UnityEngine.Events;
using KrokoshaCasualtiesMP;

namespace MP3Client {
    [HarmonyPatch]
    internal static class Patch_BuildSeparateSongUI {
        private static readonly ConditionalWeakTable<object, object> builtFor = new ConditionalWeakTable<object, object>();
        internal static WeakReference LastMp3MenuInstance;
        internal static void ResetForNewSession() {
            LastMp3MenuInstance = null;
        }
        static MethodBase TargetMethod() {
            var t = UITypes.MP3Menu;
            return AccessTools.Method(t, "UpdateList");
        }
        static void Postfix(object __instance) {
            MP3Client.Debug.Trace("Patch_BuildSeparateSongUI.Postfix", "UpdateList returned for instance=" + __instance.GetHashCode() + " is_server=" + KrokoshaScavMultiplayer.is_server + " is_client=" + KrokoshaScavMultiplayer.is_client);
            LastMp3MenuInstance = new WeakReference(__instance);
            if (KrokoshaScavMultiplayer.is_server && KrokoshaScavMultiplayer.network_system_is_running && !MP3Client.AutoLoadMusic.Value && !HeadlessMusicLoader.DeviceFolderLoadTriggered && !Patch_RegisterServerReceivers.pendingActive) {
                MP3Client.Debug.Trace("Patch_BuildSeparateSongUI.Postfix", "host opened the menu with device folder music not yet approved - auto-firing the pending-request prompt");
                Patch_RegisterServerReceivers.Server_HandleLoadRequest(null);
            }
            object marker;
            if (builtFor.TryGetValue(__instance, out marker)) {
                MP3Client.Debug.Trace("Patch_BuildSeparateSongUI.Postfix", "instance=" + __instance.GetHashCode() + " already built, skipping");
                return;
            }
            MP3Client.Debug.Trace("Patch_BuildSeparateSongUI.Postfix", "instance=" + __instance.GetHashCode() + " starting DeferredBuild");
            MP3Client.Instance.StartCoroutine(DeferredBuild(__instance));
        }
        static IEnumerator DeferredBuild(object mp3MenuInstance) {
            MP3Client.Debug.Trace("DeferredBuild", "instance=" + mp3MenuInstance.GetHashCode() + " started, looking up 'dropdown' field");
            object dropdown = null;
            try {
                dropdown = ReflectionHelpers.GetMember(mp3MenuInstance, "dropdown");
            } catch (Exception e) {
                MP3Client.Log.LogError("mp3client: 'dropdown' field lookup failed: " + e);
                yield break;
            }
            if (dropdown == null) {
                MP3Client.Debug.Trace("DeferredBuild", "instance=" + mp3MenuInstance.GetHashCode() + " 'dropdown' field is null, giving up");
                yield break;
            }
            MP3Client.Debug.Trace("DeferredBuild", "instance=" + mp3MenuInstance.GetHashCode() + " dropdown found, waiting up to 5s for options to populate");
            float deadline = Time.unscaledTime + 5f;
            System.Collections.IList options = null;
            while (Time.unscaledTime < deadline) {
                options = ReflectionHelpers.GetMember(dropdown, "options") as System.Collections.IList;
                if (options != null && options.Count > 0) break;
                yield return null;
            }
            if (options == null || options.Count == 0) {
                MP3Client.Debug.Trace("DeferredBuild", "instance=" + mp3MenuInstance.GetHashCode() + " gave up after 5s, options never populated (options=" + (options == null ? "null" : "count 0") + ")");
                yield break;
            }
            MP3Client.Debug.Trace("DeferredBuild", "instance=" + mp3MenuInstance.GetHashCode() + " options populated (count=" + options.Count + "), calling LocalLibraryUI.Build");
            try {
                LocalLibraryUI.Build(mp3MenuInstance, (Component)dropdown);
                builtFor.Add(mp3MenuInstance, new object());
                MP3Client.Debug.Trace("DeferredBuild", "instance=" + mp3MenuInstance.GetHashCode() + " LocalLibraryUI.Build completed without throwing");
            } catch (Exception e) {
                MP3Client.Log.LogError("mp3client: Patch_BuildSeparateSongUI failed: " + e);
            }
        }
    }
    [HarmonyPatch]
    internal static class Patch_LogExitClick {
        static MethodBase TargetMethod() {
            var t = UITypes.MP3Menu;
            return AccessTools.Method(t, "Exit");
        }
        static void Prefix(object __instance) {
            MP3Client.Debug.Trace("MP3Menu.Exit", "instance=" + __instance.GetHashCode() + " is_server=" + KrokoshaScavMultiplayer.is_server + " is_client=" + KrokoshaScavMultiplayer.is_client);
        }
    }
    [HarmonyPatch]
    internal static class Patch_LogMenuStart {
        static MethodBase TargetMethod() {
            var t = UITypes.MP3Menu;
            return AccessTools.Method(t, "Start");
        }
        static void Prefix(object __instance) {
            MP3Client.Debug.Trace("MP3Menu.Start", "instance=" + __instance.GetHashCode() + " is_server=" + KrokoshaScavMultiplayer.is_server + " is_client=" + KrokoshaScavMultiplayer.is_client + " network_system_is_running=" + KrokoshaScavMultiplayer.network_system_is_running);
        }
        static void Postfix(object __instance) {
            var dropdown = ReflectionHelpers.GetMember(__instance, "dropdown");
            var options = dropdown != null ? ReflectionHelpers.GetMember(dropdown, "options") as System.Collections.IList : null;
            MP3Client.Debug.Trace("MP3Menu.Start", "instance=" + __instance.GetHashCode() + " finished, dropdown=" + (dropdown != null ? "found" : "NULL") + " options.count=" + (options != null ? options.Count.ToString() : "n/a"));
        }
    }
    internal static class CustomMusicFolder {
        public static string GetPath() {
            return Path.Combine(Application.dataPath, "custommusic");
        }
        public static void EnsureExists() {
            var dir = GetPath();
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }
        public static List<string> Scan() {
            EnsureExists();
            var result = new List<string>();
            try {
                foreach (var f in Directory.GetFiles(GetPath())) {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext == ".mp3" || ext == ".wav" || ext == ".ogg") result.Add(f);
                }
                result.Sort(StringComparer.OrdinalIgnoreCase);
            } catch (Exception e) {
                MP3Client.Log.LogWarning("mp3client: failed to scan " + GetPath() + ": " + e);
            }
            return result;
        }
        public static string CopyIn(string sourcePath) {
            EnsureExists();
            var name = Path.GetFileName(sourcePath);
            var dest = Path.Combine(GetPath(), name);
            if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase)) return dest;
            int i = 1;
            while (File.Exists(dest)) {
                dest = Path.Combine(GetPath(), Path.GetFileNameWithoutExtension(name) + "_" + i + Path.GetExtension(name));
                i++;
            }
            File.Copy(sourcePath, dest);
            return dest;
        }
    }
    internal static class LocalLibraryUI {
        internal static bool LocalIsSelected;
        private const string LibraryDropdownName = "MP3Client_LibraryDropdown";
        private const string BrowseButtonName = "MP3Client_BrowseButton";
        private static readonly ConditionalWeakTable<object, object> libraryDropdownFor = new ConditionalWeakTable<object, object>();
        private static readonly ConditionalWeakTable<object, string> selectedSongFor = new ConditionalWeakTable<object, string>();
        private static readonly ConditionalWeakTable<object, string> lastSourceFor = new ConditionalWeakTable<object, string>();
        private class ActiveTransfer { public ushort SyncId; public Coroutine Routine; }
        private static readonly ConditionalWeakTable<object, ActiveTransfer> activeTransferFor = new ConditionalWeakTable<object, ActiveTransfer>();
        private static readonly ConditionalWeakTable<object, object> pauseButtonFor = new ConditionalWeakTable<object, object>();
        private static object lastActiveInstanceKey;
        internal static readonly List<object> HostGatedControls = new List<object>();
        private static List<string> cachedSongs = new List<string>();
        internal static object CurrentLibDropdown;
        internal static void RegisterPauseButton(object mp3MenuInstance, object pauseButtonComponent) {
            pauseButtonFor.Remove(mp3MenuInstance);
            pauseButtonFor.Add(mp3MenuInstance, pauseButtonComponent);
            ActiveTransfer existing;
            SetPauseInteractable(mp3MenuInstance, !activeTransferFor.TryGetValue(mp3MenuInstance, out existing));
        }
        internal static void SetPauseInteractable(object mp3MenuInstance, bool interactable) {
            object pauseButtonComponent;
            if (!pauseButtonFor.TryGetValue(mp3MenuInstance, out pauseButtonComponent)) return;
            try { ReflectionHelpers.SetMember(pauseButtonComponent, "interactable", interactable); } catch { }
        }
        internal static void SetHostGateEnabled(bool enabled) {
            foreach (var c in HostGatedControls) {
                try { ReflectionHelpers.SetMember(c, "interactable", enabled); } catch { }
            }
        }
        internal static void SetDropdownHostMissingText(object libDropdown, bool missing) {
            if (libDropdown == null) return;
            if (missing) {
                SetDropdownSingleMessage(libDropdown, "Host doesn't have MP3Client plugin");
            } else {
                RefreshLibraryOptions(libDropdown);
                try { ReflectionHelpers.SetMember(libDropdown, "interactable", true); } catch { }
            }
        }
        internal static void SetDropdownSingleMessage(object libDropdown, string message) {
            if (libDropdown == null) return;
            ReflectionHelpers.CallMethod(libDropdown, "ClearOptions", Type.EmptyTypes, null);
            ReflectionHelpers.CallMethod(libDropdown, "AddOptions", new[] { typeof(List<string>) }, new object[] { new List<string> { message } });
            ReflectionHelpers.CallMethod(libDropdown, "SetValueWithoutNotify", new[] { typeof(int) }, new object[] { 0 });
            try { ReflectionHelpers.SetMember(libDropdown, "interactable", false); } catch { }
        }
        internal static void SetDropdownLocalIsHostText(object libDropdown) {
            SetDropdownSingleMessage(libDropdown, "This dropdown is for Client");
        }
        internal static void SetDropdownNoServerText(object libDropdown) {
            SetDropdownSingleMessage(libDropdown, "This dropdown only available in server");
        }
        public static void BeginSend(object mp3MenuInstance) {
            CancelActive(mp3MenuInstance);
            lastActiveInstanceKey = mp3MenuInstance;
        }
        public static void RegisterTransfer(object mp3MenuInstance, ushort syncId, Coroutine routine) {
            activeTransferFor.Remove(mp3MenuInstance);
            activeTransferFor.Add(mp3MenuInstance, new ActiveTransfer { SyncId = syncId, Routine = routine });
            SetPauseInteractable(mp3MenuInstance, false);
        }
        public static void ClearIfCurrent(object mp3MenuInstance, ushort syncId) {
            ActiveTransfer existing;
            if (activeTransferFor.TryGetValue(mp3MenuInstance, out existing) && existing.SyncId == syncId) {
                activeTransferFor.Remove(mp3MenuInstance);
                SetPauseInteractable(mp3MenuInstance, true);
            }
        }
        public static void CancelActive(object mp3MenuInstance) {
            ActiveTransfer existing;
            if (!activeTransferFor.TryGetValue(mp3MenuInstance, out existing)) return;
            if (existing.Routine != null) MP3Client.Instance.StopCoroutine(existing.Routine);
            Patch_InterceptBrowsePlay.SendCancel(existing.SyncId);
            Patch_InterceptBrowsePlay.StopSilence();
            activeTransferFor.Remove(mp3MenuInstance);
            SetPauseInteractable(mp3MenuInstance, true);
        }
        public static bool CancelActiveGlobal() {
            object instance = lastActiveInstanceKey;
            if (instance == null) return false;
            ActiveTransfer existing;
            if (!activeTransferFor.TryGetValue(instance, out existing)) return false;
            CancelActive(instance);
            return true;
        }
        private static void OnStopButtonClicked() {
            bool cancelled = CancelActiveGlobal();
            ushort playerSyncId;
            if (Patch_InterceptBrowsePlay.TryGetCurrentPlayerSyncId(out playerSyncId)) Patch_InterceptBrowsePlay.SendCancel(playerSyncId);
            KrokoshaScavMultiplayer.DoMultiplayerStatusMessageLog(cancelled ? "mp3client: MP3stop - dropped current transfer and stopped playback." : "mp3client: MP3stop - stopped playback.");
        }
        private static void OnPauseButtonClicked() {
            ushort playerSyncId;
            if (Patch_InterceptBrowsePlay.TryGetCurrentPlayerSyncId(out playerSyncId)) {
                Patch_InterceptBrowsePlay.SendPause(playerSyncId);
                KrokoshaScavMultiplayer.DoMultiplayerStatusMessageLog("mp3client: MP3pause - sent pause/resume toggle.");
            }
        }
        public static void Build(object mp3MenuInstance, Component originalDropdown) {
            MP3Client.Debug.UIState("Build", "building MP3Client UI onto the MP3 menu, instance=" + mp3MenuInstance.GetHashCode());
            HostGatedControls.Clear();
            Transform parent = originalDropdown.transform.parent != null ? originalDropdown.transform.parent : originalDropdown.transform;
            var origRect = originalDropdown.GetComponent<RectTransform>();
            MP3Client.Debug.UIState("Build", "parent=" + parent.name + " origRect=" + (origRect != null ? origRect.rect.ToString() : "null"));
            float rowHeight = origRect != null ? origRect.rect.height + 4f : 34f;
            GameObject libraryGO = UnityEngine.Object.Instantiate(originalDropdown.gameObject, parent, false);
            libraryGO.name = LibraryDropdownName;
            object libDropdown = libraryGO.GetComponent(originalDropdown.GetType());
            ReflectionHelpers.CallMethod(libDropdown, "ClearOptions", Type.EmptyTypes, null);
            var libValueChangedEvt = ReflectionHelpers.GetMember(libDropdown, "onValueChanged");
            libValueChangedEvt.GetType().GetMethod("RemoveAllListeners").Invoke(libValueChangedEvt, null);
            libraryDropdownFor.Remove(mp3MenuInstance);
            libraryDropdownFor.Add(mp3MenuInstance, libDropdown);
            CurrentLibDropdown = libDropdown;
            int optionCount = RefreshLibraryOptions(libDropdown);
            MP3Client.Debug.UIState("Build", "library dropdown cloned and populated, " + optionCount + " options (including placeholder)");
            HookValueChanged(libDropdown, originalDropdown, mp3MenuInstance);
            HookBaseDropdownValueChanged(originalDropdown, libDropdown, mp3MenuInstance);
            var panel = FindPanel(originalDropdown.transform);
            MP3Client.Debug.UIState("Build", "FindPanel result: " + (panel != null ? panel.name : "null (will use parent as search root)"));
            var searchRoot = panel != null ? panel : parent;
            var buttonType = UITypes.Button;
            var allButtons = searchRoot.GetComponentsInChildren(buttonType, true);
            MP3Client.Debug.UIState("Build", "searched '" + searchRoot.name + "' for buttons, found " + allButtons.Length + ": [" + string.Join(", ", allButtons.Select(b => b.gameObject.name)) + "]");
            Component playButton = null;
            Component exitButton = null;
            foreach (Component c in allButtons) {
                var n = c.gameObject.name.ToLowerInvariant();
                if (playButton == null && n.Contains("play")) playButton = c;
                if (exitButton == null && n.Contains("exit")) exitButton = c;
            }
            MP3Client.Debug.UIState("Build", "playButton=" + (playButton != null ? playButton.gameObject.name : "NOT FOUND") + " exitButton=" + (exitButton != null ? exitButton.gameObject.name : "NOT FOUND"));

            if (playButton != null) {
                MP3Client.Debug.UIState("Build", "using row-layout path (Browse/Stop/Pause + Play/Exit)");
                Component anchorButton = exitButton != null ? exitButton : playButton;
                GameObject browseGO = BuildBrowseFromTemplate(anchorButton, originalDropdown, mp3MenuInstance);

                GameObject stopGO = BuildStopFromTemplate(browseGO);
                GameObject pauseGO = BuildPauseFromTemplate(browseGO);

                AssembleContainerLayout(originalDropdown, libraryGO, playButton, exitButton, browseGO, stopGO, pauseGO, rowHeight, panel);
                HookPlayButton(playButton, mp3MenuInstance);
                HostGatedControls.Add(browseGO.GetComponent(buttonType));
                HostGatedControls.Add(stopGO.GetComponent(buttonType));
                HostGatedControls.Add(pauseGO.GetComponent(buttonType));
                RegisterPauseButton(mp3MenuInstance, pauseGO.GetComponent(buttonType));
            } else {
                MP3Client.Debug.UIState("Build", "using fallback path (no base Play button found in search root)");
                var libRect = libraryGO.GetComponent<RectTransform>();
                if (libRect != null && origRect != null) libRect.anchoredPosition = origRect.anchoredPosition + new Vector2(0f, -(rowHeight + MP3Client.UIVerticalOffset.Value));
                BuildFallbackRow(mp3MenuInstance, parent, origRect, rowHeight, MP3Client.UIVerticalOffset.Value);
                GrowPanel(panel, rowHeight * 1.5f + MP3Client.UIVerticalOffset.Value);
            }
            try {
                HostPluginCheck.EnsureChecked(libDropdown);
                MP3Client.Debug.UIState("Build", "HostPluginCheck.EnsureChecked completed");
            } catch (Exception e) {
                MP3Client.Log.LogError("mp3client: EnsureChecked failed: " + e);
            }
            MP3Client.Debug.UIState("Build", "finished, HostGatedControls count=" + HostGatedControls.Count);
        }
        public static Sprite LoadEmbeddedIcon(string resourceFileName) {
            try {
                var assembly = Assembly.GetExecutingAssembly();
                string resourceName = assembly.GetManifestResourceNames()
                    .FirstOrDefault(r => r.EndsWith(resourceFileName, StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrEmpty(resourceName)) {
                    MP3Client.Log.LogError("mp3client: Icon missing! Did you mark " + resourceFileName + " as an Embedded Resource?");
                    return null;
                }
                using (var stream = assembly.GetManifestResourceStream(resourceName)) {
                    if (stream == null) return null;
                    byte[] buffer = new byte[stream.Length];
                    stream.Read(buffer, 0, buffer.Length);
                    Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    MethodInfo legacyLoad = typeof(Texture2D).GetMethod("LoadImage", new[] { typeof(byte[]) });
                    if (legacyLoad != null) {
                        legacyLoad.Invoke(texture, new object[] { buffer });
                    } else {
                        Type imgConvType = AccessTools.TypeByName("UnityEngine.ImageConversion");
                        var modernLoad = imgConvType?.GetMethod("LoadImage", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Texture2D), typeof(byte[]) }, null);
                        modernLoad?.Invoke(null, new object[] { texture, buffer });
                    }
                    return Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), new Vector2(0.5f, 0.5f));
                }
            } catch (Exception e) {
                MP3Client.Log.LogError("mp3client: Icon load exception: " + e);
                return null;
            }
        }
        private static void NeutralizeCompetingLayout(RectTransform rt) {
            if (rt == null) return;
            var fitterType = UITypes.ContentSizeFitter;
            if (fitterType != null) {
                var fitter = rt.GetComponent(fitterType) as Behaviour;
                if (fitter != null && fitter.enabled) fitter.enabled = false;
            }
        }
        private static object ReplaceButtonComponent(GameObject go) {
            var buttonType = UITypes.Button;
            var oldButton = go.GetComponent(buttonType);
            var targetGraphic = ReflectionHelpers.GetMember(oldButton, "targetGraphic");
            var transition = ReflectionHelpers.GetMember(oldButton, "transition");
            UnityEngine.Object.DestroyImmediate(oldButton);
            var newButton = go.AddComponent(buttonType);
            if (targetGraphic != null) ReflectionHelpers.SetMember(newButton, "targetGraphic", targetGraphic);
            if (transition != null) ReflectionHelpers.SetMember(newButton, "transition", transition);
            return newButton;
        }
        private static void HookPlayButton(Component playButton, object mp3MenuInstance) {
            var go = playButton.gameObject;
            var newButton = ReplaceButtonComponent(go);
            var onClick = ReflectionHelpers.GetMember(newButton, "onClick");
            var addListener = onClick.GetType().GetMethod("AddListener");
            UnityAction handler = () => {
                MP3Client.Debug.Trace("HookPlayButton", "clicked, instance=" + mp3MenuInstance.GetHashCode() + " IsLocalLastSelected=" + LocalLibraryUI.IsLocalLastSelected(mp3MenuInstance));
                if (LocalLibraryUI.IsLocalLastSelected(mp3MenuInstance)) {
                    string path;
                    if (LocalLibraryUI.TryGetStaged(mp3MenuInstance, out path) && !string.IsNullOrEmpty(path)) {
                        MP3Client.Debug.Trace("HookPlayButton", "routing to local staged song: " + path);
                        Patch_InterceptBrowsePlay.BeginSilenceUntilClipChanges();
                        Patch_InterceptBrowsePlay.SendStagedSong(mp3MenuInstance, path);
                        return;
                    }
                    MP3Client.Debug.Trace("HookPlayButton", "IsLocalLastSelected true but TryGetStaged failed/empty - falling through to base selection");
                }
                MP3Client.Debug.Trace("HookPlayButton", "routing to base dropdown selection");
                LocalLibraryUI.PlayBaseSelectionWithoutExit(mp3MenuInstance);
            };
            addListener.Invoke(onClick, new object[] { handler });
        }
        internal static void PlayBaseSelectionWithoutExit(object mp3MenuInstance) {
            var menuType = UITypes.MP3Menu;
            var loadingField = AccessTools.Field(menuType, "loadingMusic");
            if (loadingField != null && (bool)loadingField.GetValue(null)) {
                MP3Client.Debug.Trace("PlayBaseSelectionWithoutExit", "skipped - still loading music");
                return;
            }
            var dropdown = ReflectionHelpers.GetMember(mp3MenuInstance, "dropdown");
            int index = (int)ReflectionHelpers.GetMember(dropdown, "value");
            bool multiplayerActive = KrokoshaScavMultiplayer.network_system_is_running && KrokoshaScavMultiplayer.rules.EnableMP3Sync;
            if (multiplayerActive) {
                var itemField = AccessTools.Field(typeof(MP3Menu_UpdateList_MultiplayerPatch), "last_locally_used_mp3_player");
                var itemObj = itemField != null ? itemField.GetValue(null) : null;
                if (itemObj != null) {
                    if (KrokoshaScavMultiplayer.is_client) {
                        SyncInfo syncInfo;
                        if (NetObjectRegistry.TryGetSyncInfoOrRegister((Component)itemObj, out syncInfo)) {
                            var writer = Net.CreateWriter(10137);
                            writer.Put(index);
                            writer.Put(syncInfo.syncId);
                            var dm = DeliveryMethod.ReliableUnordered;
                            Net.Client_Send(in dm, in writer);
                            MP3Client.Debug.Trace("PlayBaseSelectionWithoutExit", "multiplayer client - sent native play-request msg 10137 index=" + index + " syncId=" + syncInfo.syncId);
                        } else {
                            MP3Client.Debug.Trace("PlayBaseSelectionWithoutExit", "multiplayer client - mp3 player not registered, cannot send play request");
                        }
                    } else {
                        var clipsFieldMp = AccessTools.Field(menuType, "clips");
                        var clipsMp = clipsFieldMp != null ? clipsFieldMp.GetValue(null) as System.Collections.IList : null;
                        if (clipsMp != null && index >= 0 && index < clipsMp.Count) {
                            var serverPlayMethod = AccessTools.Method(typeof(MP3Menu_Play_MultiplayerPatch), "Server_PlayThisSongOnThisMp3Player");
                            if (serverPlayMethod != null) {
                                serverPlayMethod.Invoke(null, new object[] { clipsMp[index], itemObj });
                                MP3Client.Debug.Trace("PlayBaseSelectionWithoutExit", "multiplayer host - played directly via Server_PlayThisSongOnThisMp3Player index=" + index);
                            } else {
                                MP3Client.Debug.Trace("PlayBaseSelectionWithoutExit", "multiplayer host - Server_PlayThisSongOnThisMp3Player method not found");
                            }
                        } else {
                            MP3Client.Debug.Trace("PlayBaseSelectionWithoutExit", "multiplayer host - clips null or index " + index + " out of range");
                        }
                    }
                    return;
                }
                MP3Client.Debug.Trace("PlayBaseSelectionWithoutExit", "multiplayer active but no known mp3 player instance - falling back to local MusicManager playback");
            }
            var clipsField = AccessTools.Field(menuType, "clips");
            var clips = clipsField != null ? clipsField.GetValue(null) as System.Collections.IList : null;
            if (clips == null || index < 0 || index >= clips.Count) {
                MP3Client.Debug.Trace("PlayBaseSelectionWithoutExit", "skipped - clips null or index " + index + " out of range");
                return;
            }
            var clip = clips[index] as AudioClip;
            MP3Client.Debug.Trace("PlayBaseSelectionWithoutExit", "solo/offline - index=" + index + " clip=" + (clip != null ? clip.name : "NULL"));
            var musicManagerType = AccessTools.TypeByName("MusicManager");
            var mainField = musicManagerType != null ? AccessTools.Field(musicManagerType, "main") : null;
            var mainInstance = mainField != null ? mainField.GetValue(null) : null;
            var playSongMethod = musicManagerType != null ? AccessTools.Method(musicManagerType, "PlaySong") : null;
            if (mainInstance == null || playSongMethod == null) {
                MP3Client.Debug.Trace("PlayBaseSelectionWithoutExit", "skipped - MusicManager.main/PlaySong not found (main=" + (mainInstance != null) + " method=" + (playSongMethod != null) + ")");
                return;
            }
            playSongMethod.Invoke(mainInstance, new object[] { clip });
            MP3Client.Debug.Trace("PlayBaseSelectionWithoutExit", "PlaySong invoked successfully");
        }
        private const string LayoutRootName = "MP3Client_LayoutRoot";
        private static GameObject BuildRow(Transform parent, string name, Type hLayoutType, Type layoutElementType, float rowHeight) {
            GameObject row = new GameObject(name, typeof(RectTransform));
            row.transform.SetParent(parent, false);
            var hLayout = row.AddComponent(hLayoutType);
            ReflectionHelpers.SetMember(hLayout, "spacing", 8f);
            ReflectionHelpers.SetMember(hLayout, "childForceExpandWidth", true);
            ReflectionHelpers.SetMember(hLayout, "childForceExpandHeight", true);
            ReflectionHelpers.SetMember(hLayout, "childControlWidth", true);
            ReflectionHelpers.SetMember(hLayout, "childControlHeight", true);
            ReflectionHelpers.SetMember(hLayout, "childAlignment", TextAnchor.MiddleCenter);
            SetRowLayoutElement(row, layoutElementType, rowHeight);
            return row;
        }
        private static void SetRowLayoutElement(GameObject go, Type layoutElementType, float rowHeight) {
            if (layoutElementType == null) return;
            var existing = go.GetComponent(layoutElementType);
            if (existing != null) UnityEngine.Object.DestroyImmediate(existing);
            var le = go.AddComponent(layoutElementType);
            ReflectionHelpers.SetMember(le, "preferredHeight", rowHeight);
            ReflectionHelpers.SetMember(le, "minHeight", rowHeight);
        }
        private static void StripLayoutElement(GameObject go, Type layoutElementType) {
            if (layoutElementType == null || go == null) return;
            var existing = go.GetComponent(layoutElementType);
            if (existing != null) UnityEngine.Object.DestroyImmediate(existing);
        }
        private static void AssembleContainerLayout(Component originalDropdown, GameObject libraryGO, Component playButton, Component exitButton, GameObject browseGO, GameObject stopGO, GameObject pauseGO, float rowHeight, Transform panel) {
            var origRect = originalDropdown.GetComponent<RectTransform>();
            if (origRect == null || playButton == null) return;
            Transform oldParent = originalDropdown.transform.parent;
            const float spacing = 4f;
            GameObject root = new GameObject(LayoutRootName, typeof(RectTransform));
            root.transform.SetParent(oldParent, false);
            var rootRect = root.GetComponent<RectTransform>();
            float newRootHeight = (rowHeight * 4f) + (spacing * 3f);

            if (MP3Client.UIOnTop.Value) {
                rootRect.anchorMin = new Vector2(0.5f, 1f);
                rootRect.anchorMax = new Vector2(0.5f, 1f);
                rootRect.pivot = new Vector2(0.5f, 1f);
            } else {
                rootRect.anchorMin = new Vector2(0.5f, 0.5f);
                rootRect.anchorMax = new Vector2(0.5f, 0.5f);
                rootRect.pivot = new Vector2(0.5f, 0.5f);
            }

            rootRect.anchoredPosition = new Vector2(0f, -(rowHeight * 0.25f));
            rootRect.sizeDelta = new Vector2(origRect.rect.width, newRootHeight);

            root.transform.SetSiblingIndex(originalDropdown.transform.GetSiblingIndex());
            var vLayoutType = UITypes.VerticalLayoutGroup;
            var hLayoutType = UITypes.HorizontalLayoutGroup;
            var layoutElementType = UITypes.LayoutElement;
            if (vLayoutType == null || hLayoutType == null) {
                MP3Client.Log.LogError("mp3client: VerticalLayoutGroup/HorizontalLayoutGroup not found - cannot build container layout");
                UnityEngine.Object.DestroyImmediate(root);
                return;
            }
            var vLayout = root.AddComponent(vLayoutType);
            ReflectionHelpers.SetMember(vLayout, "spacing", spacing);
            ReflectionHelpers.SetMember(vLayout, "childForceExpandWidth", true);
            ReflectionHelpers.SetMember(vLayout, "childForceExpandHeight", false);
            ReflectionHelpers.SetMember(vLayout, "childControlWidth", true);
            ReflectionHelpers.SetMember(vLayout, "childControlHeight", true);
            ReflectionHelpers.SetMember(vLayout, "childAlignment", TextAnchor.UpperCenter);

            originalDropdown.transform.SetParent(root.transform, false);
            SetRowLayoutElement(originalDropdown.gameObject, layoutElementType, rowHeight);
            libraryGO.transform.SetParent(root.transform, false);
            SetRowLayoutElement(libraryGO, layoutElementType, rowHeight);
            GameObject row1 = BuildRow(root.transform, "MP3Client_Row_BrowseStopPause", hLayoutType, layoutElementType, rowHeight);
            browseGO.transform.SetParent(row1.transform, false);
            stopGO.transform.SetParent(row1.transform, false);
            pauseGO.transform.SetParent(row1.transform, false);
            GameObject row2 = BuildRow(root.transform, "MP3Client_Row_PlayExit", hLayoutType, layoutElementType, rowHeight);
            if (exitButton != null) exitButton.transform.SetParent(row2.transform, false);
            playButton.transform.SetParent(row2.transform, false);

            StripLayoutElement(browseGO, layoutElementType);
            StripLayoutElement(stopGO, layoutElementType);
            StripLayoutElement(pauseGO, layoutElementType);
            StripLayoutElement(playButton.gameObject, layoutElementType);
            if (exitButton != null) StripLayoutElement(exitButton.gameObject, layoutElementType);
            NeutralizeCompetingLayout(browseGO.GetComponent<RectTransform>());
            NeutralizeCompetingLayout(stopGO.GetComponent<RectTransform>());
            NeutralizeCompetingLayout(pauseGO.GetComponent<RectTransform>());
            NeutralizeCompetingLayout(playButton.GetComponent<RectTransform>());
            if (exitButton != null) NeutralizeCompetingLayout(exitButton.GetComponent<RectTransform>());

            float addedHeight = (rowHeight * 2.5f) + (spacing * 2f);
            GrowPanel(panel, addedHeight);

            var rebuilderType = UITypes.LayoutRebuilder;
            if (rebuilderType != null) {
                var forceRebuild = rebuilderType.GetMethod("ForceRebuildLayoutImmediate", BindingFlags.Public | BindingFlags.Static);
                if (forceRebuild != null) forceRebuild.Invoke(null, new object[] { rootRect });
            }
        }
        private static GameObject BuildLabeledClone(GameObject template, string name, string label, Color color, Action onClick, string iconName = null) {
            GameObject go = UnityEngine.Object.Instantiate(template, template.transform.parent, false);
            go.name = name;
            var buttonComp = ReplaceButtonComponent(go);
            var onClickObj = ReflectionHelpers.GetMember(buttonComp, "onClick");
            var addListener = onClickObj.GetType().GetMethod("AddListener");
            UnityAction handler = () => onClick();
            addListener.Invoke(onClickObj, new object[] { handler });

            var tmpTextType = UITypes.TMPText;
            bool labelSet = false;
            if (tmpTextType != null) {
                foreach (Component txt in go.GetComponentsInChildren(tmpTextType, true)) {
                    ReflectionHelpers.SetMember(txt, "text", label);
                    txt.gameObject.SetActive(true);
                    labelSet = true;
                }
            }
            var uiTextType = UITypes.Text;
            if (uiTextType != null) {
                foreach (Component txt in go.GetComponentsInChildren(uiTextType, true)) {
                    ReflectionHelpers.SetMember(txt, "text", label);
                    txt.gameObject.SetActive(true);
                    labelSet = true;
                }
            }
            if (!labelSet && uiTextType != null) {
                GameObject labelGO = new GameObject("Label", typeof(RectTransform));
                labelGO.transform.SetParent(go.transform, false);
                var labelRect = labelGO.GetComponent<RectTransform>();
                labelRect.anchorMin = Vector2.zero; labelRect.anchorMax = Vector2.one;
                labelRect.offsetMin = Vector2.zero; labelRect.offsetMax = Vector2.zero;
                var textComp = labelGO.AddComponent(uiTextType);
                ReflectionHelpers.SetMember(textComp, "text", label);
                ReflectionHelpers.SetMember(textComp, "color", Color.white);
                ReflectionHelpers.SetMember(textComp, "alignment", 4);
            }
            var imageType = UITypes.Image;
            var mainImage = go.GetComponent(imageType);
            if (mainImage != null) ReflectionHelpers.SetMember(mainImage, "color", color);

            if (!string.IsNullOrEmpty(iconName)) {
                Sprite customSprite = LoadEmbeddedIcon(iconName);
                if (customSprite != null) {
                    foreach (Component img in go.GetComponentsInChildren(imageType, true)) {
                        if (img.gameObject == go) continue;
                        if (img.gameObject.activeSelf) {
                            ReflectionHelpers.SetMember(img, "sprite", customSprite);
                            break;
                        }
                    }
                }
            }
            return go;
        }
        private static GameObject BuildStopFromTemplate(GameObject template) {
            Color btnColor = MP3Client.ColoredButtons.Value ? new Color(1f, 0.25f, 0.25f, 0.85f) : Color.white;
            return BuildLabeledClone(template, "MP3Client_StopButton", "Stop", btnColor, OnStopButtonClicked, "StopIcon.png");
        }
        private static GameObject BuildPauseFromTemplate(GameObject template) {
            Color btnColor = MP3Client.ColoredButtons.Value ? new Color(1f, 0.75f, 0.1f, 0.85f) : Color.white;
            return BuildLabeledClone(template, "MP3Client_PauseButton", "Pause", btnColor, OnPauseButtonClicked, "PauseIcon.png");
        }
        private static GameObject BuildBrowseFromTemplate(Component anchorButton, Component originalDropdown, object mp3MenuInstance) {
            var anchorParent = anchorButton.transform.parent;
            GameObject browseGO = UnityEngine.Object.Instantiate(anchorButton.gameObject, anchorParent, false);
            browseGO.name = "MP3Client_BrowseButton";
            var browseButtonComp = ReplaceButtonComponent(browseGO);
            var onClickObj = ReflectionHelpers.GetMember(browseButtonComp, "onClick");
            var addListener = onClickObj.GetType().GetMethod("AddListener");
            UnityAction handler = () => Patch_InterceptBrowsePlay.BrowseAndPackage(mp3MenuInstance);
            addListener.Invoke(onClickObj, new object[] { handler });

            var imageType = UITypes.Image;
            Sprite customSprite = LoadEmbeddedIcon("BrowseIcon.png");
            Component iconImage = null;
            foreach (Component img in browseGO.GetComponentsInChildren(imageType, true)) {
                if (img.gameObject == browseGO) continue;
                if (iconImage == null) {
                    iconImage = img;
                    img.gameObject.SetActive(true);
                } else {
                    img.gameObject.SetActive(false);
                }
            }
            if (customSprite != null && iconImage != null) {
                ReflectionHelpers.SetMember(iconImage, "sprite", customSprite);
                ReflectionHelpers.SetMember(iconImage, "color", Color.white);
                var tmpTextType = UITypes.TMPText;
                if (tmpTextType != null) {
                    foreach (Component txt in browseGO.GetComponentsInChildren(tmpTextType, true)) {
                        UnityEngine.Object.DestroyImmediate(txt.gameObject);
                    }
                }
            } else if (customSprite == null) {
                var uiTextType = UITypes.Text;
                GameObject labelGO = new GameObject("Label", typeof(RectTransform));
                labelGO.transform.SetParent(browseGO.transform, false);
                var labelRect = labelGO.GetComponent<RectTransform>();
                labelRect.anchorMin = Vector2.zero; labelRect.anchorMax = Vector2.one;
                labelRect.offsetMin = Vector2.zero; labelRect.offsetMax = Vector2.zero;
                var textComp = labelGO.AddComponent(uiTextType);
                ReflectionHelpers.SetMember(textComp, "text", "FILE");
                ReflectionHelpers.SetMember(textComp, "color", Color.white);
                ReflectionHelpers.SetMember(textComp, "alignment", 4);
            }
            return browseGO;
        }
        private static readonly ConditionalWeakTable<object, object> menuForDropdown = new ConditionalWeakTable<object, object>();
        private static void HookBaseDropdownValueChanged(Component originalDropdown, object libDropdown, object mp3MenuInstance) {
            menuForDropdown.Remove(originalDropdown);
            menuForDropdown.Add(originalDropdown, mp3MenuInstance);
            var evt = ReflectionHelpers.GetMember(originalDropdown, "onValueChanged");
            var addListener = evt.GetType().GetMethod("AddListener");
            UnityAction<int> handler = (index) => {
                CancelActive(mp3MenuInstance);
                lastSourceFor.Remove(mp3MenuInstance);
                lastSourceFor.Add(mp3MenuInstance, "base");
                LocalIsSelected = false;
            };
            addListener.Invoke(evt, new object[] { handler });
        }
        private static Transform FindPanel(Transform from) {
            var imageType = UITypes.Image;
            var ownHeight = from.GetComponent<RectTransform>() != null ? from.GetComponent<RectTransform>().rect.height : 0f;
            var t = from.parent;
            for (int i = 0; i < 6 && t != null; i++) {
                var rt = t as RectTransform;
                var img = t.GetComponent(imageType);
                if (rt != null && img != null && rt.rect.height > ownHeight * 1.5f) return t;
                t = t.parent;
            }
            return null;
        }
        private static void GrowPanel(Transform panel, float extraHeight) {
            if (panel == null) return;
            var rt = panel as RectTransform;
            if (rt == null) return;

            if (MP3Client.UIOnTop.Value) {
                rt.anchorMin = new Vector2(0.5f, 1f);
                rt.anchorMax = new Vector2(0.5f, 1f);
                rt.pivot = new Vector2(0.5f, 1f);
            } else {
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
            }

            rt.sizeDelta = new Vector2(rt.sizeDelta.x, rt.sizeDelta.y + extraHeight);
            rt.anchoredPosition = Vector2.zero;
        }
        private static void BuildFallbackRow(object mp3MenuInstance, Transform parent, RectTransform origRect, float rowHeight, float verticalOffset) {
            float fullWidth = origRect != null ? origRect.rect.width : 150f;
            float fourthWidth = fullWidth / 4f - 4f;
            Vector2 stepDown2 = new Vector2(0f, -(rowHeight * 1.5f + verticalOffset));
            float baseX = origRect != null ? origRect.anchoredPosition.x : 0f;
            float baseY = origRect != null ? origRect.anchoredPosition.y + stepDown2.y : 0f;
            MakeButton(parent, BrowseButtonName, origRect, new Vector2(baseX - fourthWidth * 1.5f - 4f, baseY), new Vector2(fourthWidth, rowHeight - 4f), new Color(1f, 0f, 1f, 0.85f), Protocol.BROWSE_LABEL, () => Patch_InterceptBrowsePlay.BrowseAndPackage(mp3MenuInstance));
            MakeButton(parent, "MP3Client_SendButton", origRect, new Vector2(baseX - fourthWidth * 0.5f, baseY), new Vector2(fourthWidth, rowHeight - 4f), new Color(0f, 1f, 1f, 0.85f), "Send", () => {
                string path;
                if (!selectedSongFor.TryGetValue(mp3MenuInstance, out path) || string.IsNullOrEmpty(path)) {
                    KrokoshaScavMultiplayer.DoMultiplayerStatusMessageError("mp3client: pick a song first.");
                    return;
                }
                Patch_InterceptBrowsePlay.SendStagedSong(mp3MenuInstance, path);
            });
            MakeButton(parent, "MP3Client_PauseButton", origRect, new Vector2(baseX + fourthWidth * 0.5f, baseY), new Vector2(fourthWidth, rowHeight - 4f), new Color(1f, 0.75f, 0.1f, 0.85f), "Pause", OnPauseButtonClicked);
            MakeButton(parent, "MP3Client_StopButton", origRect, new Vector2(baseX + fourthWidth * 1.5f + 4f, baseY), new Vector2(fourthWidth, rowHeight - 4f), new Color(1f, 0.25f, 0.25f, 0.85f), "Stop", OnStopButtonClicked);
        }
        private static RectTransform MakeButton(Transform parent, string name, RectTransform origRect, Vector2 anchoredPosition, Vector2 size, Color color, string label, Action onClick) {
            GameObject buttonGO = new GameObject(name, typeof(RectTransform));
            buttonGO.transform.SetParent(parent, false);
            var btnRect = buttonGO.GetComponent<RectTransform>();
            btnRect.sizeDelta = size;
            if (origRect != null) {
                btnRect.anchorMin = origRect.anchorMin;
                btnRect.anchorMax = origRect.anchorMax;
                btnRect.pivot = origRect.pivot;
                btnRect.anchoredPosition = anchoredPosition;
            }
            var imageType = UITypes.Image;
            var buttonType = UITypes.Button;
            var image = buttonGO.AddComponent(imageType);
            ReflectionHelpers.SetMember(image, "color", color);
            var button = buttonGO.AddComponent(buttonType);
            var onClickObj = ReflectionHelpers.GetMember(button, "onClick");
            var addListener = onClickObj.GetType().GetMethod("AddListener");
            UnityAction handler = () => onClick();
            addListener.Invoke(onClickObj, new object[] { handler });
            if (!string.IsNullOrEmpty(label)) {
                GameObject textGO = new GameObject("Label", typeof(RectTransform));
                textGO.transform.SetParent(buttonGO.transform, false);
                var textRect = textGO.GetComponent<RectTransform>();
                textRect.anchorMin = Vector2.zero;
                textRect.anchorMax = Vector2.one;
                textRect.offsetMin = Vector2.zero;
                textRect.offsetMax = Vector2.zero;
                var tmpTextType = UITypes.TMPText;
                if (tmpTextType != null) {
                    var text = textGO.AddComponent(tmpTextType);
                    ReflectionHelpers.SetMember(text, "text", label);
                    ReflectionHelpers.SetMember(text, "color", Color.black);
                } else {
                    var uiTextType = UITypes.Text;
                    if (uiTextType != null) {
                        var text = textGO.AddComponent(uiTextType);
                        ReflectionHelpers.SetMember(text, "text", label);
                        ReflectionHelpers.SetMember(text, "color", Color.black);
                    }
                }
            }
            HostGatedControls.Add(button);
            return btnRect;
        }
        public static int RefreshLibraryOptions(object libDropdown) {
            return RefreshLibraryOptions(libDropdown, null);
        }
        public static int RefreshLibraryOptions(object libDropdown, string selectPath) {
            cachedSongs = CustomMusicFolder.Scan();
            var labels = new List<string> { "Select a song..." };
            labels.AddRange(cachedSongs.Select(Path.GetFileNameWithoutExtension));
            ReflectionHelpers.CallMethod(libDropdown, "ClearOptions", Type.EmptyTypes, null);
            ReflectionHelpers.CallMethod(libDropdown, "AddOptions", new[] { typeof(List<string>) }, new object[] { labels });
            int selectedIndex = 0;
            if (selectPath != null) {
                string wanted = Path.GetFullPath(selectPath);
                int songIndex = cachedSongs.FindIndex(s => string.Equals(Path.GetFullPath(s), wanted, StringComparison.OrdinalIgnoreCase));
                if (songIndex >= 0) selectedIndex = songIndex + 1;
            }
            ReflectionHelpers.CallMethod(libDropdown, "SetValueWithoutNotify", new[] { typeof(int) }, new object[] { selectedIndex });
            return labels.Count;
        }
        public static void TryRefresh(object mp3MenuInstance) {
            TryRefresh(mp3MenuInstance, null);
        }
        public static void TryRefresh(object mp3MenuInstance, string selectPath) {
            object libDropdown;
            if (libraryDropdownFor.TryGetValue(mp3MenuInstance, out libDropdown)) RefreshLibraryOptions(libDropdown, selectPath);
        }
        public static void Stage(object mp3MenuInstance, string path) {
            selectedSongFor.Remove(mp3MenuInstance);
            selectedSongFor.Add(mp3MenuInstance, path);
        }
        public static bool TryGetStaged(object mp3MenuInstance, out string path) {
            return selectedSongFor.TryGetValue(mp3MenuInstance, out path);
        }
        public static bool IsLocalLastSelected(object mp3MenuInstance) {
            string src;
            return lastSourceFor.TryGetValue(mp3MenuInstance, out src) && src == "local";
        }
        private static void HookValueChanged(object libDropdown, Component originalDropdown, object mp3MenuInstance) {
            var evt = ReflectionHelpers.GetMember(libDropdown, "onValueChanged");
            var addListener = evt.GetType().GetMethod("AddListener");
            UnityAction<int> handler = (index) => {
                int songIndex = index - 1;
                if (songIndex < 0) {
                    LocalIsSelected = false;
                    return;
                }
                var songs = cachedSongs;
                if (songIndex >= songs.Count) return;
                CancelActive(mp3MenuInstance);
                Stage(mp3MenuInstance, songs[songIndex]);
                lastSourceFor.Remove(mp3MenuInstance);
                lastSourceFor.Add(mp3MenuInstance, "local");
                LocalIsSelected = true;
                KrokoshaScavMultiplayer.DoMultiplayerStatusMessageLog("mp3client: staged " + Path.GetFileName(songs[songIndex]) + ", press Play.");
            };
            addListener.Invoke(evt, new object[] { handler });
        }
    }
    internal static class Patch_InterceptBrowsePlay {
        private const string FileFilter = "Audio files (*.mp3;*.wav;*.ogg)\0*.mp3;*.wav;*.ogg\0All files\0*.*\0\0";
        public static void BrowseAndPackage(object mp3MenuInstance) {
            NativeFileDialog.ShowAsync(FileFilter, path => {
                if (string.IsNullOrEmpty(path)) {
                    return;
                }
                string packaged;
                try {
                    packaged = CustomMusicFolder.CopyIn(path);
                } catch (Exception e) {
                    KrokoshaScavMultiplayer.DoMultiplayerStatusMessageError("mp3client: couldn't copy into custommusic: " + e.Message);
                    return;
                }
                LocalLibraryUI.TryRefresh(mp3MenuInstance, packaged);
                LocalLibraryUI.Stage(mp3MenuInstance, packaged);
                KrokoshaScavMultiplayer.DoMultiplayerStatusMessageLog("mp3client: packaged " + Path.GetFileName(packaged) + ", press Send.");
            });
        }
        public static void SendStagedSong(object mp3MenuInstance, string path) {
            SendLocalFile(mp3MenuInstance, path);
        }
        private static Coroutine silenceRoutine;
        public static void BeginSilenceUntilClipChanges() {
            if (silenceRoutine != null) { MP3Client.Instance.StopCoroutine(silenceRoutine); silenceRoutine = null; }
            var sources = GetLocalAudioSources();
            if (sources.Count == 0) return;
            object previousClip = ReflectionHelpers.GetMember(sources[0], "clip");
            silenceRoutine = MP3Client.Instance.StartCoroutine(SilenceUntilClipChanges(sources, previousClip));
        }
        public static void StopSilence() {
            if (silenceRoutine != null) { MP3Client.Instance.StopCoroutine(silenceRoutine); silenceRoutine = null; }
            foreach (var src in GetLocalAudioSources()) {
                try { ReflectionHelpers.SetMember(src, "mute", false); } catch { }
            }
        }
        private static List<Component> GetLocalAudioSources() {
            var result = new List<Component>();
            try {
                var itemField = AccessTools.Field(typeof(MP3Menu_UpdateList_MultiplayerPatch), "last_locally_used_mp3_player");
                var itemObj = itemField.GetValue(null);
                if (itemObj == null) return result;
                var component = (Component)itemObj;
                var audioSourceType = UITypes.AudioSource;
                if (audioSourceType == null) return result;
                foreach (Component src in component.GetComponentsInChildren(audioSourceType, true)) result.Add(src);
            } catch { }
            return result;
        }
        private static IEnumerator SilenceUntilClipChanges(List<Component> sources, object previousClip) {
            var stopMethod = sources[0].GetType().GetMethod("Stop", Type.EmptyTypes);
            float deadline = Time.unscaledTime + Protocol.OVERALL_DEADLINE_SECONDS;
            while (Time.unscaledTime < deadline) {
                bool changed = false;
                foreach (var src in sources) {
                    var currentClip = ReflectionHelpers.GetMember(src, "clip");
                    if (!Equals(currentClip, previousClip)) { changed = true; break; }
                }
                if (changed) break;
                foreach (var src in sources) {
                    try {
                        ReflectionHelpers.SetMember(src, "mute", true);
                        if (stopMethod != null) stopMethod.Invoke(src, null);
                    } catch { }
                }
                yield return null;
            }
            foreach (var src in sources) {
                try { ReflectionHelpers.SetMember(src, "mute", false); } catch { }
            }
            silenceRoutine = null;
        }
        public static void SendCancel(ushort syncId) {
            var writer = Net.CreateWriter(Protocol.MSG_CANCEL);
            writer.Put(syncId);
            var dm = DeliveryMethod.ReliableOrdered;
            Net.Client_Send(in dm, in writer);
        }
        public static void SendPause(ushort syncId) {
            var writer = Net.CreateWriter(Protocol.MSG_PAUSE);
            writer.Put(syncId);
            var dm = DeliveryMethod.ReliableOrdered;
            Net.Client_Send(in dm, in writer);
        }
        internal static bool TryGetCurrentPlayerSyncId(out ushort syncId) {
            syncId = 0;
            var itemField = AccessTools.Field(typeof(MP3Menu_UpdateList_MultiplayerPatch), "last_locally_used_mp3_player");
            var itemObj = itemField.GetValue(null);
            if (itemObj == null) return false;
            SyncInfo si;
            if (!NetObjectRegistry.TryGetSyncInfoOrRegister((Component)itemObj, out si)) return false;
            syncId = si.syncId;
            return true;
        }
        private static void SendLocalFile(object mp3MenuInstance, string path) {
            var itemField = AccessTools.Field(typeof(MP3Menu_UpdateList_MultiplayerPatch), "last_locally_used_mp3_player");
            var itemObj = itemField.GetValue(null);
            if (itemObj == null) {
                KrokoshaScavMultiplayer.DoMultiplayerStatusMessageError("mp3client: no mp3 player selected.");
                return;
            }
            var component = (Component)itemObj;
            SyncInfo si;
            if (!NetObjectRegistry.TryGetSyncInfoOrRegister(component, out si)) {
                KrokoshaScavMultiplayer.DoMultiplayerStatusMessageError("mp3client: mp3 player not registered on network yet, try again.");
                return;
            }
            ushort syncId = si.syncId;
            byte[] bytes;
            try {
                bytes = File.ReadAllBytes(path);
            } catch (Exception e) {
                KrokoshaScavMultiplayer.DoMultiplayerStatusMessageError("mp3client: couldn't read " + path + ": " + e.Message);
                return;
            }
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".mp3" && ext != ".wav" && ext != ".ogg") {
                KrokoshaScavMultiplayer.DoMultiplayerStatusMessageError("mp3client: unsupported file type " + ext);
                return;
            }
            LocalLibraryUI.BeginSend(mp3MenuInstance);
            SendFile(mp3MenuInstance, syncId, ext, bytes);
        }
        internal static readonly Dictionary<ushort, (string Ext, byte[] Bytes)> pendingFiles = new Dictionary<ushort, (string, byte[])>();
        static void SendFile(object mp3MenuInstance, ushort syncId, string ext, byte[] bytes) {
            pendingFiles[syncId] = (ext, bytes);
            var co = MP3Client.Instance.StartCoroutine(SendFileCoroutine(syncId, ext, bytes, 0, true, mp3MenuInstance));
            LocalLibraryUI.RegisterTransfer(mp3MenuInstance, syncId, co);
        }
        internal static IEnumerator SendFileCoroutine(ushort syncId, string ext, byte[] bytes, int startOffset, bool sendBegin, object mp3MenuInstance = null) {
            float startTime = Time.unscaledTime;
            string exitReason = "interrupted/unknown (finally reached without normal completion)";
            try {
                if (sendBegin) {
                    var beginWriter = Net.CreateWriter(Protocol.MSG_BEGIN);
                    beginWriter.Put(syncId);
                    beginWriter.Put(bytes.Length);
                    beginWriter.Put(ext);
                    var dmBegin = DeliveryMethod.ReliableOrdered;
                    Net.Client_Send(in dmBegin, in beginWriter);
                    MP3Client.Debug.SendStart(syncId, ext, bytes.Length);
                    yield return null;
                }
                int offset = startOffset;
                int chunkIndex = 0;
                int[] percentThresholds = { 0, 25, 50, 75 };
                int nextThresholdIndex = 0;
                KrokoshaScavMultiplayer.DoMultiplayerStatusMessageLog("mp3client: staged with 0% package");
                nextThresholdIndex = 1;
                while (offset < bytes.Length) {
                    int len = Math.Min(Protocol.CHUNK_SIZE, bytes.Length - offset);
                    byte[] chunk = new byte[len];
                    Array.Copy(bytes, offset, chunk, 0, len);
                    var chunkWriter = Net.CreateWriter(Protocol.MSG_CHUNK);
                    chunkWriter.Put(syncId);
                    chunkWriter.Put(offset);
                    chunkWriter.PutBytesWithLength(chunk);
                    var dmChunk = DeliveryMethod.ReliableOrdered;
                    Net.Client_Send(in dmChunk, in chunkWriter);
                    offset += len;
                    chunkIndex++;
                    int percent = bytes.Length > 0 ? (int)((offset / (float)bytes.Length) * 100f) : 100;
                    while (nextThresholdIndex < percentThresholds.Length && percent >= percentThresholds[nextThresholdIndex]) {
                        KrokoshaScavMultiplayer.DoMultiplayerStatusMessageLog("mp3client: staged with " + percentThresholds[nextThresholdIndex] + "% package");
                        MP3Client.Debug.SendProgress(syncId, offset, bytes.Length, Time.unscaledTime - startTime);
                        nextThresholdIndex++;
                    }
                    if (chunkIndex % Protocol.CHUNKS_PER_FRAME == 0) yield return null;
                }
                yield return null;
                var endWriter = Net.CreateWriter(Protocol.MSG_END);
                endWriter.Put(syncId);
                var dmEnd = DeliveryMethod.ReliableOrdered;
                Net.Client_Send(in dmEnd, in endWriter);
                MP3Client.Debug.SendDone(syncId, bytes.Length, Time.unscaledTime - startTime);
                if (mp3MenuInstance != null) LocalLibraryUI.ClearIfCurrent(mp3MenuInstance, syncId);
                exitReason = "completed normally";
            } finally {
                MP3Client.Debug.SendCoroutineExit(syncId, exitReason);
            }
        }
    }
    internal static class NativeFileDialog {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OPENFILENAME {
            public int lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public string lpstrFilter;
            public string lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public IntPtr lpstrFile;
            public int nMaxFile;
            public string lpstrFileTitle;
            public int nMaxFileTitle;
            public string lpstrInitialDir;
            public string lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            public string lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public string lpTemplateName;
            public IntPtr pvReserved;
            public int dwReserved;
            public int FlagsEx;
        }
        [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetOpenFileName([In, Out] ref OPENFILENAME ofn);
        private const int OFN_FILEMUSTEXIST = 0x00001000;
        private const int OFN_PATHMUSTEXIST = 0x00000800;
        public static void ShowAsync(string filter, Action<string> onResult) {
            var thread = new Thread(() => {
                string result = null;
                try {
                    result = ShowBlocking(filter);
                } catch (Exception e) {
                    MP3Client.Log.LogError("mp3client: file dialog failed: " + e);
                }
                MP3Client.RunOnMainThread(() => onResult(result));
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
        }
        private static string ShowBlocking(string filter) {
            var ofn = new OPENFILENAME();
            ofn.lStructSize = Marshal.SizeOf(ofn);
            ofn.lpstrFilter = filter;
            IntPtr fileBuffer = Marshal.AllocHGlobal(2048);
            try {
                Marshal.WriteInt16(fileBuffer, 0, 0);
                ofn.lpstrFile = fileBuffer;
                ofn.nMaxFile = 1024;
                ofn.lpstrTitle = "Pick a song to broadcast";
                ofn.Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST;
                if (GetOpenFileName(ref ofn)) return Marshal.PtrToStringUni(fileBuffer);
                return null;
            } finally {
                Marshal.FreeHGlobal(fileBuffer);
            }
        }
    }
}