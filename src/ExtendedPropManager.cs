using System;
using System.Runtime.InteropServices;
using ColossalFramework;
using UnityEngine;

namespace MorePropsBETA
{
    [StructLayout(LayoutKind.Sequential)]
    public struct ExtendedPropInstance
    {
        public Vector3 m_position;
        public float m_angle;
        public ushort m_flags;
        public ushort m_infoIndex;
        public uint m_nextGridProp;       // Con trỏ 32-bit trong m_propGrid32
        public ushort m_mountedVanillaId; // 0 nếu Unmounted, 1..65535 nếu đang mount trong m_props.m_buffer
        public float m_lastAccessTime;    // Timestamp cho thuật toán LRU Eviction

        public bool IsCreated => (m_flags & (ushort)PropInstance.Flags.Created) != 0;
        public bool IsMounted => m_mountedVanillaId != 0;
    }

    public sealed class ExtendedPropManager
    {
        public const uint MAX_EXTENDED_PROPS = 262144; // 256K = 1 << 18
        public const int PROPGRID_RESOLUTION = 270;
        public const float PROPGRID_CELL_SIZE = 64f;   // 17280 / 270 = 64m
        public const float HALF_MAP_SIZE = 8640f;
        public const int SAFE_VANILLA_THRESHOLD = 48000; // Ngưỡng an toàn để bắt đầu Unmount prop ở xa

        private static ExtendedPropManager s_instance;
        public static ExtendedPropManager Instance => s_instance;

        // Bộ nhớ dữ liệu 256K toàn cục
        public readonly ExtendedPropInstance[] m_props256k;
        public readonly uint[] m_propGrid32;

        // Bảng ánh xạ 2 chiều O(1) giữa 16-bit Active Buffer và 256K Extended Storage
        public readonly uint[] m_vanillaToExtendedId;
        public readonly ushort[] m_extendedToVanillaId;

        // Free List quản lý cấp phát O(1) cho 256K
        private readonly uint[] m_freeList256k;
        private uint m_freeListHead;
        private uint m_extendedCount;

        // Cờ nội bộ ngăn chặn đệ quy khi Mount / Unmount gọi API của PropManager
        private int m_internalOperationDepth = 0;
        public bool m_isInternalOperation
        {
            get => m_internalOperationDepth > 0;
            set
            {
                if (value)
                    m_internalOperationDepth++;
                else if (m_internalOperationDepth > 0)
                    m_internalOperationDepth--;
            }
        }

        public uint ExtendedPropCount => m_extendedCount;
        public uint MaxProps => m_props256k != null ? (uint)m_props256k.Length : MAX_EXTENDED_PROPS;
        public uint DynamicItemCount => m_props256k != null ? m_extendedCount + 1u : 0u;

        public static void EnsureInitialized()
        {
            if (s_instance == null)
            {
                s_instance = new ExtendedPropManager();
            }
        }

        private ExtendedPropManager()
        {
            m_props256k = new ExtendedPropInstance[MAX_EXTENDED_PROPS];
            m_propGrid32 = new uint[PROPGRID_RESOLUTION * PROPGRID_RESOLUTION];
            m_vanillaToExtendedId = new uint[65536];
            m_extendedToVanillaId = new ushort[MAX_EXTENDED_PROPS];
            m_freeList256k = new uint[MAX_EXTENDED_PROPS];

            Reset();
        }

        public void Reset()
        {
            Array.Clear(m_props256k, 0, m_props256k.Length);
            Array.Clear(m_propGrid32, 0, m_propGrid32.Length);
            Array.Clear(m_vanillaToExtendedId, 0, m_vanillaToExtendedId.Length);
            Array.Clear(m_extendedToVanillaId, 0, m_extendedToVanillaId.Length);

            // Nối chuỗi Free List từ 1 đến MAX_EXTENDED_PROPS - 1
            for (uint i = 1; i < MAX_EXTENDED_PROPS - 1; i++)
            {
                m_freeList256k[i] = i + 1;
            }
            m_freeList256k[MAX_EXTENDED_PROPS - 1] = 0;
            m_freeListHead = 1;
            m_extendedCount = 0;
            m_internalOperationDepth = 0;
        }

        public static int WorldToGridX(float posX)
        {
            int gx = (int)((posX + HALF_MAP_SIZE) * (1f / PROPGRID_CELL_SIZE));
            return Mathf.Clamp(gx, 0, PROPGRID_RESOLUTION - 1);
        }

        public static int WorldToGridZ(float posZ)
        {
            int gz = (int)((posZ + HALF_MAP_SIZE) * (1f / PROPGRID_CELL_SIZE));
            return Mathf.Clamp(gz, 0, PROPGRID_RESOLUTION - 1);
        }

        // ==========================================
        // CẤP PHÁT VÀ THU HỒI EXTENDED ID (256K)
        // ==========================================
        public bool AllocateExtendedProp(out uint extendedId)
        {
            if (m_freeListHead == 0 || m_extendedCount >= MAX_EXTENDED_PROPS - 1)
            {
                extendedId = 0;
                return false;
            }

            extendedId = m_freeListHead;
            m_freeListHead = m_freeList256k[extendedId];
            m_freeList256k[extendedId] = 0;
            m_extendedCount++;

            m_props256k[extendedId].m_flags = (ushort)PropInstance.Flags.Created;
            m_props256k[extendedId].m_nextGridProp = 0;
            m_props256k[extendedId].m_mountedVanillaId = 0;
            m_props256k[extendedId].m_lastAccessTime = Time.realtimeSinceStartup;

            return true;
        }

        public void ReleaseExtendedProp(uint extendedId)
        {
            if (extendedId == 0 || extendedId >= MAX_EXTENDED_PROPS) return;

            if (!m_props256k[extendedId].IsCreated) return;

            // Nếu prop đang Mount trong m_props gốc, gỡ bỏ trước
            ushort vId = m_props256k[extendedId].m_mountedVanillaId;
            if (vId != 0)
            {
                UnmountProp(vId, true);
            }

            // Gỡ khỏi lưới không gian 32-bit
            RemoveFromGrid32(extendedId, m_props256k[extendedId].m_position);

            // Xóa sạch trạng thái
            m_props256k[extendedId] = default(ExtendedPropInstance);
            m_extendedToVanillaId[extendedId] = 0;

            // Trả lại vào Free List
            m_freeList256k[extendedId] = m_freeListHead;
            m_freeListHead = extendedId;

            if (m_extendedCount > 0)
            {
                m_extendedCount--;
            }
        }

        public void RebuildFreeListAndCount()
        {
            uint count = 0;
            m_freeListHead = 0;
            uint prevFree = 0;

            for (uint i = 1; i < MAX_EXTENDED_PROPS; i++)
            {
                if (m_props256k[i].IsCreated)
                {
                    m_freeList256k[i] = 0;
                    count++;
                }
                else
                {
                    if (m_freeListHead == 0)
                    {
                        m_freeListHead = i;
                    }
                    else
                    {
                        m_freeList256k[prevFree] = i;
                    }
                    prevFree = i;
                    m_freeList256k[i] = 0;
                }
            }

            m_extendedCount = count;
            Log.Info($"Rebuilt ExtendedProp free list: {count} active props, next free ID={m_freeListHead}");
        }

        // ==========================================
        // CƠ CHẾ HOT-SWAPPING (MOUNT / UNMOUNT)
        // ==========================================
        public ushort MountProp(uint extendedId)
        {
            if (extendedId == 0 || extendedId >= MAX_EXTENDED_PROPS) return 0;

            ref ExtendedPropInstance extProp = ref m_props256k[extendedId];
            if (!extProp.IsCreated) return 0;

            if (extProp.IsMounted)
            {
                extProp.m_lastAccessTime = Time.realtimeSinceStartup;
                return extProp.m_mountedVanillaId;
            }

            PropManager pm = Singleton<PropManager>.instance;
            if (pm == null) return 0;

            PropInfo info = PrefabCollection<PropInfo>.GetPrefab(extProp.m_infoIndex);
            if (info == null) return 0;

            // Kiểm tra ngưỡng an toàn của bộ đệm 16-bit
            if (pm.m_propCount >= SAFE_VANILLA_THRESHOLD)
            {
                Vector3 camPos = Camera.main != null ? Camera.main.transform.position : extProp.m_position;
                EvictDistantProps(camPos, 600f, 1000);
            }

            m_isInternalOperation = true;
            ushort vanillaId = 0;
            try
            {
                if (!pm.CreateProp(out vanillaId, ref SimulationManager.instance.m_randomizer, info, extProp.m_position, extProp.m_angle, (extProp.m_flags & (ushort)PropInstance.Flags.Single) != 0))
                {
                    // Nếu buffer vẫn đầy, ép buộc giải phóng prop cũ nhất theo LRU
                    ushort evictedSlot = EvictOldestSingleProp();
                    if (evictedSlot == 0 || !pm.CreateProp(out vanillaId, ref SimulationManager.instance.m_randomizer, info, extProp.m_position, extProp.m_angle, (extProp.m_flags & (ushort)PropInstance.Flags.Single) != 0))
                    {
                        Log.Warning($"MountProp failed to acquire active vanilla slot for extendedId={extendedId}");
                        return 0; // Không thể cấp phát slot active
                    }
                }

                // Ghi đè trạng thái chính xác vào slot của game gốc
                pm.m_props.m_buffer[vanillaId].m_flags = extProp.m_flags;
                pm.m_props.m_buffer[vanillaId].Position = extProp.m_position;
                pm.m_props.m_buffer[vanillaId].Angle = extProp.m_angle;
                pm.m_props.m_buffer[vanillaId].Info = info;

                // Cập nhật quan hệ ánh xạ 2 chiều
                m_vanillaToExtendedId[vanillaId] = extendedId;
                m_extendedToVanillaId[extendedId] = vanillaId;
                extProp.m_mountedVanillaId = vanillaId;
                extProp.m_lastAccessTime = Time.realtimeSinceStartup;

                // Tính toán chính xác độ cao mặt đất (Terrain Height) để tránh prop bay lơ lửng tại Y = 0
                pm.m_props.m_buffer[vanillaId].CalculateProp(vanillaId);

                // Thay vì gọi pm.UpdatePropRenderer gây đệ quy UpdateProps,
                // đánh dấu vào m_updatedProps bitmask để engine tự động chạy cập nhật renderer ở frame tiếp theo
                if (pm.m_updatedProps != null && (vanillaId >> 6) < pm.m_updatedProps.Length)
                {
                    pm.m_updatedProps[vanillaId >> 6] |= 1UL << (vanillaId & 63);
                    pm.m_propsUpdated = true;
                }
            }
            catch (Exception ex)
            {
                Log.Exception($"MountProp(extendedId={extendedId})", ex);
            }
            finally
            {
                m_isInternalOperation = false;
            }

            return vanillaId;
        }

        public void UnmountProp(ushort vanillaId, bool callVanillaRelease = true)
        {
            if (vanillaId == 0) return;

            try
            {
                uint extendedId = m_vanillaToExtendedId[vanillaId];
                if (extendedId == 0) return;

                PropManager pm = Singleton<PropManager>.instance;
                if (pm == null) return;

                ref PropInstance vProp = ref pm.m_props.m_buffer[vanillaId];
                ref ExtendedPropInstance extProp = ref m_props256k[extendedId];

                // Đồng bộ ngược toàn bộ thông số từ Active Buffer về m_props256k trước khi gỡ
                extProp.m_position = vProp.Position;
                extProp.m_angle = vProp.Angle;
                extProp.m_flags = vProp.m_flags;
                extProp.m_infoIndex = vProp.m_infoIndex;
                extProp.m_mountedVanillaId = 0;

                // Xóa ánh xạ 2 chiều
                m_vanillaToExtendedId[vanillaId] = 0;
                m_extendedToVanillaId[extendedId] = 0;

                if (callVanillaRelease)
                {
                    m_isInternalOperation = true;
                    try
                    {
                        pm.ReleaseProp(vanillaId);
                    }
                    finally
                    {
                        m_isInternalOperation = false;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception($"UnmountProp(vanillaId={vanillaId})", ex);
            }
        }

        public void SyncFromVanilla(ushort vanillaId)
        {
            if (vanillaId == 0) return;

            try
            {
                uint extendedId = m_vanillaToExtendedId[vanillaId];
                if (extendedId == 0 || extendedId >= MAX_EXTENDED_PROPS) return;

                PropManager pm = Singleton<PropManager>.instance;
                if (pm == null) return;

                ref PropInstance vProp = ref pm.m_props.m_buffer[vanillaId];
                ref ExtendedPropInstance extProp = ref m_props256k[extendedId];

                Vector3 oldPos = extProp.m_position;
                extProp.m_position = vProp.Position;
                extProp.m_angle = vProp.Angle;
                extProp.m_flags = vProp.m_flags;
                extProp.m_infoIndex = vProp.m_infoIndex;
                extProp.m_lastAccessTime = Time.realtimeSinceStartup;

                // Nếu vị trí thay đổi (ví dụ Move It di chuyển prop), cập nhật lại spatial grid 32-bit
                if (oldPos != extProp.m_position)
                {
                    RemoveFromGrid32(extendedId, oldPos);
                    AddToGrid32(extendedId, extProp.m_position);
                }
            }
            catch (Exception ex)
            {
                Log.Exception($"SyncFromVanilla(vanillaId={vanillaId})", ex);
            }
        }

        public void MountPropsInArea(Vector3 center, float radius)
        {
            m_isInternalOperation = true;
            try
            {
                int minX = WorldToGridX(center.x - radius);
                int maxX = WorldToGridX(center.x + radius);
                int minZ = WorldToGridZ(center.z - radius);
                int maxZ = WorldToGridZ(center.z + radius);
                float radiusSqr = radius * radius;

                for (int z = minZ; z <= maxZ; z++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        uint cell = (uint)(z * PROPGRID_RESOLUTION + x);
                        uint extId = m_propGrid32[cell];

                        while (extId != 0)
                        {
                            ref ExtendedPropInstance prop = ref m_props256k[extId];
                            uint next = prop.m_nextGridProp;

                            if (prop.IsCreated && !prop.IsMounted)
                            {
                                Vector3 diff = prop.m_position - center;
                                diff.y = 0f;
                                if (diff.sqrMagnitude <= radiusSqr)
                                {
                                    MountProp(extId);
                                }
                            }
                            else if (prop.IsMounted)
                            {
                                prop.m_lastAccessTime = Time.realtimeSinceStartup;
                            }

                            extId = next;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception("MountPropsInArea", ex);
            }
            finally
            {
                m_isInternalOperation = false;
            }
        }

        public void MountPropsInRect(float minX, float minZ, float maxX, float maxZ)
        {
            m_isInternalOperation = true;
            try
            {
                int gx0 = WorldToGridX(minX);
                int gx1 = WorldToGridX(maxX);
                int gz0 = WorldToGridZ(minZ);
                int gz1 = WorldToGridZ(maxZ);

                for (int z = gz0; z <= gz1; z++)
                {
                    for (int x = gx0; x <= gx1; x++)
                    {
                        uint cell = (uint)(z * PROPGRID_RESOLUTION + x);
                        uint extId = m_propGrid32[cell];

                        while (extId != 0)
                        {
                            ref ExtendedPropInstance prop = ref m_props256k[extId];
                            uint next = prop.m_nextGridProp;

                            if (prop.IsCreated && !prop.IsMounted)
                            {
                                if (prop.m_position.x >= minX && prop.m_position.x <= maxX &&
                                    prop.m_position.z >= minZ && prop.m_position.z <= maxZ)
                                {
                                    MountProp(extId);
                                }
                            }
                            else if (prop.IsMounted)
                            {
                                prop.m_lastAccessTime = Time.realtimeSinceStartup;
                            }

                            extId = next;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception("MountPropsInRect", ex);
            }
            finally
            {
                m_isInternalOperation = false;
            }
        }

        public void MountPropsInBounds(Bounds bounds)
        {
            MountPropsInRect(bounds.min.x, bounds.min.z, bounds.max.x, bounds.max.z);
        }

        // ==========================================
        // THUẬT TOÁN EVICTION & LƯỚI KHÔNG GIAN 32-BIT
        // ==========================================
        public void EvictDistantProps(Vector3 preserveCenter, float preserveRadius, int targetEvictCount)
        {
            try
            {
                float[] thresholds = new float[] { preserveRadius, preserveRadius * 0.5f, 60f, 5f };
                int evicted = 0;

                for (int t = 0; t < thresholds.Length; t++)
                {
                    if (evicted >= targetEvictCount) break;

                    float radiusSqr = thresholds[t] * thresholds[t];

                    for (int i = 1; i < 65536; i++)
                    {
                        if (evicted >= targetEvictCount) break;

                        ushort vId = (ushort)i;
                        uint extId = m_vanillaToExtendedId[vId];
                        if (extId == 0) continue;

                        ref ExtendedPropInstance extProp = ref m_props256k[extId];
                        Vector3 diff = extProp.m_position - preserveCenter;
                        diff.y = 0f;

                        if (diff.sqrMagnitude > radiusSqr)
                        {
                            UnmountProp(vId, true);
                            evicted++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception("EvictDistantProps", ex);
            }
        }

        public void EnsureBufferHeadroom(Vector3 focusPoint, int targetCount = 48000)
        {
            try
            {
                PropManager pm = Singleton<PropManager>.instance;
                if (pm == null || pm.m_propCount <= targetCount) return;

                int needToEvict = pm.m_propCount - targetCount;
                EvictDistantProps(focusPoint, 600f, needToEvict);

                if (pm.m_propCount > targetCount)
                {
                    EvictDistantProps(focusPoint, 200f, pm.m_propCount - targetCount);
                }
                if (pm.m_propCount > targetCount)
                {
                    EvictDistantProps(focusPoint, 5f, pm.m_propCount - targetCount);
                }
            }
            catch (Exception ex)
            {
                Log.Exception("EnsureBufferHeadroom", ex);
            }
        }

        public void SyncAllFromVanilla()
        {
            try
            {
                for (int i = 1; i < 65536; i++)
                {
                    ushort vId = (ushort)i;
                    if (m_vanillaToExtendedId[vId] != 0)
                    {
                        SyncFromVanilla(vId);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception("SyncAllFromVanilla", ex);
            }
        }

        private ushort EvictOldestSingleProp()
        {
            float oldestTime = float.MaxValue;
            ushort oldestVanillaId = 0;

            for (int i = 1; i < 65536; i++)
            {
                ushort vId = (ushort)i;
                uint extId = m_vanillaToExtendedId[vId];
                if (extId == 0) continue;

                if (m_props256k[extId].m_lastAccessTime < oldestTime)
                {
                    oldestTime = m_props256k[extId].m_lastAccessTime;
                    oldestVanillaId = vId;
                }
            }

            if (oldestVanillaId != 0)
            {
                UnmountProp(oldestVanillaId, true);
            }

            return oldestVanillaId;
        }

        public void AddToGrid32(uint extendedId, Vector3 position)
        {
            int gx = WorldToGridX(position.x);
            int gz = WorldToGridZ(position.z);
            uint cell = (uint)(gz * PROPGRID_RESOLUTION + gx);

            m_props256k[extendedId].m_nextGridProp = m_propGrid32[cell];
            m_propGrid32[cell] = extendedId;
        }

        public void RemoveFromGrid32(uint extendedId, Vector3 position)
        {
            int gx = WorldToGridX(position.x);
            int gz = WorldToGridZ(position.z);
            uint cell = (uint)(gz * PROPGRID_RESOLUTION + gx);

            uint current = m_propGrid32[cell];
            uint prev = 0;

            while (current != 0)
            {
                if (current == extendedId)
                {
                    if (prev == 0)
                    {
                        m_propGrid32[cell] = m_props256k[current].m_nextGridProp;
                    }
                    else
                    {
                        m_props256k[prev].m_nextGridProp = m_props256k[current].m_nextGridProp;
                    }
                    m_props256k[current].m_nextGridProp = 0;
                    return;
                }
                prev = current;
                current = m_props256k[current].m_nextGridProp;
            }
        }
    }
}
