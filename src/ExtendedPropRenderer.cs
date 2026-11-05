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

            TerrainManager tm = Singleton<TerrainManager>.instance;
            EventManager em = Singleton<EventManager>.instance;

            try
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        uint cell = (uint)(z * ExtendedPropManager.PROPGRID_RESOLUTION + x);
                        uint extId = extMgr.m_propGrid32[cell];
                        int walk = 0;

                        while (extId != 0)
                        {
                            if (++walk > 65536) break;
                            ref ExtendedPropInstance prop = ref extMgr.m_props256k[extId];
                            uint next = prop.m_nextGridProp;

                            // CHỈ RENDER các prop đang ở trạng thái UNMOUNTED
                            // (Các prop MOUNTED đã được game gốc vẽ qua m_props trong vòng lặp trước đó)
                            if (prop.IsCreated && !prop.IsMounted && (prop.m_flags & (ushort)PropInstance.Flags.Hidden) == 0)
                            {
                                PropInfo info = PrefabCollection<PropInfo>.GetPrefab(prop.m_infoIndex);
                                if (info != null && cameraInfo.CheckRenderDistance(prop.m_position, info.m_maxRenderDistance))
                                {
                                    // Kiểm tra View Frustum Culling
                                    if (cameraInfo.Intersect(prop.m_position, info.m_generatedInfo.m_size.y * info.m_maxScale))
                                    {
                                        float scale = info.m_minScale + (float)randomizer.Int32(10000u) * (info.m_maxScale - info.m_minScale) * 0.0001f;
                                        Color color = info.GetColor(ref randomizer);

                                        // Gọi chính xác overload RenderInstance theo cấu hình của PropInfo
                                        // (đảm bảo truyền đầy đủ heightMap/waterMap để tránh crash ArgumentNullException khi xuất LOD)
                                        if (info.m_requireWaterMap && tm != null)
                                        {
                                            tm.GetHeightMapping(prop.m_position, out Texture heightMap, out Vector4 heightMapping, out Vector4 surfaceMapping);
                                            tm.GetWaterMapping(prop.m_position, out Texture waterHeightMap, out Vector4 waterHeightMapping, out Vector4 waterSurfaceMapping);
                                            PropInstance.RenderInstance(
                                                cameraInfo, info, default(InstanceID), prop.m_position, scale, prop.m_angle, color,
                                                RenderManager.DefaultColorLocation, true,
                                                heightMap, heightMapping, surfaceMapping,
                                                waterHeightMap, waterHeightMapping, waterSurfaceMapping
                                            );
                                        }
                                        else if (info.m_requireHeightMap && tm != null)
                                        {
                                            tm.GetHeightMapping(prop.m_position, out Texture heightMap, out Vector4 heightMapping, out Vector4 surfaceMapping);
                                            PropInstance.RenderInstance(
                                                cameraInfo, info, default(InstanceID), prop.m_position, scale, prop.m_angle, color,
                                                RenderManager.DefaultColorLocation, true,
                                                heightMap, heightMapping, surfaceMapping
                                            );
                                        }
                                        else
                                        {
                                            Vector4 billboardRect = (info.m_eventBillboard && em != null) ? em.blankBillboardRect : Vector4.zero;
                                            PropInstance.RenderInstance(
                                                cameraInfo, info, default(InstanceID), prop.m_position, scale, prop.m_angle, color,
                                                RenderManager.DefaultColorLocation, true,
                                                billboardRect
                                            );
                                        }
                                    }
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
                        try
                        {
                            PropInstance.RenderLod(cameraInfo, info);
                        }
                        catch
                        {
                            // Nếu một asset bị lỗi LOD texture/material, reset lodCount để tránh tái diễn lỗi ở frame sau
                            info.m_lodCount = 0;
                        }
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
