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
        private static readonly FastList<PropInfo> s_renderedPrefabs = new FastList<PropInfo>();

        [HarmonyPostfix]
        public static void EndRenderingImplPostfix(RenderManager.CameraInfo cameraInfo)
        {
            if (Singleton<LoadingManager>.instance == null || !Singleton<LoadingManager>.instance.m_loadingComplete) return;
            if (ExtendedPropManager.s_emlCoexistenceMode) return;

            ExtendedPropManager extMgr = ExtendedPropManager.Instance;
            if (extMgr == null || extMgr.ExtendedPropCount == 0) return;

            Bounds cameraBounds = cameraInfo.m_bounds;
            Vector3 camPos = cameraInfo.m_position;
            const float MAX_PROP_RENDER_DIST = 1200f; // Ngưỡng tối đa prop có thể hiển thị trong Cities: Skylines

            float boxMinX = Mathf.Max(cameraBounds.min.x - 32f, camPos.x - MAX_PROP_RENDER_DIST);
            float boxMaxX = Mathf.Min(cameraBounds.max.x + 32f, camPos.x + MAX_PROP_RENDER_DIST);
            float boxMinZ = Mathf.Max(cameraBounds.min.z - 32f, camPos.z - MAX_PROP_RENDER_DIST);
            float boxMaxZ = Mathf.Min(cameraBounds.max.z + 32f, camPos.z + MAX_PROP_RENDER_DIST);

            if (boxMinX > boxMaxX || boxMinZ > boxMaxZ) return;

            int minX = ExtendedPropManager.WorldToGridX(boxMinX);
            int maxX = ExtendedPropManager.WorldToGridX(boxMaxX);
            int minZ = ExtendedPropManager.WorldToGridZ(boxMinZ);
            int maxZ = ExtendedPropManager.WorldToGridZ(boxMaxZ);

            Randomizer randomizer = new Randomizer(314159);

            TerrainManager tm = Singleton<TerrainManager>.instance;
            EventManager em = Singleton<EventManager>.instance;

            try
            {
                s_renderedPrefabs.Clear();

                for (int z = minZ; z <= maxZ; z++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        uint cell = (uint)(z * ExtendedPropManager.PROPGRID_RESOLUTION + x);
                        uint extId = extMgr.m_propGrid32[cell];
                        if (extId == 0) continue;
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
                                        if (info.m_lodCount == 0)
                                        {
                                            s_renderedPrefabs.Add(info);
                                        }

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

                // Xả các Batch LOD chỉ trên các prefab thực tế có prop unmounted được vẽ trong khung hình
                for (int i = 0; i < s_renderedPrefabs.m_size; i++)
                {
                    PropInfo info = s_renderedPrefabs.m_buffer[i];
                    if (info != null && info.m_lodCount > 0)
                    {
                        try
                        {
                            PropInstance.RenderLod(cameraInfo, info);
                        }
                        catch
                        {
                            info.m_lodCount = 0;
                        }
                    }
                }
                s_renderedPrefabs.Clear();
            }
            catch (Exception ex)
            {
                Log.Exception("EndRenderingImplPostfix", ex);
            }
        }
    }
}
