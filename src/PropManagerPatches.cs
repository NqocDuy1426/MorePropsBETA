using System;
using System.Reflection;
using HarmonyLib;
using ColossalFramework;
using ColossalFramework.Math;
using UnityEngine;

namespace MorePropsBETA
{
    [HarmonyPatch]
    public static class PropManagerPatches
    {
        // ==========================================
        // 1. PATCH CheckLimits: BỎ QUA GIỚI HẠN CỨNG 65531 CỦA GAME GỐC
        // ==========================================
        [HarmonyPatch(typeof(PropManager), "CheckLimits")]
        [HarmonyPrefix]
        public static bool CheckLimitsPrefix(ref bool __result)
        {
            try
            {
                ExtendedPropManager extMgr = ExtendedPropManager.Instance;
                if (extMgr != null)
                {
                    __result = extMgr.ExtendedPropCount < ExtendedPropManager.MAX_EXTENDED_PROPS - 1;
                    return false; // Bỏ qua hoàn toàn hàm CheckLimits 65K của game gốc
                }
            }
            catch (Exception ex)
            {
                Log.Exception("CheckLimitsPrefix", ex);
            }
            return true;
        }

        // ==========================================
        // 2. PATCH CreateProp: ĐẢM BẢO CÒN SLOT & ĐỒNG BỘ 256K
        // ==========================================
        [HarmonyPatch(typeof(PropManager), nameof(PropManager.CreateProp))]
        [HarmonyPrefix]
        public static bool CreatePropPrefix(ref bool __result, out ushort prop, ref Randomizer randomizer, PropInfo info, Vector3 position, float angle, bool single)
        {
            prop = 0;
            try
            {
                ExtendedPropManager extMgr = ExtendedPropManager.Instance;
                if (extMgr == null || extMgr.m_isInternalOperation)
                {
                    return true;
                }

                if (extMgr.ExtendedPropCount >= ExtendedPropManager.MAX_EXTENDED_PROPS - 1)
                {
                    __result = false;
                    Log.Warning("Prop limit ceiling reached: 262,144 props. Creation blocked.");
                    return false; // Đã chạm mốc 262.144 prop tối đa
                }

                PropManager pm = Singleton<PropManager>.instance;
                if (pm != null && pm.m_propCount >= ExtendedPropManager.SAFE_VANILLA_THRESHOLD)
                {
                    // Unmount bớt các prop ở xa để luôn có sẵn slot trống cho công cụ đặt prop
                    extMgr.EvictDistantProps(position, 600f, 1000);
                }
            }
            catch (Exception ex)
            {
                Log.Exception("CreatePropPrefix", ex);
            }

            return true; // Cho phép vanilla CreateProp thực thi cấp phát slot active
        }

        [HarmonyPatch(typeof(PropManager), nameof(PropManager.CreateProp))]
        [HarmonyPostfix]
        public static void CreatePropPostfix(bool __result, ushort prop, PropInfo info, Vector3 position, float angle, bool single)
        {
            try
            {
                ExtendedPropManager extMgr = ExtendedPropManager.Instance;
                if (extMgr == null || extMgr.m_isInternalOperation) return;

                if (__result && prop != 0)
                {
                    // Nếu chưa được liên kết với 256K, cấp phát một extended ID mới
                    if (extMgr.m_vanillaToExtendedId[prop] == 0)
                    {
                        if (extMgr.AllocateExtendedProp(out uint extId))
                        {
                            ref ExtendedPropInstance extProp = ref extMgr.m_props256k[extId];
                            extProp.m_position = position;
                            extProp.m_angle = angle;
                            extProp.m_infoIndex = (ushort)info.m_prefabDataIndex;
                            ushort flags = (ushort)PropInstance.Flags.Created;
                            if (single) flags |= (ushort)PropInstance.Flags.Single;
                            extProp.m_flags = flags;
                            extProp.m_mountedVanillaId = prop;
                            extProp.m_lastAccessTime = Time.realtimeSinceStartup;

                            extMgr.m_vanillaToExtendedId[prop] = extId;
                            extMgr.m_extendedToVanillaId[extId] = prop;

                            extMgr.AddToGrid32(extId, position);
                        }
                        else
                        {
                            Log.Error($"AllocateExtendedProp failed for vanillaId={prop}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception("CreatePropPostfix", ex);
            }
        }

        // ==========================================
        // 3. PATCH ReleaseProp: GIẢI PHÓNG VÀ ĐỒNG BỘ 2 CHIỀU
        // ==========================================
        [HarmonyPatch(typeof(PropManager), nameof(PropManager.ReleaseProp))]
        [HarmonyPrefix]
        public static void ReleasePropPrefix(ushort prop)
        {
            try
            {
                ExtendedPropManager extMgr = ExtendedPropManager.Instance;
                if (extMgr == null || extMgr.m_isInternalOperation || prop == 0) return;

                uint extendedId = extMgr.m_vanillaToExtendedId[prop];
                if (extendedId != 0)
                {
                    extMgr.ReleaseExtendedProp(extendedId);
                }
            }
            catch (Exception ex)
            {
                Log.Exception("ReleasePropPrefix", ex);
            }
        }

        // ==========================================
        // 4. PATCH MoveProp, UpdateProp, UpdatePropRenderer: ĐỒNG BỘ DỮ LIỆU TỨC THÌ
        // ==========================================
        [HarmonyPatch(typeof(PropManager), nameof(PropManager.MoveProp))]
        [HarmonyPostfix]
        public static void MovePropPostfix(ushort prop, Vector3 position)
        {
            try
            {
                ExtendedPropManager extMgr = ExtendedPropManager.Instance;
                if (extMgr == null || extMgr.m_isInternalOperation || prop == 0) return;

                extMgr.SyncFromVanilla(prop);
            }
            catch (Exception ex)
            {
                Log.Exception("MovePropPostfix", ex);
            }
        }

        [HarmonyPatch(typeof(PropManager), nameof(PropManager.UpdateProp))]
        [HarmonyPostfix]
        public static void UpdatePropPostfix(ushort prop)
        {
            try
            {
                ExtendedPropManager extMgr = ExtendedPropManager.Instance;
                if (extMgr == null || extMgr.m_isInternalOperation || prop == 0) return;

                extMgr.SyncFromVanilla(prop);
            }
            catch (Exception ex)
            {
                Log.Exception("UpdatePropPostfix", ex);
            }
        }

        [HarmonyPatch(typeof(PropManager), nameof(PropManager.UpdatePropRenderer))]
        [HarmonyPostfix]
        public static void UpdatePropRendererPostfix(ushort prop, bool updateGroup)
        {
            try
            {
                ExtendedPropManager extMgr = ExtendedPropManager.Instance;
                if (extMgr == null || extMgr.m_isInternalOperation || prop == 0) return;

                extMgr.SyncFromVanilla(prop);
            }
            catch (Exception ex)
            {
                Log.Exception("UpdatePropRendererPostfix", ex);
            }
        }

        // ==========================================
        // 5. PATCH UpdateProps: BẮT VÙNG CHỌN / SỬA ĐỔI Ô LƯỚI TỰ ĐỘNG
        // ==========================================
        [HarmonyPatch(typeof(PropManager), nameof(PropManager.UpdateProps))]
        [HarmonyPrefix]
        public static void UpdatePropsPrefix(float minX, float minZ, float maxX, float maxZ)
        {
            try
            {
                ExtendedPropManager extMgr = ExtendedPropManager.Instance;
                if (extMgr == null || extMgr.m_isInternalOperation)
                {
                    return;
                }

                extMgr.m_isInternalOperation = true;
                try
                {
                    extMgr.MountPropsInRect(minX, minZ, maxX, maxZ);
                }
                finally
                {
                    extMgr.m_isInternalOperation = false;
                }
            }
            catch (Exception ex)
            {
                Log.Exception("UpdatePropsPrefix", ex);
            }
        }

        // ==========================================
        // 6. PATCH RayCast: TỰ ĐỘNG BẮT VÀ MOUNT KHI CHUỘT RÀ TRÚNG PROP 256K
        // ==========================================
        [HarmonyPatch(typeof(PropManager), nameof(PropManager.RayCast))]
        [HarmonyPrefix]
        public static bool RayCastPrefix(ref bool __result, Segment3 ray, ItemClass.Service service, ItemClass.SubService subService, ItemClass.Layer itemLayers, PropInstance.Flags ignoreFlags, out Vector3 hit, out ushort propIndex)
        {
            hit = Vector3.zero;
            propIndex = 0;

            try
            {
                ExtendedPropManager extMgr = ExtendedPropManager.Instance;
                if (extMgr == null)
                {
                    return true;
                }

                Vector3 a = ray.a;
                Vector3 b = ray.b;

                int minX = ExtendedPropManager.WorldToGridX(Mathf.Min(a.x, b.x) - 16f);
                int maxX = ExtendedPropManager.WorldToGridX(Mathf.Max(a.x, b.x) + 16f);
                int minZ = ExtendedPropManager.WorldToGridZ(Mathf.Min(a.z, b.z) - 16f);
                int maxZ = ExtendedPropManager.WorldToGridZ(Mathf.Max(a.z, b.z) + 16f);

                float minDistanceSqr = float.MaxValue;
                uint closestExtendedId = 0;
                Vector3 bestHit = Vector3.zero;

                for (int z = minZ; z <= maxZ; z++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        uint cell = (uint)(z * ExtendedPropManager.PROPGRID_RESOLUTION + x);
                        uint extId = extMgr.m_propGrid32[cell];

                        while (extId != 0)
                        {
                            ref ExtendedPropInstance extProp = ref extMgr.m_props256k[extId];
                            uint next = extProp.m_nextGridProp;

                            if (extProp.IsCreated && (extProp.m_flags & (ushort)ignoreFlags) == 0)
                            {
                                PropInfo info = PrefabCollection<PropInfo>.GetPrefab(extProp.m_infoIndex);
                                if (info != null)
                                {
                                    if ((service == ItemClass.Service.None || info.m_class.m_service == service) &&
                                        (subService == ItemClass.SubService.None || info.m_class.m_subService == subService) &&
                                        (itemLayers == ItemClass.Layer.None || (info.m_class.m_layer & itemLayers) != ItemClass.Layer.None))
                                    {
                                        float scale = (info.m_minScale + info.m_maxScale) * 0.5f;
                                        float height = info.m_mesh != null ? info.m_mesh.bounds.size.y * scale : 2f;
                                        float radius = Mathf.Max(info.m_mesh != null ? info.m_mesh.bounds.extents.x * scale : 1f, 0.5f);

                                        Bounds propBounds = new Bounds(extProp.m_position + new Vector3(0f, height * 0.5f, 0f), new Vector3(radius * 2f, height, radius * 2f));

                                        if (propBounds.IntersectRay(new Ray(ray.a, ray.b - ray.a), out float dist))
                                        {
                                            Vector3 candidateHit = ray.a + (ray.b - ray.a).normalized * dist;
                                            float distSqr = (candidateHit - ray.a).sqrMagnitude;

                                            if (distSqr < minDistanceSqr)
                                            {
                                                minDistanceSqr = distSqr;
                                                closestExtendedId = extId;
                                                bestHit = candidateHit;
                                            }
                                        }
                                    }
                                }
                            }

                            extId = next;
                        }
                    }
                }

                if (closestExtendedId != 0)
                {
                    // Mount prop trúng đích ngay lập tức vào Active Buffer để công cụ tương tác tức thì
                    propIndex = extMgr.MountProp(closestExtendedId);
                    hit = bestHit;
                    __result = (propIndex != 0);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Exception("RayCastPrefix", ex);
            }

            hit = Vector3.zero;
            propIndex = 0;
            return true; // Cho phép vanilla RayCast thực thi bình thường khi không trúng extended prop nào
        }
    }

    // ==========================================
    // DYNAMIC MOVE IT HOOK (REFLECTIVE ADAPTER)
    // ==========================================
    public static class DynamicMoveItHook
    {
        public static void TryHookMoveIt()
        {
            try
            {
                Assembly moveItAssembly = null;
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name.Equals("MoveIt", StringComparison.OrdinalIgnoreCase))
                    {
                        moveItAssembly = asm;
                        break;
                    }
                }

                if (moveItAssembly == null)
                {
                    Log.Info("Move It assembly not loaded in current AppDomain.");
                    return;
                }

                Type moveItToolType = moveItAssembly.GetType("MoveIt.MoveItTool");
                if (moveItToolType == null) return;

                MethodInfo updateMethod = moveItToolType.GetMethod("OnToolUpdate", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (updateMethod != null)
                {
                    HarmonyLib.Harmony harmony = new HarmonyLib.Harmony(ModInfo.HARMONY_ID);
                    MethodInfo postfixMethod = typeof(DynamicMoveItHook).GetMethod(nameof(OnMoveItToolUpdatePostfix), BindingFlags.Static | BindingFlags.NonPublic);
                    harmony.Patch(updateMethod, postfix: new HarmonyMethod(postfixMethod));
                    Log.Info("Successfully integrated dynamic hook with MoveItTool.OnToolUpdate!");
                }
            }
            catch (Exception ex)
            {
                Log.Exception("DynamicMoveItHook.TryHookMoveIt", ex);
            }
        }

        private static void OnMoveItToolUpdatePostfix(object __instance)
        {
            try
            {
                // Khi Move It đang kích hoạt, tự động Mount các prop nằm quanh con trỏ chuột trong bán kính 64m
                ToolBase tool = __instance as ToolBase;
                if (tool != null && tool.enabled && Camera.main != null && ExtendedPropManager.Instance != null)
                {
                    Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
                    if (Physics.Raycast(ray, out RaycastHit hit, 2000f))
                    {
                        ExtendedPropManager.Instance.MountPropsInArea(hit.point, 64f);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning("OnMoveItToolUpdatePostfix exception: " + ex.Message);
            }
        }
    }
}
