using System;
using HarmonyLib;
using ColossalFramework;
using ColossalFramework.Math;
using UnityEngine;

namespace MorePropsBETA
{
    [HarmonyPatch(typeof(PropManager), "EndRenderingImpl")]
    internal static class ExtendedPropRenderer
    {
        [HarmonyPostfix]
        public static void EndRenderingImplPostfix(RenderManager.CameraInfo cameraInfo)
        {
            if (Singleton<LoadingManager>.instance == null || !Singleton<LoadingManager>.instance.m_loadingComplete) return;

            ExtendedPropManager extMgr = ExtendedPropManager.Instance;
            if (extMgr == null || extMgr.ExtendedPropCount == 0) return;

            Bounds cameraBounds = cameraInfo.m_bounds;

            int minX = ExtendedPropManager.WorldToGridX(cameraBounds.min.x - 32f);
            int maxX = ExtendedPropManager.WorldToGridX(cameraBounds.max.x + 32f);
            int minZ = ExtendedPropManager.WorldToGridZ(cameraBounds.min.z - 32f);
            int maxZ = ExtendedPropManager.WorldToGridZ(cameraBounds.max.z + 32f);

            Randomizer randomizer = new Randomizer(314159);

            try
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        uint cell = (uint)(z * ExtendedPropManager.PROPGRID_RESOLUTION + x);
                        uint extId = extMgr.m_propGrid32[cell];

                        while (extId != 0)
                        {
                            ref ExtendedPropInstance prop = ref extMgr.m_props256k[extId];
                            uint next = prop.m_nextGridProp;

                            // CHỈ RENDER các prop đang ở trạng thái UNMOUNTED
                            // (Các prop MOUNTED đã được game gốc vẽ qua m_props trong vòng lặp trước đó)
                            if (prop.IsCreated && !prop.IsMounted && (prop.m_flags & (ushort)PropInstance.Flags.Hidden) == 0)
                            {
                                PropInfo info = PrefabCollection<PropInfo>.GetPrefab(prop.m_infoIndex);
                                if (info != null && cameraInfo.CheckRenderDistance(prop.m_position, info.m_maxRenderDistance))
                                {
                                    float scale = (info.m_minScale + info.m_maxScale) * 0.5f;
                                    Color color = info.GetColor(ref randomizer);

                                    // Gọi chính xác phương thức RenderInstance gốc của game engine
                                    PropInstance.RenderInstance(
                                        cameraInfo,
                                        info,
                                        default(InstanceID),
                                        prop.m_position,
                                        scale,
                                        prop.m_angle,
                                        color,
                                        RenderManager.DefaultColorLocation,
                                        true
                                    );
                                }
                            }

                            extId = next;
                        }
                    }
                }

                // Xả các Batch LOD còn đọng trong PropInfo để hoàn tất khung hình
                int propCount = PrefabCollection<PropInfo>.PrefabCount();
                for (int i = 0; i < propCount; i++)
                {
                    PropInfo info = PrefabCollection<PropInfo>.GetPrefab((uint)i);
                    if (info != null && info.m_lodCount > 0)
                    {
                        PropInstance.RenderLod(cameraInfo, info);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception("EndRenderingImplPostfix", ex);
            }
        }
    }
}
