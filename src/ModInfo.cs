using System;
using System.Reflection;
using ICities;
using CitiesHarmony.API;
using UnityEngine;
using ColossalFramework.UI;
using ColossalFramework.Plugins;

namespace MorePropsBETA
{
    public sealed class ModInfo : IUserMod
    {
        public const string HARMONY_ID = "com.ngocduy.morepropsbeta";
        public const string MOD_NAME = "More Props BETA";
        public const string MOD_VERSION = "1.0.4-BETA";
        public const string MOD_DESCRIPTION = "Expands the prop limit to 262,144 (256K) with Active Window Hot-Swapping for 100% Move It compatibility.";

        public string Name => MOD_NAME;
        public string Description => MOD_DESCRIPTION;

        public void OnEnabled()
        {
            HarmonyHelper.DoOnHarmonyReady(() =>
            {
                Patcher.PatchAll();
            });
        }

        public void OnDisabled()
        {
            if (HarmonyHelper.IsHarmonyInstalled)
            {
                Patcher.UnpatchAll();
            }
        }
    }

    public sealed class LoadingExtension : LoadingExtensionBase
    {
        private static GameObject s_hotkeyGo;

        public override void OnCreated(ILoading loading)
        {
            base.OnCreated(loading);
        }

        public override void OnLevelLoaded(LoadMode mode)
        {
            base.OnLevelLoaded(mode);

            if (mode != LoadMode.LoadGame && mode != LoadMode.NewGame && mode != LoadMode.NewGameFromScenario && mode != LoadMode.LoadScenario)
            {
                return;
            }

            CheckModConflicts();
            ExtendedPropManager.EnsureInitialized();
            DynamicMoveItHook.TryHookMoveIt();
            ThirdPartyLimitIntegrations.TryPatchLimitMods(new HarmonyLib.Harmony(ModInfo.HARMONY_ID));
            ExtendedPropManager.Instance?.EnsureBufferHeadroom(Camera.main != null ? Camera.main.transform.position : Vector3.zero, ExtendedPropManager.SAFE_VANILLA_THRESHOLD);

            if (s_hotkeyGo == null)
            {
                s_hotkeyGo = new GameObject("MorePropsBETA_HotkeyListener");
                s_hotkeyGo.AddComponent<LimitDisplayHotkeyListener>();
                UnityEngine.Object.DontDestroyOnLoad(s_hotkeyGo);
            }
        }

        public override void OnLevelUnloading()
        {
            base.OnLevelUnloading();
            ExtendedPropManager.Instance?.Reset();
        }

        private static void CheckModConflicts()
        {
            try
            {
                PluginManager pluginManager = PluginManager.instance;
                foreach (PluginManager.PluginInfo plugin in pluginManager.GetPluginsInfo())
                {
                    if (!plugin.isEnabled) continue;

                    ulong pubId = plugin.publishedFileID.AsUInt64;
                    bool isEML = plugin.name.Contains("ExtendedManagersLibrary") || plugin.name.Contains("EML") || pubId == 2696146766;
                    bool isQuayhuanPA = pubId == 2696149201 || (plugin.name.Equals("PropAnarchy", StringComparison.OrdinalIgnoreCase));

                    if (isEML || isQuayhuanPA)
                    {
                        string conflictedMod = isEML ? "Extended Managers Library (EML)" : "Quayhuan's Prop Anarchy (requires EML)";
                        string warnMsg = $"Detected '{conflictedMod}' enabled. This is incompatible with More Props BETA.";
                        Log.Warning(warnMsg);
                        UIView.library.ShowModal<ExceptionPanel>("ExceptionPanel").SetMessage(
                            "Incompatible Mod Detected",
                            $"More Props BETA detected '{conflictedMod}' enabled.\n\n" +
                            "This mod requires EML and cannot run alongside More Props BETA. " +
                            "Please use 'Prop Control' (by algernon) or classic 'Prop & Tree Anarchy' instead!",
                            true
                        );
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception("CheckModConflicts", ex);
            }
        }
    }

    internal static class Patcher
    {
        private static bool s_isPatched = false;

        public static void PatchAll()
        {
            if (s_isPatched) return;

            try
            {
                HarmonyLib.Harmony harmony = new HarmonyLib.Harmony(ModInfo.HARMONY_ID);
                harmony.PatchAll(Assembly.GetExecutingAssembly());
                ThirdPartyLimitIntegrations.TryPatchLimitMods(harmony);
                s_isPatched = true;
                Log.Info("Harmony patches applied successfully.");
            }
            catch (Exception ex)
            {
                Log.Exception("Patcher.PatchAll", ex);
            }
        }

        public static void UnpatchAll()
        {
            if (!s_isPatched) return;

            try
            {
                HarmonyLib.Harmony harmony = new HarmonyLib.Harmony(ModInfo.HARMONY_ID);
                harmony.UnpatchAll(ModInfo.HARMONY_ID);
                s_isPatched = false;
                Log.Info("Harmony patches unpatched.");
            }
            catch (Exception ex)
            {
                Log.Exception("Patcher.UnpatchAll", ex);
            }
        }
    }
}
