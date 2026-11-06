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
        public const string MOD_VERSION = "1.0.10-BETA";
        public const string MOD_DESCRIPTION = "Expands the prop limit to 262,144 (256K) with High-FPS Active Window Hot-Swapping for 100% Move It & Prop Mod compatibility.";

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
                if (ExtendedPropManager.IsEMLOrPropAnarchyActive())
                {
                    ExtendedPropManager.s_emlCoexistenceMode = true;
                    Log.Info("[MorePropsBETA] Extended Managers Library (EML) or Prop Anarchy detected. Operating in Non-Interference Safe Mode (100% peaceful coexistence & 0% conflict).");
                    return;
                }

                PluginManager pluginManager = PluginManager.instance;
                foreach (PluginManager.PluginInfo plugin in pluginManager.GetPluginsInfo())
                {
                    if (!plugin.isEnabled) continue;

                    ulong pubId = plugin.publishedFileID.AsUInt64;
                    bool isEML = plugin.name.Contains("ExtendedManagersLibrary") || plugin.name.Contains("EML") || pubId == 2696146766;
                    bool isQuayhuanPA = pubId == 2696149201 || (plugin.name.Equals("PropAnarchy", StringComparison.OrdinalIgnoreCase));
                    bool isTreeAnarchy = pubId == 2527486462 || (plugin.name.Equals("TreeAnarchy", StringComparison.OrdinalIgnoreCase));

                    if (isEML || isQuayhuanPA || isTreeAnarchy)
                    {
                        ExtendedPropManager.s_emlCoexistenceMode = true;
                        Log.Info($"[MorePropsBETA] Detected '{plugin.name}' enabled. Operating in Non-Interference Safe Mode (100% peaceful coexistence & 0% conflict).");
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
                SafePatchClass(harmony, typeof(PropManagerPatches));
                SafePatchClass(harmony, typeof(ExtendedPropRenderer));
                ThirdPartyLimitIntegrations.TryPatchLimitMods(harmony);
                s_isPatched = true;
                Log.Info("Harmony patches applied successfully.");
            }
            catch (Exception ex)
            {
                Log.Exception("Patcher.PatchAll", ex);
            }
        }

        private static void SafePatchClass(HarmonyLib.Harmony harmony, Type patchClass)
        {
            try
            {
                harmony.CreateClassProcessor(patchClass).Patch();
            }
            catch (Exception ex)
            {
                Log.Warning($"Non-critical patch issue on {patchClass.Name}: {ex.Message}");
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
