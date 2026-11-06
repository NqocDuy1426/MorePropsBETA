using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ColossalFramework;
using ColossalFramework.UI;

namespace MorePropsBETA
{
    internal sealed class LimitDisplayPanel : UIPanel
    {
        private UILabel m_titleLabel;
        private UILabel m_vanillaBufferLabel;
        private UILabel m_total256kLabel;
        private float m_lastUpdateTime = 0f;

        public override void Start()
        {
            base.Start();

            name = "MorePropsBETA_Monitor";
            backgroundSprite = "MenuPanel2";
            canFocus = true;
            isInteractive = true;
            width = 250;
            height = 100;
            relativePosition = new Vector3(80, 80);

            UIDragHandle dragHandle = AddUIComponent<UIDragHandle>();
            dragHandle.width = width;
            dragHandle.height = 30;
            dragHandle.relativePosition = Vector3.zero;
            dragHandle.target = this;

            m_titleLabel = AddUIComponent<UILabel>();
            m_titleLabel.text = "More Props BETA Monitor";
            m_titleLabel.textScale = 0.85f;
            m_titleLabel.textColor = new Color32(0, 255, 180, 255);
            m_titleLabel.relativePosition = new Vector3(12, 10);

            m_vanillaBufferLabel = AddUIComponent<UILabel>();
            m_vanillaBufferLabel.text = "Active Buffer: 0";
            m_vanillaBufferLabel.textScale = 0.75f;
            m_vanillaBufferLabel.relativePosition = new Vector3(12, 38);

            m_total256kLabel = AddUIComponent<UILabel>();
            m_total256kLabel.text = "Total Props: 0";
            m_total256kLabel.textScale = 0.75f;
            m_total256kLabel.relativePosition = new Vector3(12, 65);
        }

        public override void Update()
        {
            base.Update();

            try
            {
                if (Time.realtimeSinceStartup - m_lastUpdateTime < 0.5f) return;
                m_lastUpdateTime = Time.realtimeSinceStartup;

                PropManager pm = Singleton<PropManager>.instance;
                ExtendedPropManager extMgr = ExtendedPropManager.Instance;

                if (pm != null && extMgr != null)
                {
                    int activeCount = pm.m_propCount;
                    uint bufferCapacity = pm.m_props.m_size;
                    uint totalCount = extMgr.ExtendedPropCount;
                    uint maxCapacity = extMgr.MaxProps;

                    m_vanillaBufferLabel.text = $"Active Buffer: {activeCount:N0} / {bufferCapacity:N0}";
                    m_total256kLabel.text = $"Total Props: {totalCount:N0} / {maxCapacity:N0}";

                    if (activeCount > ExtendedPropManager.SAFE_VANILLA_THRESHOLD)
                    {
                        m_vanillaBufferLabel.textColor = new Color32(255, 160, 0, 255); // Cảnh báo vàng cam
                    }
                    else
                    {
                        m_vanillaBufferLabel.textColor = Color.white;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception("LimitDisplayPanel.Update", ex);
            }
        }
    }

    internal sealed class LimitDisplayHotkeyListener : MonoBehaviour
    {
        private LimitDisplayPanel m_panel;
        private float m_lastHeadroomCheck = 0f;

        public void Update()
        {
            try
            {
                // Phím tắt Alt + P để bật / tắt bảng thông số mini
                if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt))
                {
                    if (Input.GetKeyDown(KeyCode.P))
                    {
                        TogglePanel();
                    }
                }

                // Bảo đảm headroom trong Active Buffer không bị tràn (> 63,500)
                if (Time.realtimeSinceStartup - m_lastHeadroomCheck > 5f)
                {
                    m_lastHeadroomCheck = Time.realtimeSinceStartup;
                    PropManager pm = Singleton<PropManager>.instance;
                    if (pm != null && pm.m_propCount > 63500)
                    {
                        Vector3 focus = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
                        ExtendedPropManager.Instance?.EnsureBufferHeadroom(focus, ExtendedPropManager.SAFE_VANILLA_THRESHOLD);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception("LimitDisplayHotkeyListener.Update", ex);
            }
        }

        public void TogglePanel()
        {
            try
            {
                if (m_panel == null)
                {
                    UIView view = UIView.GetAView();
                    if (view != null)
                    {
                        m_panel = (LimitDisplayPanel)view.AddUIComponent(typeof(LimitDisplayPanel));
                    }
                }
                else
                {
                    m_panel.isVisible = !m_panel.isVisible;
                }
            }
            catch (Exception ex)
            {
                Log.Exception("LimitDisplayHotkeyListener.TogglePanel", ex);
            }
        }
    }

    internal static class ThirdPartyLimitIntegrations
    {
        private static bool s_cslPatched = false;
        private static bool s_watchItPatched = false;
        private static bool s_showItPatched = false;

        public const int VANILLA_DEFAULT_PROP_LIMIT = 65531; // Hằng số duy nhất: Giới hạn gốc của Cities: Skylines trước khi cài mod

        public static void TryPatchLimitMods(HarmonyLib.Harmony harmony)
        {
            if (harmony == null) return;
            TryPatchCSLShowMoreLimits(harmony);
            TryPatchWatchIt(harmony);
            TryPatchShowIt(harmony);
        }

        private static void TryPatchCSLShowMoreLimits(HarmonyLib.Harmony harmony)
        {
            if (s_cslPatched) return;

            try
            {
                Type guiType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name == "CSLShowMoreLimits")
                    {
                        guiType = asm.GetType("CSLShowMoreLimits.CSLShowMoreLimitsGUI");
                        break;
                    }
                }

                if (guiType != null)
                {
                    MethodInfo mFetchMax = guiType.GetMethod("FetchMaxSizeLimitSizeData", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    MethodInfo postFetchMax = typeof(ThirdPartyLimitIntegrations).GetMethod(nameof(CSL_FetchMaxSizeLimitSizeData_Postfix), BindingFlags.Public | BindingFlags.Static);
                    if (mFetchMax != null && postFetchMax != null)
                    {
                        harmony.Patch(mFetchMax, postfix: new HarmonyMethod(postFetchMax));
                    }

                    MethodInfo mRefresh = guiType.GetMethod("RefreshDisplayData", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    MethodInfo preRefresh = typeof(ThirdPartyLimitIntegrations).GetMethod(nameof(CSL_RefreshDisplayData_Prefix), BindingFlags.Public | BindingFlags.Static);
                    MethodInfo postRefresh = typeof(ThirdPartyLimitIntegrations).GetMethod(nameof(CSL_RefreshDisplayData_Postfix), BindingFlags.Public | BindingFlags.Static);
                    if (mRefresh != null)
                    {
                        harmony.Patch(mRefresh, 
                            prefix: preRefresh != null ? new HarmonyMethod(preRefresh) : null,
                            postfix: postRefresh != null ? new HarmonyMethod(postRefresh) : null);
                    }

                    MethodInfo mCheckColor = guiType.GetMethod("CheckStatsForColorChange", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    MethodInfo postColor = typeof(ThirdPartyLimitIntegrations).GetMethod(nameof(CSL_CheckStatsForColorChange_Postfix), BindingFlags.Public | BindingFlags.Static);
                    if (mCheckColor != null && postColor != null)
                    {
                        harmony.Patch(mCheckColor, postfix: new HarmonyMethod(postColor));
                    }

                    s_cslPatched = true;
                    Log.Info("Successfully integrated CSLShowMoreLimits into native update lifecycle.");
                }
            }
            catch (Exception ex)
            {
                Log.Exception("TryPatchCSLShowMoreLimits", ex);
            }
        }

        private static void TryPatchWatchIt(HarmonyLib.Harmony harmony)
        {
            if (s_watchItPatched) return;

            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string name = asm.GetName().Name;
                    if (name.IndexOf("WatchIt", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        foreach (Type t in asm.GetTypes())
                        {
                            if (t.Name.Contains("Prop") && (t.Name.Contains("Data") || t.Name.Contains("Manager") || t.Name.Contains("Item") || t.Name.Contains("Panel")))
                            {
                                MethodInfo mTotal = t.GetProperty("Total", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetGetMethod();
                                MethodInfo mCapacity = t.GetProperty("Capacity", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetGetMethod();
                                MethodInfo mUsed = t.GetProperty("Used", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetGetMethod();
                                MethodInfo mCount = t.GetProperty("Count", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetGetMethod();

                                MethodInfo postLimit = typeof(ThirdPartyLimitIntegrations).GetMethod(nameof(WatchIt_GetLimit_Postfix), BindingFlags.Public | BindingFlags.Static);
                                MethodInfo postUsage = typeof(ThirdPartyLimitIntegrations).GetMethod(nameof(WatchIt_GetUsage_Postfix), BindingFlags.Public | BindingFlags.Static);

                                if (mTotal != null && postLimit != null) harmony.Patch(mTotal, postfix: new HarmonyMethod(postLimit));
                                if (mCapacity != null && postLimit != null) harmony.Patch(mCapacity, postfix: new HarmonyMethod(postLimit));
                                if (mUsed != null && postUsage != null) harmony.Patch(mUsed, postfix: new HarmonyMethod(postUsage));
                                if (mCount != null && postUsage != null) harmony.Patch(mCount, postfix: new HarmonyMethod(postUsage));
                            }
                        }
                        Log.Info("WatchIt detected and patched for dynamic prop monitoring.");
                        s_watchItPatched = true;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception("TryPatchWatchIt", ex);
            }
        }

        private static void TryPatchShowIt(HarmonyLib.Harmony harmony)
        {
            if (s_showItPatched) return;

            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string name = asm.GetName().Name;
                    if (name.IndexOf("ShowIt", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        foreach (Type t in asm.GetTypes())
                        {
                            if (t.Name.Contains("Prop") && (t.Name.Contains("Data") || t.Name.Contains("Manager") || t.Name.Contains("Limit")))
                            {
                                MethodInfo mTotal = t.GetProperty("Total", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetGetMethod();
                                MethodInfo mLimit = t.GetProperty("Limit", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetGetMethod();
                                MethodInfo mUsed = t.GetProperty("Used", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetGetMethod();
                                MethodInfo mCount = t.GetProperty("Count", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetGetMethod();

                                MethodInfo postLimit = typeof(ThirdPartyLimitIntegrations).GetMethod(nameof(WatchIt_GetLimit_Postfix), BindingFlags.Public | BindingFlags.Static);
                                MethodInfo postUsage = typeof(ThirdPartyLimitIntegrations).GetMethod(nameof(WatchIt_GetUsage_Postfix), BindingFlags.Public | BindingFlags.Static);

                                if (mTotal != null && postLimit != null) harmony.Patch(mTotal, postfix: new HarmonyMethod(postLimit));
                                if (mLimit != null && postLimit != null) harmony.Patch(mLimit, postfix: new HarmonyMethod(postLimit));
                                if (mUsed != null && postUsage != null) harmony.Patch(mUsed, postfix: new HarmonyMethod(postUsage));
                                if (mCount != null && postUsage != null) harmony.Patch(mCount, postfix: new HarmonyMethod(postUsage));
                            }
                        }
                        Log.Info("ShowIt detected and patched for dynamic prop monitoring.");
                        s_showItPatched = true;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception("TryPatchShowIt", ex);
            }
        }

        public static void WatchIt_GetLimit_Postfix(ref int __result)
        {
            ExtendedPropManager extMgr = ExtendedPropManager.Instance;
            if (extMgr != null)
            {
                __result = (int)extMgr.MaxProps; // Động từ manager
            }
        }

        public static void WatchIt_GetUsage_Postfix(ref int __result)
        {
            ExtendedPropManager extMgr = ExtendedPropManager.Instance;
            if (extMgr != null)
            {
                __result = (int)extMgr.ExtendedPropCount; // Động từ live count
            }
        }

        /// <summary>
        /// Postfix cho CSLShowMoreLimits.FetchMaxSizeLimitSizeData:
        /// Chạy tự động trong vòng đời tự cập nhật kích thước của CSL:
        /// - #maxsize: Lấy ĐỘNG từ kích thước thực tế của ExtendedPropManager.MaxProps
        /// - #defaultLimit: 65,531 (Giới hạn gốc của game trước khi unlock)
        /// </summary>
        public static void CSL_FetchMaxSizeLimitSizeData_Postfix(object __instance)
        {
            try
            {
                if (__instance == null) return;
                ExtendedPropManager extMgr = ExtendedPropManager.Instance;
                uint dynamicMaxSize = extMgr != null ? extMgr.MaxProps : (Singleton<PropManager>.instance != null ? (uint)Singleton<PropManager>.instance.m_props.m_size : 262144u);

                FieldInfo fLimits = __instance.GetType().GetField("LimitsizesInt", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                FieldInfo fMax = __instance.GetType().GetField("MaxsizesInt", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (fLimits != null)
                {
                    object[] arr = fLimits.GetValue(__instance) as object[];
                    if (arr != null && arr.Length > 15) arr[15] = VANILLA_DEFAULT_PROP_LIMIT;
                }
                if (fMax != null)
                {
                    object[] arr = fMax.GetValue(__instance) as object[];
                    if (arr != null && arr.Length > 15) arr[15] = (int)dynamicMaxSize;
                }

                // Cập nhật nhãn tiêu đề: User Props [dynamicMaxSize] | [65531]:
                FieldInfo fUserPropsText = __instance.GetType().GetField("m_UserPropsText", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (fUserPropsText != null)
                {
                    UILabel textLabel = fUserPropsText.GetValue(__instance) as UILabel;
                    if (textLabel != null)
                    {
                        textLabel.text = string.Format("User Props     [{0}]  |  [{1}]:", dynamicMaxSize, VANILLA_DEFAULT_PROP_LIMIT);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception("CSL_FetchMaxSizeLimitSizeData_Postfix", ex);
            }
        }

        /// <summary>
        /// Prefix cho CSLShowMoreLimits.RefreshDisplayData:
        /// Chạy tự động theo chu kỳ refresh tự nhiên của CSL (AutoRefresh 3s hoặc nút Manual Refresh):
        /// Cung cấp số liệu động thực tế vào bộ đệm nội bộ _tmpotherdata[5] của CSL.
        /// </summary>
        public static void CSL_RefreshDisplayData_Prefix(object __instance)
        {
            try
            {
                ExtendedPropManager extMgr = ExtendedPropManager.Instance;
                if (extMgr == null || __instance == null) return;

                FieldInfo fTmpOtherData = __instance.GetType().GetField("_tmpotherdata", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (fTmpOtherData != null)
                {
                    object[] arr = fTmpOtherData.GetValue(__instance) as object[];
                    if (arr != null && arr.Length > 5)
                    {
                        arr[5] = (int)extMgr.ExtendedPropCount;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception("CSL_RefreshDisplayData_Prefix", ex);
            }
        }

        /// <summary>
        /// Postfix cho CSLShowMoreLimits.RefreshDisplayData:
        /// Hiển thị đầy đủ cả hai cột động:
        /// - #In Use: extMgr.ExtendedPropCount (số prop thực tế đã đặt trên bản đồ)
        /// - #itemCount: extMgr.DynamicItemCount (tổng số slot item đang dùng theo chuẩn m_size - m_unusedCount)
        /// </summary>
        public static void CSL_RefreshDisplayData_Postfix(object __instance)
        {
            try
            {
                ExtendedPropManager extMgr = ExtendedPropManager.Instance;
                if (extMgr == null || __instance == null) return;

                uint inUse = extMgr.ExtendedPropCount;
                uint itemCount = extMgr.DynamicItemCount;

                FieldInfo fUserPropsValue = __instance.GetType().GetField("m_UserPropsValue", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (fUserPropsValue != null)
                {
                    UILabel label = fUserPropsValue.GetValue(__instance) as UILabel;
                    if (label != null)
                    {
                        label.text = string.Format("   {0}    |    [{1}]", inUse, itemCount);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception("CSL_RefreshDisplayData_Postfix", ex);
            }
        }

        /// <summary>
        /// Postfix cho CSLShowMoreLimits.CheckStatsForColorChange:
        /// Chạy tự động ngay sau khi CSL refresh dữ liệu:
        /// Cập nhật màu sắc động dựa trên tỉ lệ thực tế giữa inUse và MaxProps.
        /// </summary>
        public static void CSL_CheckStatsForColorChange_Postfix(object __instance)
        {
            try
            {
                ExtendedPropManager extMgr = ExtendedPropManager.Instance;
                if (extMgr == null || __instance == null) return;

                uint inUse = extMgr.ExtendedPropCount;
                uint maxProps = extMgr.MaxProps;

                FieldInfo fUserPropsValue = __instance.GetType().GetField("m_UserPropsValue", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (fUserPropsValue != null)
                {
                    UILabel label = fUserPropsValue.GetValue(__instance) as UILabel;
                    if (label != null)
                    {
                        float pct = maxProps > 0 ? (float)inUse / (float)maxProps : 0f;
                        if (pct >= 0.95f)
                            label.textColor = new Color32(255, 0, 0, 255);
                        else if (pct >= 0.90f)
                            label.textColor = new Color32(255, 204, 0, 255);
                        else
                            label.textColor = new Color32(0, 204, 0, 255);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception("CSL_CheckStatsForColorChange_Postfix", ex);
            }
        }
    }
}
