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

        public void Update()
        {
            try
            {
                // Phím tắt Alt + P để bật / tắt bảng thông số
                if (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt))
                {
                    if (Input.GetKeyDown(KeyCode.P))
                    {
                        TogglePanel();
                    }
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

        public static void TryPatchLimitMods(HarmonyLib.Harmony harmony)
        {
            if (harmony == null) return;
            TryPatchCSLShowMoreLimits(harmony);
            TryPatchWatchIt(harmony);
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
                    Log.Info("Successfully patched CSLShowMoreLimits to display 262,144 prop capacity with standard formatting.");
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
                        Log.Info($"Detected WatchIt assembly: {name}. Registering integration.");
                        s_watchItPatched = true;
                        break;
                    }
                }
            }
            catch
            {
                // Graceful fallback if WatchIt is absent
            }
        }

        /// <summary>
        /// Gán kích thước tối đa (MaxsizesInt[15] = 262,144) và ngưỡng giới hạn (LimitsizesInt[15] = 262,139)
        /// vào CSL Show More Limits, tạo tiêu đề: User Props [262144] | [262139]:
        /// </summary>
        public static void CSL_FetchMaxSizeLimitSizeData_Postfix(object __instance)
        {
            try
            {
                if (__instance == null) return;
                FieldInfo fLimits = __instance.GetType().GetField("LimitsizesInt", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                FieldInfo fMax = __instance.GetType().GetField("MaxsizesInt", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (fLimits != null)
                {
                    object[] arr = fLimits.GetValue(__instance) as object[];
                    if (arr != null && arr.Length > 15) arr[15] = (int)ExtendedPropManager.MAX_EXTENDED_PROPS - 5;
                }
                if (fMax != null)
                {
                    object[] arr = fMax.GetValue(__instance) as object[];
                    if (arr != null && arr.Length > 15) arr[15] = (int)ExtendedPropManager.MAX_EXTENDED_PROPS;
                }

                // Cập nhật text nhãn tiêu đề nếu đã khởi tạo
                FieldInfo fUserPropsText = __instance.GetType().GetField("m_UserPropsText", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (fUserPropsText != null)
                {
                    UILabel textLabel = fUserPropsText.GetValue(__instance) as UILabel;
                    if (textLabel != null)
                    {
                        textLabel.text = string.Format("User Props     [{0}]  |  [{1}]:", ExtendedPropManager.MAX_EXTENDED_PROPS, ExtendedPropManager.MAX_EXTENDED_PROPS - 5);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception("CSL_FetchMaxSizeLimitSizeData_Postfix", ex);
            }
        }

        /// <summary>
        /// Prefix trước khi CSL Show More Limits chạy RefreshDisplayData:
        /// Đồng bộ số lượng Prop thực tế (256K) vào mảng nội bộ _tmpotherdata[5] của CSL.
        /// Điều này đảm bảo:
        /// 1. CSL Show More Limits tự động đọc đúng số lượng 256K.
        /// 2. Chức năng Copy Clipboard và Log Data của CSL xuất ra đúng số liệu thực tế.
        /// 3. Hàm int.Parse(_tmpotherdata[5].ToString()) trong CSL không bị crash lỗi định dạng chuỗi.
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
        /// Postfix sau khi CSL Show More Limits hoàn tất RefreshDisplayData:
        /// Đảm bảo m_UserPropsValue hiển thị đúng định dạng chuẩn của CSL ("   {0}")
        /// Giống y hệt dòng Trees ("   {0}" -> "   208746") và các mod More khác, không còn lệch cột hay % lạc lõng.
        /// </summary>
        public static void CSL_RefreshDisplayData_Postfix(object __instance)
        {
            try
            {
                ExtendedPropManager extMgr = ExtendedPropManager.Instance;
                if (extMgr == null || __instance == null) return;

                FieldInfo fUserPropsValue = __instance.GetType().GetField("m_UserPropsValue", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (fUserPropsValue != null)
                {
                    UILabel label = fUserPropsValue.GetValue(__instance) as UILabel;
                    if (label != null)
                    {
                        uint count = extMgr.ExtendedPropCount;
                        label.text = string.Format("   {0}", count);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception("CSL_RefreshDisplayData_Postfix", ex);
            }
        }

        /// <summary>
        /// Postfix đồng bộ màu sắc cảnh báo theo bảng màu nguyên bản của CSL Show More Limits:
        /// - Dưới 90%: Màu xanh lá (0, 204, 0)
        /// - 90% đến 95%: Màu vàng cam cảnh báo (255, 204, 0)
        /// - Trên 95%: Màu đỏ tới hạn (255, 0, 0)
        /// </summary>
        public static void CSL_CheckStatsForColorChange_Postfix(object __instance)
        {
            try
            {
                ExtendedPropManager extMgr = ExtendedPropManager.Instance;
                if (extMgr == null || __instance == null) return;

                FieldInfo fUserPropsValue = __instance.GetType().GetField("m_UserPropsValue", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (fUserPropsValue != null)
                {
                    UILabel label = fUserPropsValue.GetValue(__instance) as UILabel;
                    if (label != null)
                    {
                        uint count = extMgr.ExtendedPropCount;
                        uint max = ExtendedPropManager.MAX_EXTENDED_PROPS;
                        float pct = (float)count / (float)max;

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
