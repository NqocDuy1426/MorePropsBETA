using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ColossalFramework;
using ColossalFramework.UI;

namespace MorePropsBETA
{
    public sealed class LimitDisplayPanel : UIPanel
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
            m_vanillaBufferLabel.text = "Active Buffer: 0 / 65,536";
            m_vanillaBufferLabel.textScale = 0.75f;
            m_vanillaBufferLabel.relativePosition = new Vector3(12, 38);

            m_total256kLabel = AddUIComponent<UILabel>();
            m_total256kLabel.text = "Total Props: 0 / 262,144";
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
                    uint totalCount = extMgr.ExtendedPropCount;

                    m_vanillaBufferLabel.text = $"Active Buffer: {activeCount:N0} / 65,536";
                    m_total256kLabel.text = $"Total Props: {totalCount:N0} / 262,144";

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

    public sealed class LimitDisplayHotkeyListener : MonoBehaviour
    {
        private LimitDisplayPanel m_panel;
        private float m_lastHeadroomCheck = 0f;
        private float m_lastDisplaySync = 0f;

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

                // Đồng bộ liên tục với CSL Show More Limits và các mod kiểm tra bất kể thứ tự tải/mở GUI
                if (Time.realtimeSinceStartup - m_lastDisplaySync > 1f)
                {
                    m_lastDisplaySync = Time.realtimeSinceStartup;
                    ThirdPartyLimitIntegrations.PeriodicSyncAllLimitDisplays();
                }

                // Bảo đảm headroom trong Active Buffer không bị tràn (> 55,000)
                if (Time.realtimeSinceStartup - m_lastHeadroomCheck > 3f)
                {
                    m_lastHeadroomCheck = Time.realtimeSinceStartup;
                    PropManager pm = Singleton<PropManager>.instance;
                    if (pm != null && pm.m_propCount > 55000)
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

    public static class ThirdPartyLimitIntegrations
    {
        private static bool s_cslPatched = false;
        private static bool s_watchItPatched = false;
        private static bool s_showItPatched = false;
        private static Type s_cslGuiType = null;

        public const int VANILLA_DEFAULT_PROP_LIMIT = 65531; // Giới hạn gốc của Cities: Skylines trước khi cài mod

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
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name == "CSLShowMoreLimits")
                    {
                        s_cslGuiType = asm.GetType("CSLShowMoreLimits.CSLShowMoreLimitsGUI");
                        break;
                    }
                }

                if (s_cslGuiType != null)
                {
                    MethodInfo mFetchMax = s_cslGuiType.GetMethod("FetchMaxSizeLimitSizeData", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    MethodInfo postFetchMax = typeof(ThirdPartyLimitIntegrations).GetMethod(nameof(CSL_FetchMaxSizeLimitSizeData_Postfix), BindingFlags.Public | BindingFlags.Static);
                    if (mFetchMax != null && postFetchMax != null)
                    {
                        harmony.Patch(mFetchMax, postfix: new HarmonyMethod(postFetchMax));
                    }

                    MethodInfo mRefresh = s_cslGuiType.GetMethod("RefreshDisplayData", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    MethodInfo preRefresh = typeof(ThirdPartyLimitIntegrations).GetMethod(nameof(CSL_RefreshDisplayData_Prefix), BindingFlags.Public | BindingFlags.Static);
                    MethodInfo postRefresh = typeof(ThirdPartyLimitIntegrations).GetMethod(nameof(CSL_RefreshDisplayData_Postfix), BindingFlags.Public | BindingFlags.Static);
                    if (mRefresh != null)
                    {
                        harmony.Patch(mRefresh, 
                            prefix: preRefresh != null ? new HarmonyMethod(preRefresh) : null,
                            postfix: postRefresh != null ? new HarmonyMethod(postRefresh) : null);
                    }

                    MethodInfo mCheckColor = s_cslGuiType.GetMethod("CheckStatsForColorChange", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    MethodInfo postColor = typeof(ThirdPartyLimitIntegrations).GetMethod(nameof(CSL_CheckStatsForColorChange_Postfix), BindingFlags.Public | BindingFlags.Static);
                    if (mCheckColor != null && postColor != null)
                    {
                        harmony.Patch(mCheckColor, postfix: new HarmonyMethod(postColor));
                    }

                    s_cslPatched = true;
                    Log.Info("Successfully patched CSLShowMoreLimits for 262,144 prop capacity and dual-column display.");
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
                        Log.Info($"WatchIt detected and patched for 256K Prop limits.");
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
                        Log.Info($"ShowIt detected and patched for 256K Prop limits.");
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
            __result = (int)ExtendedPropManager.MAX_EXTENDED_PROPS;
        }

        public static void WatchIt_GetUsage_Postfix(ref int __result)
        {
            ExtendedPropManager extMgr = ExtendedPropManager.Instance;
            if (extMgr != null)
            {
                __result = (int)extMgr.ExtendedPropCount;
            }
        }

        /// <summary>
        /// Đồng bộ định kỳ với mọi instance của CSL Show More Limits đang hiển thị trên màn hình.
        /// Bất kể người dùng bật/tắt (Ctrl + L), tải save mới, đổi cửa sổ,... đều đảm bảo 100% hiển thị chuẩn.
        /// </summary>
        public static void PeriodicSyncAllLimitDisplays()
        {
            try
            {
                ExtendedPropManager extMgr = ExtendedPropManager.Instance;
                if (extMgr == null) return;

                if (s_cslGuiType != null)
                {
                    UnityEngine.Object[] objs = UnityEngine.Object.FindObjectsOfType(s_cslGuiType);
                    if (objs != null && objs.Length > 0)
                    {
                        foreach (object gui in objs)
                        {
                            SyncCSLShowMoreLimitsInstance(gui, extMgr);
                        }
                    }
                }
            }
            catch
            {
                // Silent catch for periodic polling
            }
        }

        /// <summary>
        /// Thực thi đồng bộ toàn diện trên một instance CSL Show More Limits:
        /// 1. #maxsize: size sau khi unlock = 262,144 (MaxsizesInt[15])
        /// 2. #defaultLimit: limit trước khi unlock = 65,531 (LimitsizesInt[15])
        /// 3. #In Use & [#itemCount]: hiển thị đầy đủ hai cột như các thông số Net Segments, Buildings,... ("   {0}    |    [{1}]")
        /// 4. Màu sắc: Xanh lá (<90%), Vàng cam (90-95%), Đỏ (>=95%)
        /// 5. Bộ nhớ đệm dữ liệu: _tmpotherdata[5] = (int)inUse (bảo đảm an toàn cho Copy & Log)
        /// </summary>
        public static void SyncCSLShowMoreLimitsInstance(object guiInstance, ExtendedPropManager extMgr)
        {
            if (guiInstance == null || extMgr == null) return;

            try
            {
                Type guiType = guiInstance.GetType();

                // 1. Gán #maxsize sau khi unlock (262144) và #defaultLimit trước khi unlock (65531)
                FieldInfo fLimits = guiType.GetField("LimitsizesInt", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                FieldInfo fMax = guiType.GetField("MaxsizesInt", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (fLimits != null)
                {
                    object[] arr = fLimits.GetValue(guiInstance) as object[];
                    if (arr != null && arr.Length > 15) arr[15] = VANILLA_DEFAULT_PROP_LIMIT;
                }
                if (fMax != null)
                {
                    object[] arr = fMax.GetValue(guiInstance) as object[];
                    if (arr != null && arr.Length > 15) arr[15] = (int)ExtendedPropManager.MAX_EXTENDED_PROPS;
                }

                // 2. Cập nhật nhãn cột trái: "User Props     [262144]  |  [65531]:"
                FieldInfo fUserPropsText = guiType.GetField("m_UserPropsText", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (fUserPropsText != null)
                {
                    UILabel textLabel = fUserPropsText.GetValue(guiInstance) as UILabel;
                    if (textLabel != null)
                    {
                        textLabel.text = string.Format("User Props     [{0}]  |  [{1}]:", ExtendedPropManager.MAX_EXTENDED_PROPS, VANILLA_DEFAULT_PROP_LIMIT);
                    }
                }

                uint inUse = extMgr.ExtendedPropCount;
                uint itemCount = inUse > 0 ? inUse + 1 : 0;

                // 3. Gán bộ nhớ số nguyên nội bộ cho CSL: _tmpotherdata[5] = (int)inUse
                FieldInfo fTmpOtherData = guiType.GetField("_tmpotherdata", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (fTmpOtherData != null)
                {
                    object[] arr = fTmpOtherData.GetValue(guiInstance) as object[];
                    if (arr != null && arr.Length > 5)
                    {
                        arr[5] = (int)inUse;
                    }
                }

                // 4. Gán nhãn cột phải hiển thị đầy đủ cả hai cột: #In Use và [#itemCount]
                // Định dạng "   {0}    |    [{1}]" chuẩn xác theo Net Segments, Net Nodes, Buildings, Vehicles Active,...
                FieldInfo fUserPropsValue = guiType.GetField("m_UserPropsValue", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (fUserPropsValue != null)
                {
                    UILabel label = fUserPropsValue.GetValue(guiInstance) as UILabel;
                    if (label != null)
                    {
                        label.text = string.Format("   {0}    |    [{1}]", inUse, itemCount);

                        // 5. Màu sắc chuẩn CSL Show More Limits theo tỉ lệ 256K
                        float pct = (float)inUse / (float)ExtendedPropManager.MAX_EXTENDED_PROPS;
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
                Log.Exception("SyncCSLShowMoreLimitsInstance", ex);
            }
        }

        public static void CSL_FetchMaxSizeLimitSizeData_Postfix(object __instance)
        {
            SyncCSLShowMoreLimitsInstance(__instance, ExtendedPropManager.Instance);
        }

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

        public static void CSL_RefreshDisplayData_Postfix(object __instance)
        {
            SyncCSLShowMoreLimitsInstance(__instance, ExtendedPropManager.Instance);
        }

        public static void CSL_CheckStatsForColorChange_Postfix(object __instance)
        {
            SyncCSLShowMoreLimitsInstance(__instance, ExtendedPropManager.Instance);
        }
    }
}
