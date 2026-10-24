using System;
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
}
