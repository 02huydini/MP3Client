using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using KrokoshaCasualtiesMP;

namespace MP3Client {
    internal static class HostPluginCheck {
        internal enum Status { Unknown, Confirmed, Missing }
        internal static Status CurrentStatus = Status.Unknown;
        private static bool pingSent;
        internal static void ResetForNewSession() {
            CurrentStatus = Status.Unknown;
            pingSent = false;
        }
        private static bool TryApplyNonClientState(object libDropdown) {
            if (!KrokoshaScavMultiplayer.network_system_is_running) {
                Plugin.Debug.UIState("TryApplyNonClientState", "no active session - showing no-server message, gate controls disabled");
                LocalLibraryUI.SetDropdownNoServerText(libDropdown);
                LocalLibraryUI.SetHostGateEnabled(false);
                return true;
            }
            if (KrokoshaScavMultiplayer.is_server) {
                Plugin.Debug.UIState("TryApplyNonClientState", "local player is host - showing client-only message, gate controls disabled");
                LocalLibraryUI.SetDropdownLocalIsHostText(libDropdown);
                LocalLibraryUI.SetHostGateEnabled(false);
                return true;
            }
            return false;
        }
        public static void EnsureChecked(object libDropdown) {
            if (TryApplyNonClientState(libDropdown)) return;
            if (CurrentStatus == Status.Missing) {
                LocalLibraryUI.SetDropdownHostMissingText(libDropdown, true);
                LocalLibraryUI.SetHostGateEnabled(false);
                return;
            }
            if (CurrentStatus == Status.Confirmed) return;
            LocalLibraryUI.SetDropdownHostMissingText(libDropdown, false);
            if (!pingSent) {
                pingSent = true;
                Plugin.Debug.NetState("HostPluginCheck", "sending MSG_PLUGIN_PING to host, awaiting pong");
                var writer = Net.CreateWriter(Protocol.MSG_PLUGIN_PING);
                var dm = DeliveryMethod.ReliableOrdered;
                Net.Client_Send(in dm, in writer);
            }
            Plugin.Instance.StartCoroutine(WaitForPong(libDropdown));
        }
        private static IEnumerator WaitForPong(object libDropdown) {
            float deadline = Time.unscaledTime + Protocol.PLUGIN_CHECK_TIMEOUT_SECONDS;
            while (Time.unscaledTime < deadline) {
                if (CurrentStatus == Status.Confirmed) yield break;
                if (TryApplyNonClientState(libDropdown)) yield break;
                yield return null;
            }
            if (CurrentStatus == Status.Unknown) {
                if (TryApplyNonClientState(libDropdown)) yield break;
                CurrentStatus = Status.Missing;
                KrokoshaScavMultiplayer.DoMultiplayerStatusMessageError("mp3client: Host doesn't have this plugin - custom music can't be sent.");
                LocalLibraryUI.SetDropdownHostMissingText(libDropdown, true);
                LocalLibraryUI.SetHostGateEnabled(false);
            }
        }
        public static void OnPong() {
            Plugin.Debug.NetState("HostPluginCheck", "received MSG_PLUGIN_PONG - host has the plugin, enabling gate controls");
            CurrentStatus = Status.Confirmed;
            LocalLibraryUI.SetDropdownHostMissingText(LocalLibraryUI.CurrentLibDropdown, false);
            LocalLibraryUI.SetHostGateEnabled(true);
        }
    }
    internal static class UITypes {
        private static readonly Dictionary<string, Type> cache = new Dictionary<string, Type>();
        public static Type Get(string name) {
            Type t;
            if (cache.TryGetValue(name, out t)) return t;
            t = AccessTools.TypeByName(name);
            cache[name] = t;
            return t;
        }
        public static Type Button { get { return Get("UnityEngine.UI.Button"); } }
        public static Type Image { get { return Get("UnityEngine.UI.Image"); } }
        public static Type Text { get { return Get("UnityEngine.UI.Text"); } }
        public static Type TMPText { get { return Get("TMPro.TMP_Text") ?? Get("TMPro.TextMeshProUGUI"); } }
        public static Type LayoutElement { get { return Get("UnityEngine.UI.LayoutElement"); } }
        public static Type VerticalLayoutGroup { get { return Get("UnityEngine.UI.VerticalLayoutGroup"); } }
        public static Type HorizontalLayoutGroup { get { return Get("UnityEngine.UI.HorizontalLayoutGroup"); } }
        public static Type ContentSizeFitter { get { return Get("UnityEngine.UI.ContentSizeFitter"); } }
        public static Type LayoutRebuilder { get { return Get("UnityEngine.UI.LayoutRebuilder"); } }
        public static Type AudioSource { get { return Get("UnityEngine.AudioSource"); } }
        public static Type MP3Menu { get { return Get("MP3Menu"); } }
    }
    internal static class ReflectionHelpers {
        private static readonly Dictionary<string, MemberInfo> memberCache = new Dictionary<string, MemberInfo>();
        private static readonly Dictionary<string, MethodInfo> methodCache = new Dictionary<string, MethodInfo>();
        private static MemberInfo ResolveMember(Type t, string name) {
            string key = t.FullName + "|" + name;
            MemberInfo mi;
            if (memberCache.TryGetValue(key, out mi)) return mi;
            mi = (MemberInfo)t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                ?? t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            memberCache[key] = mi;
            return mi;
        }
        public static object GetMember(object target, string name) {
            var mi = ResolveMember(target.GetType(), name);
            var prop = mi as PropertyInfo;
            if (prop != null) return prop.GetValue(target);
            var field = mi as FieldInfo;
            if (field != null) return field.GetValue(target);
            return null;
        }
        public static void CallMethod(object target, string name, Type[] paramTypes, object[] args) {
            var t = target.GetType();
            string key = t.FullName + "|" + name + "|" + paramTypes.Length;
            MethodInfo m;
            if (!methodCache.TryGetValue(key, out m)) {
                m = t.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, paramTypes, null);
                methodCache[key] = m;
            }
            if (m == null) throw new MissingMethodException(t.Name, name);
            m.Invoke(target, args);
        }
        public static void SetMember(object target, string name, object value) {
            var t = target.GetType();
            var mi = ResolveMember(t, name);
            var prop = mi as PropertyInfo;
            if (prop != null) { prop.SetValue(target, value); return; }
            var field = mi as FieldInfo;
            if (field != null) { field.SetValue(target, value); return; }
            throw new MissingMemberException(t.Name, name);
        }
    }
}