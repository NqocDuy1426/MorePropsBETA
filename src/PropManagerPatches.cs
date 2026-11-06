using System;
using System.Reflection;
using HarmonyLib;
using ColossalFramework;
using ColossalFramework.Math;
using UnityEngine;

namespace MorePropsBETA
{
    internal static class PropManagerPatches
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
                if (ExtendedPropManager.s_emlCoexistenceMode) return true;

                ExtendedPropManager.EnsureInitialized();
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
        public static bool CreatePropPrefix(ref bool __result, ref ushort prop, ref Randomizer randomizer, PropInfo info, Vector3 position, float angle, bool single)
        {
            try
            {
                if (ExtendedPropManager.s_emlCoexistenceMode) return true;

                ExtendedPropManager.EnsureInitialized();
                ExtendedPropManager extMgr = ExtendedPropManager.Instance;
                if (extMgr == null || extMgr.m_isInternalOperation)
                {
                    return true;
                }

                if (extMgr.ExtendedPropCount >= ExtendedPropManager.MAX_EXTENDED_PROPS - 1)
                {
                    prop = 0;
                    __result = false;
                    Log.Warning("Prop limit ceiling reached: 262,144 props. Creation blocked.");
                    return false; // Đã chạm mốc 262.144 prop tối đa
                }

                PropManager pm = Singleton<PropManager>.instance;
                LoadingManager lm = Singleton<LoadingManager>.instance;
                bool isLevelLoading = lm != null && !lm.m_loadingComplete;
                if (!isLevelLoading && pm != null && pm.m_propCount >= ExtendedPropManager.SAFE_VANILLA_THRESHOLD)
                {
                    extMgr.EnsureBufferHeadroom(position, ExtendedPropManager.SAFE_VANILLA_THRESHOLD);
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
        // 6. PATCH RayCast: TỰ ĐỘNG MOUNT CÁC PROP DỌC TIA TRƯỚC KHI ENGINE GỐC KIỂM TRA
        // ==========================================
        [HarmonyPatch(typeof(PropManager), nameof(PropManager.RayCast))]
        [HarmonyPrefix]
        public static bool RayCastPrefix(Segment3 ray, ItemClass.Service service, ItemClass.SubService subService, ItemClass.Layer itemLayers, PropInstance.Flags ignoreFlags, out Vector3 hit, out ushort propIndex)
        {
            hit = Vector3.zero;
            propIndex = 0;

            try
            {
                ExtendedPropManager extMgr = ExtendedPropManager.Instance;
                if (extMgr != null && extMgr.ExtendedPropCount > 0)
                {
                    MountCandidatePropsAlongRay(extMgr, ray);
                }
            }
            catch (Exception ex)
            {
                Log.Exception("RayCastPrefix", ex);
            }

            // Luôn cho phép vanilla PropManager.RayCast thực thi:
            // Vì các prop unmounted dọc tia đã được MountProp đưa vào m_propGrid của game gốc,
            // engine sẽ tự động kiểm tra va chạm với 100% độ chính xác hình học và trả về tọa độ hit chuẩn xác!
            return true;
        }

        private static void MountCandidatePropsAlongRay(ExtendedPropManager extMgr, Segment3 ray)
        {
            Bounds mapBounds = new Bounds(new Vector3(0f, 512f, 0f), new Vector3(17280f, 1152f, 17280f));
            Segment3 clippedRay = ray;
            if (!clippedRay.Clip(mapBounds)) return;

            Vector3 a = clippedRay.a;
            Vector3 b = clippedRay.b;

            int minX = ExtendedPropManager.WorldToGridX(Mathf.Min(a.x, b.x) - 16f);
            int maxX = ExtendedPropManager.WorldToGridX(Mathf.Max(a.x, b.x) + 16f);
            int minZ = ExtendedPropManager.WorldToGridZ(Mathf.Min(a.z, b.z) - 16f);
            int maxZ = ExtendedPropManager.WorldToGridZ(Mathf.Max(a.z, b.z) + 16f);

            // Nếu góc nhìn camera nằm ngang chân trời quét qua quá nhiều ô, lấy điểm giao cắt địa hình làm trọng tâm
            if (maxX - minX > 25 || maxZ - minZ > 25)
            {
                TerrainManager tm = Singleton<TerrainManager>.instance;
                if (tm != null && tm.RayCast(clippedRay, out Vector3 groundHit))
                {
                    extMgr.MountPropsInArea(groundHit, 96f);
                    return;
                }
            }

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
                        ref ExtendedPropInstance extProp = ref extMgr.m_props256k[extId];
                        uint next = extProp.m_nextGridProp;

                        if (extProp.IsCreated && !extProp.IsMounted)
                        {
                            // Kiểm tra khoảng cách hình học từ prop đến tia ray
                            if (clippedRay.DistanceSqr(extProp.m_position) < 900f) // Trong phạm vi 30m quanh tia
                            {
                                extMgr.MountProp(extId);
                            }
                        }

                        extId = next;
                    }
                }
            }
        }

        // ==========================================
        // 7. PATCH ToolController.Update: TỰ ĐỘNG MOUNT TOÀN DIỆN CHO MỌI CÔNG CỤ (BULLDOZER, MOVE IT, PICKER...)
        // ==========================================
        private static Vector3 s_lastMountedCursorPos = new Vector3(-9999f, -9999f, -9999f);

        [HarmonyPatch(typeof(ToolController), "Update")]
        [HarmonyPostfix]
        public static void ToolControllerUpdatePostfix(ToolController __instance)
        {
            try
            {
                if (ExtendedPropManager.s_emlCoexistenceMode) return;
                if (Singleton<LoadingManager>.instance == null || !Singleton<LoadingManager>.instance.m_loadingComplete) return;

                ExtendedPropManager extMgr = ExtendedPropManager.Instance;
                if (extMgr == null || extMgr.ExtendedPropCount == 0) return;

                ToolBase currentTool = __instance != null ? __instance.CurrentTool : null;
                if (currentTool == null || !currentTool.enabled) return;

                // TỐI ƯU HÓA HIỆU NĂNG: Chỉ mount khi công cụ chỉnh sửa thực tế đang hoạt động
                // Bỏ qua DefaultTool (lúc người chơi chỉ ngắm thành phố / điều khiển camera thông thường)
                string toolName = currentTool.GetType().Name;
                if (toolName == "DefaultTool") return;

                Camera cam = Camera.main;
                if (cam == null) return;

                Ray mouseRay = cam.ScreenPointToRay(Input.mousePosition);
                Segment3 segment = new Segment3(mouseRay.origin, mouseRay.origin + mouseRay.direction * 5000f);

                TerrainManager tm = Singleton<TerrainManager>.instance;
                if (tm != null && tm.RayCast(segment, out Vector3 terrainHit))
                {
                    // Chỉ quét mount lại khi chuột di chuyển > 8 mét hoặc tới vị trí mới
                    if ((terrainHit - s_lastMountedCursorPos).sqrMagnitude > 64f)
                    {
                        s_lastMountedCursorPos = terrainHit;
                        extMgr.MountPropsInArea(terrainHit, 96f);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception("ToolControllerUpdatePostfix", ex);
            }
        }
    }

    // ==========================================
    // DYNAMIC MOVE IT HOOK (REFLECTIVE ADAPTER)
    // ==========================================
    internal static class DynamicMoveItHook
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

                HarmonyLib.Harmony harmony = new HarmonyLib.Harmony(ModInfo.HARMONY_ID);

                // 1. Hook MoveItTool.OnToolUpdate để mount prop quanh con trỏ chuột
                MethodInfo updateMethod = moveItToolType.GetMethod("OnToolUpdate", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (updateMethod != null)
                {
                    MethodInfo postfixMethod = typeof(DynamicMoveItHook).GetMethod(nameof(OnMoveItToolUpdatePostfix), BindingFlags.Static | BindingFlags.NonPublic);
                    harmony.Patch(updateMethod, postfix: new HarmonyMethod(postfixMethod));
                    Log.Info("Successfully integrated dynamic hook with MoveItTool.OnToolUpdate!");
                }

                // 2. Hook MoveItTool.GetMarqueeList để mount toàn bộ prop trong khung kéo chữ nhật (Marquee Selection)
                MethodInfo marqueeMethod = moveItToolType.GetMethod("GetMarqueeList", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (marqueeMethod != null)
                {
                    MethodInfo marqueePre = typeof(DynamicMoveItHook).GetMethod(nameof(GetMarqueeListPrefix), BindingFlags.Static | BindingFlags.NonPublic);
                    harmony.Patch(marqueeMethod, prefix: new HarmonyMethod(marqueePre));
                    Log.Info("Successfully integrated dynamic hook with MoveItTool.GetMarqueeList!");
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
                ToolBase tool = __instance as ToolBase;
                if (tool != null && tool.enabled && Camera.main != null && ExtendedPropManager.Instance != null)
                {
                    Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
                    Segment3 segment = new Segment3(ray.origin, ray.origin + ray.direction * 5000f);
                    TerrainManager tm = Singleton<TerrainManager>.instance;
                    if (tm != null && tm.RayCast(segment, out Vector3 terrainHit))
                    {
                        ExtendedPropManager.Instance.MountPropsInArea(terrainHit, 96f);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning("OnMoveItToolUpdatePostfix exception: " + ex.Message);
            }
        }

        private static void GetMarqueeListPrefix(object __instance, Ray mouseRay)
        {
            try
            {
                ExtendedPropManager extMgr = ExtendedPropManager.Instance;
                if (extMgr == null || extMgr.ExtendedPropCount == 0) return;

                Type toolType = __instance.GetType();
                FieldInfo fClick = toolType.GetField("m_clickPositionAbs", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (fClick != null && Singleton<TerrainManager>.instance != null)
                {
                    Vector3 startPos = (Vector3)fClick.GetValue(__instance);
                    Segment3 segment = new Segment3(mouseRay.origin, mouseRay.origin + mouseRay.direction * 5000f);
                    if (Singleton<TerrainManager>.instance.RayCast(segment, out Vector3 currentPos))
                    {
                        float minX = Mathf.Min(startPos.x, currentPos.x) - 16f;
                        float maxX = Mathf.Max(startPos.x, currentPos.x) + 16f;
                        float minZ = Mathf.Min(startPos.z, currentPos.z) - 16f;
                        float maxZ = Mathf.Max(startPos.z, currentPos.z) + 16f;
                        extMgr.MountPropsInRect(minX, minZ, maxX, maxZ);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning("GetMarqueeListPrefix exception: " + ex.Message);
            }
        }
    }
}
