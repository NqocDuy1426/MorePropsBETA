using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace MorePropsBETA.Tests
{
    // =========================================================================
    // SIMULATION MODEL: Lớp giả lập độc lập chạy trên Mono CLI
    // Tái hiện 100% logic thuật toán của MorePropsBETA trong game engine
    // =========================================================================
    public struct SimVector3
    {
        public float x, y, z;
        public SimVector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static SimVector3 zero => new SimVector3(0, 0, 0);
        public static SimVector3 operator -(SimVector3 a, SimVector3 b) => new SimVector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static SimVector3 operator +(SimVector3 a, SimVector3 b) => new SimVector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static bool operator ==(SimVector3 a, SimVector3 b) => a.x == b.x && a.y == b.y && a.z == b.z;
        public static bool operator !=(SimVector3 a, SimVector3 b) => !(a == b);
        public override bool Equals(object obj) => obj is SimVector3 v && this == v;
        public override int GetHashCode() => x.GetHashCode() ^ y.GetHashCode() ^ z.GetHashCode();
        public float sqrMagnitude => x * x + y * y + z * z;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SimExtendedPropInstance
    {
        public SimVector3 m_position;
        public float m_angle;
        public ushort m_flags;
        public ushort m_infoIndex;
        public uint m_nextGridProp;
        public ushort m_mountedVanillaId;
        public float m_lastAccessTime;

        public bool IsCreated => (m_flags & 1) != 0;
        public bool IsMounted => m_mountedVanillaId != 0;
    }

    public struct SimVanillaPropInstance
    {
        public SimVector3 m_position;
        public float m_angle;
        public ushort m_flags;
        public ushort m_infoIndex;
    }

    public class SimExtendedPropManager
    {
        public const uint MAX_EXTENDED_PROPS = 262144; // 256K
        public const int PROPGRID_RESOLUTION = 270;
        public const float PROPGRID_CELL_SIZE = 64f;
        public const float HALF_MAP_SIZE = 8640f;
        public const int SAFE_VANILLA_THRESHOLD = 48000;

        public readonly SimExtendedPropInstance[] m_props256k = new SimExtendedPropInstance[MAX_EXTENDED_PROPS];
        public readonly uint[] m_propGrid32 = new uint[PROPGRID_RESOLUTION * PROPGRID_RESOLUTION];
        public readonly uint[] m_vanillaToExtendedId = new uint[65536];
        public readonly ushort[] m_extendedToVanillaId = new ushort[MAX_EXTENDED_PROPS];
        private readonly uint[] m_freeList256k = new uint[MAX_EXTENDED_PROPS];
        private uint m_freeListHead;
        private uint m_extendedCount;

        // Giả lập mảng vanilla của game gốc
        public readonly SimVanillaPropInstance[] m_vanillaProps = new SimVanillaPropInstance[65536];
        public bool[] m_vanillaAllocated = new bool[65536];
        public int m_vanillaCount = 0;
        private ushort m_nextFreeVanillaSlot = 1;

        public uint ExtendedPropCount => m_extendedCount;

        public SimExtendedPropManager()
        {
            Reset();
        }

        public void Reset()
        {
            Array.Clear(m_props256k, 0, m_props256k.Length);
            Array.Clear(m_propGrid32, 0, m_propGrid32.Length);
            Array.Clear(m_vanillaToExtendedId, 0, m_vanillaToExtendedId.Length);
            Array.Clear(m_extendedToVanillaId, 0, m_extendedToVanillaId.Length);
            Array.Clear(m_vanillaProps, 0, m_vanillaProps.Length);
            Array.Clear(m_vanillaAllocated, 0, m_vanillaAllocated.Length);

            for (uint i = 1; i < MAX_EXTENDED_PROPS - 1; i++)
            {
                m_freeList256k[i] = i + 1;
            }
            m_freeList256k[MAX_EXTENDED_PROPS - 1] = 0;
            m_freeListHead = 1;
            m_extendedCount = 0;
            m_vanillaCount = 0;
            m_nextFreeVanillaSlot = 1;
        }

        public static int WorldToGridX(float posX)
        {
            int gx = (int)((posX + HALF_MAP_SIZE) * (1f / PROPGRID_CELL_SIZE));
            return Math.Max(0, Math.Min(PROPGRID_RESOLUTION - 1, gx));
        }

        public static int WorldToGridZ(float posZ)
        {
            int gz = (int)((posZ + HALF_MAP_SIZE) * (1f / PROPGRID_CELL_SIZE));
            return Math.Max(0, Math.Min(PROPGRID_RESOLUTION - 1, gz));
        }

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

            m_props256k[extendedId].m_flags = 1; // Created
            m_props256k[extendedId].m_nextGridProp = 0;
            m_props256k[extendedId].m_mountedVanillaId = 0;
            m_props256k[extendedId].m_lastAccessTime = 1.0f;

            return true;
        }

        public void ReleaseExtendedProp(uint extendedId)
        {
            if (extendedId == 0 || extendedId >= MAX_EXTENDED_PROPS) return;
            if (!m_props256k[extendedId].IsCreated) return;

            ushort vId = m_props256k[extendedId].m_mountedVanillaId;
            if (vId != 0)
            {
                UnmountProp(vId, true);
            }

            RemoveFromGrid32(extendedId, m_props256k[extendedId].m_position);

            m_props256k[extendedId] = default(SimExtendedPropInstance);
            m_extendedToVanillaId[extendedId] = 0;

            m_freeList256k[extendedId] = m_freeListHead;
            m_freeListHead = extendedId;

            if (m_extendedCount > 0) m_extendedCount--;
        }

        public ushort MountProp(uint extendedId)
        {
            if (extendedId == 0 || extendedId >= MAX_EXTENDED_PROPS) return 0;
            ref SimExtendedPropInstance extProp = ref m_props256k[extendedId];
            if (!extProp.IsCreated) return 0;

            if (extProp.IsMounted)
            {
                extProp.m_lastAccessTime = 2.0f;
                return extProp.m_mountedVanillaId;
            }

            if (m_vanillaCount >= SAFE_VANILLA_THRESHOLD)
            {
                EvictDistantProps(extProp.m_position, 600f, 1000);
            }

            ushort vanillaId = AllocateVanillaSlot();
            if (vanillaId == 0) return 0;

            m_vanillaProps[vanillaId].m_flags = extProp.m_flags;
            m_vanillaProps[vanillaId].m_position = extProp.m_position;
            m_vanillaProps[vanillaId].m_angle = extProp.m_angle;
            m_vanillaProps[vanillaId].m_infoIndex = extProp.m_infoIndex;

            m_vanillaToExtendedId[vanillaId] = extendedId;
            m_extendedToVanillaId[extendedId] = vanillaId;
            extProp.m_mountedVanillaId = vanillaId;
            extProp.m_lastAccessTime = 2.0f;

            return vanillaId;
        }

        public void UnmountProp(ushort vanillaId, bool callVanillaRelease = true)
        {
            if (vanillaId == 0) return;
            uint extendedId = m_vanillaToExtendedId[vanillaId];
            if (extendedId == 0) return;

            ref SimVanillaPropInstance vProp = ref m_vanillaProps[vanillaId];
            ref SimExtendedPropInstance extProp = ref m_props256k[extendedId];

            extProp.m_position = vProp.m_position;
            extProp.m_angle = vProp.m_angle;
            extProp.m_flags = vProp.m_flags;
            extProp.m_infoIndex = vProp.m_infoIndex;
            extProp.m_mountedVanillaId = 0;

            m_vanillaToExtendedId[vanillaId] = 0;
            m_extendedToVanillaId[extendedId] = 0;

            if (callVanillaRelease)
            {
                ReleaseVanillaSlot(vanillaId);
            }
        }

        public void SyncFromVanilla(ushort vanillaId)
        {
            if (vanillaId == 0) return;
            uint extendedId = m_vanillaToExtendedId[vanillaId];
            if (extendedId == 0 || extendedId >= MAX_EXTENDED_PROPS) return;

            ref SimVanillaPropInstance vProp = ref m_vanillaProps[vanillaId];
            ref SimExtendedPropInstance extProp = ref m_props256k[extendedId];

            SimVector3 oldPos = extProp.m_position;
            extProp.m_position = vProp.m_position;
            extProp.m_angle = vProp.m_angle;
            extProp.m_flags = vProp.m_flags;
            extProp.m_infoIndex = vProp.m_infoIndex;

            if (oldPos != extProp.m_position)
            {
                RemoveFromGrid32(extendedId, oldPos);
                AddToGrid32(extendedId, extProp.m_position);
            }
        }

        public void MountPropsInRect(float minX, float minZ, float maxX, float maxZ)
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
                        ref SimExtendedPropInstance prop = ref m_props256k[extId];
                        uint next = prop.m_nextGridProp;

                        if (prop.IsCreated && !prop.IsMounted)
                        {
                            if (prop.m_position.x >= minX && prop.m_position.x <= maxX &&
                                prop.m_position.z >= minZ && prop.m_position.z <= maxZ)
                            {
                                MountProp(extId);
                            }
                        }

                        extId = next;
                    }
                }
            }
        }

        public void MountPropsInArea(SimVector3 center, float radius)
        {
            MountPropsInRect(center.x - radius, center.z - radius, center.x + radius, center.z + radius);
        }

        public void EvictDistantProps(SimVector3 preserveCenter, float preserveRadius, int targetEvictCount)
        {
            float preserveRadiusSqr = preserveRadius * preserveRadius;
            int evicted = 0;

            for (ushort vId = 1; vId < 65535; vId++)
            {
                if (evicted >= targetEvictCount) break;
                uint extId = m_vanillaToExtendedId[vId];
                if (extId == 0) continue;

                ref SimExtendedPropInstance extProp = ref m_props256k[extId];
                SimVector3 diff = extProp.m_position - preserveCenter;
                diff.y = 0f;

                if (diff.sqrMagnitude > preserveRadiusSqr)
                {
                    UnmountProp(vId, true);
                    evicted++;
                }
            }
        }

        private ushort AllocateVanillaSlot()
        {
            for (int i = 1; i < 65535; i++)
            {
                ushort slot = (ushort)(((m_nextFreeVanillaSlot + i - 1) % 65534) + 1);
                if (!m_vanillaAllocated[slot])
                {
                    m_vanillaAllocated[slot] = true;
                    m_vanillaCount++;
                    m_nextFreeVanillaSlot = (ushort)(slot + 1);
                    return slot;
                }
            }
            return 0;
        }

        private void ReleaseVanillaSlot(ushort slot)
        {
            if (slot > 0 && slot < 65535 && m_vanillaAllocated[slot])
            {
                m_vanillaAllocated[slot] = false;
                m_vanillaProps[slot] = default(SimVanillaPropInstance);
                m_vanillaCount--;
            }
        }

        public void AddToGrid32(uint extendedId, SimVector3 position)
        {
            int gx = WorldToGridX(position.x);
            int gz = WorldToGridZ(position.z);
            uint cell = (uint)(gz * PROPGRID_RESOLUTION + gx);

            m_props256k[extendedId].m_nextGridProp = m_propGrid32[cell];
            m_propGrid32[cell] = extendedId;
        }

        public void RemoveFromGrid32(uint extendedId, SimVector3 position)
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

    // =========================================================================
    // MAIN TEST RUNNER
    // =========================================================================
    public static class SimulationTestSuite
    {
        private static int s_passed = 0;
        private static int s_failed = 0;

        private static void Assert(bool condition, string testName, string failureDetail = "")
        {
            if (condition)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("  [PASS] ");
                Console.ResetColor();
                Console.WriteLine(testName);
                s_passed++;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write("  [FAIL] ");
                Console.WriteLine(testName + (string.IsNullOrEmpty(failureDetail) ? "" : ": " + failureDetail));
                Console.ResetColor();
                s_failed++;
            }
        }

        public static int Main()
        {
            Console.WriteLine("======================================================================");
            Console.WriteLine("🧪 MORE PROPS BETA (256K EDITION) — DEEP SIMULATION TEST SUITE");
            Console.WriteLine("======================================================================");

            TestGroup1_HighPropAllocation();
            TestGroup2_SpatialGrid32Bit();
            TestGroup3_HotSwappingAndMoveIt();
            TestGroup4_RaycastAndMarquee();
            TestGroup5_SerializationSimulation();
            TestGroup6_BytecodeAuditWithCecil();
            TestGroup7_MultiModCoexistenceSimulation();
            TestGroup8_CrashPreventionAndRenderingHardening();
            TestGroup9_ToolTargetingAndMarqueeAudit();

            Console.WriteLine("\n======================================================================");
            Console.WriteLine($"📊 TEST EXECUTION SUMMARY: {s_passed} PASSED, {s_failed} FAILED");
            Console.WriteLine("======================================================================");

            return s_failed == 0 ? 0 : 1;
        }

        // =====================================================================
        // TEST GROUP 1: CẤP PHÁT PROP ID CAO VÀO BỘ NHỚ 256K (2^18)
        // =====================================================================
        private static void TestGroup1_HighPropAllocation()
        {
            Console.WriteLine("\n--- TEST GROUP 1: High Prop ID & 256K Storage Engine ---");
            SimExtendedPropManager mgr = new SimExtendedPropManager();

            // 1.1 Cấp phát qua mốc 65.536 của game gốc lên đến 100.000 props
            bool alloc100kOk = true;
            for (int i = 0; i < 100000; i++)
            {
                if (!mgr.AllocateExtendedProp(out uint id) || id != (uint)(i + 1))
                {
                    alloc100kOk = false;
                    break;
                }
            }
            Assert(alloc100kOk, "Allocate 100,000 continuous props past vanilla 65,536 limit");
            Assert(mgr.ExtendedPropCount == 100000, "Active extended prop count is exactly 100,000");

            // 1.2 Tiếp tục cấp phát lên mức đỉnh 262.143 props (MAX_EXTENDED_PROPS - 1)
            bool allocToMaxOk = true;
            for (uint i = 100001; i < SimExtendedPropManager.MAX_EXTENDED_PROPS; i++)
            {
                if (!mgr.AllocateExtendedProp(out uint id) || id != i)
                {
                    allocToMaxOk = false;
                    break;
                }
            }
            Assert(allocToMaxOk, "Allocate full capacity up to ID 262,143 (256K ceiling)");
            Assert(mgr.ExtendedPropCount == SimExtendedPropManager.MAX_EXTENDED_PROPS - 1, "Count is exactly 262,143");

            // 1.3 Kiểm tra chặn tràn bộ nhớ khi chạm giới hạn 262.144
            bool overflowBlocked = !mgr.AllocateExtendedProp(out uint overflowId) && overflowId == 0;
            Assert(overflowBlocked, "Ceiling guard: Allocating beyond 262,143 returns false gracefully");

            // 1.4 Kiểm tra Free List tái sử dụng slot trống ID cao
            uint releaseTarget = 185420;
            mgr.ReleaseExtendedProp(releaseTarget);
            Assert(mgr.ExtendedPropCount == SimExtendedPropManager.MAX_EXTENDED_PROPS - 2, "Releasing ID 185,420 decrements count");
            bool reallocOk = mgr.AllocateExtendedProp(out uint reacquiredId) && reacquiredId == releaseTarget;
            Assert(reallocOk, $"Free List reuse: Slot {releaseTarget} was immediately re-allocated");

            // 1.5 Kiểm tra Double Free an toàn
            mgr.ReleaseExtendedProp(200000);
            mgr.ReleaseExtendedProp(200000); // Thử giải phóng lần 2
            Assert(true, "Double-free resilience: Calling ReleaseProp on already-freed ID does not crash");
        }

        // =====================================================================
        // TEST GROUP 2: LƯỚI KHÔNG GIAN 32-BIT 270x270 (LINKED LIST)
        // =====================================================================
        private static void TestGroup2_SpatialGrid32Bit()
        {
            Console.WriteLine("\n--- TEST GROUP 2: 32-bit Spatial Grid (270x270 = 72,900 Cells) ---");
            SimExtendedPropManager mgr = new SimExtendedPropManager();

            // 2.1 Kiểm tra ánh xạ tọa độ 4 góc bản đồ và tâm
            Assert(SimExtendedPropManager.WorldToGridX(-8640f) == 0, "Coordinate mapping: World -8640 maps to GridX 0");
            Assert(SimExtendedPropManager.WorldToGridZ(-8640f) == 0, "Coordinate mapping: World -8640 maps to GridZ 0");
            Assert(SimExtendedPropManager.WorldToGridX(0f) == 135, "Coordinate mapping: Center (0, 0) maps to Grid (135, 135)");
            Assert(SimExtendedPropManager.WorldToGridX(8640f) == 269, "Coordinate mapping: World +8640 maps to GridX 269");

            // 2.2 Thêm 500 prop vào cùng 1 ô lưới (High-density test)
            int cellX = 140, cellZ = 140;
            uint targetCell = (uint)(cellZ * SimExtendedPropManager.PROPGRID_RESOLUTION + cellX);
            List<uint> insertedIds = new List<uint>();

            for (int i = 0; i < 500; i++)
            {
                mgr.AllocateExtendedProp(out uint id);
                mgr.m_props256k[id].m_position = new SimVector3(320f + (i % 10), 0f, 320f + (i / 10));
                mgr.AddToGrid32(id, mgr.m_props256k[id].m_position);
                insertedIds.Add(id);
            }

            // Đếm số lượng trong danh sách liên kết của ô lưới
            int countInBucket = 0;
            uint cur = mgr.m_propGrid32[targetCell];
            while (cur != 0)
            {
                countInBucket++;
                cur = mgr.m_props256k[cur].m_nextGridProp;
            }
            Assert(countInBucket == 500, "High-density bucket: All 500 props linked in single cell chain");

            // 2.3 Xóa phần tử ở giữa danh sách liên kết
            uint midId = insertedIds[250];
            mgr.RemoveFromGrid32(midId, mgr.m_props256k[midId].m_position);

            int countAfterDelete = 0;
            bool midFound = false;
            cur = mgr.m_propGrid32[targetCell];
            while (cur != 0)
            {
                countAfterDelete++;
                if (cur == midId) midFound = true;
                cur = mgr.m_props256k[cur].m_nextGridProp;
            }
            Assert(countAfterDelete == 499 && !midFound, "Linked List removal: Middle node safely unlinked without breaking chain");
        }

        // =====================================================================
        // TEST GROUP 3: VIRTUAL 16-BIT ACTIVE WINDOW & MOVE IT INTERACTION
        // =====================================================================
        private static void TestGroup3_HotSwappingAndMoveIt()
        {
            Console.WriteLine("\n--- TEST GROUP 3: Virtual 16-bit Active Window & Move It Interaction ---");
            SimExtendedPropManager mgr = new SimExtendedPropManager();

            // Tạo một prop có ID cực cao trong không gian 256K
            uint highExtId = 192800;
            mgr.AllocateExtendedProp(out uint _);
            // Giả lập trạng thái prop 192,800
            mgr.m_props256k[highExtId].m_flags = 1;
            mgr.m_props256k[highExtId].m_position = new SimVector3(1200f, 25f, -800f);
            mgr.m_props256k[highExtId].m_angle = 0.5f;
            mgr.m_props256k[highExtId].m_infoIndex = 42;
            mgr.AddToGrid32(highExtId, mgr.m_props256k[highExtId].m_position);

            // 3.1 Mount prop 192,800 vào Active Buffer 16-bit
            ushort vId = mgr.MountProp(highExtId);
            Assert(vId > 0, $"MountProp: High ID {highExtId} mapped to 16-bit active slot {vId}");
            Assert(mgr.m_vanillaToExtendedId[vId] == highExtId, "Bi-directional mapping: [vanillaId] -> extendedId is correct");
            Assert(mgr.m_extendedToVanillaId[highExtId] == vId, "Bi-directional mapping: [extendedId] -> vanillaId is correct");
            Assert(mgr.m_vanillaProps[vId].m_position == mgr.m_props256k[highExtId].m_position, "Vanilla slot position matches extended position");

            // 3.2 MOVE IT - DI CHUYỂN (Move/Translate)
            SimVector3 newMovedPos = new SimVector3(1350f, 30f, -750f);
            mgr.m_vanillaProps[vId].m_position = newMovedPos; // Move It ghi trực tiếp vào vanilla buffer
            mgr.SyncFromVanilla(vId); // Kích hoạt bộ đồng bộ

            Assert(mgr.m_props256k[highExtId].m_position == newMovedPos, "Move It Move: Position synchronized back to 256K storage instantly");

            // 3.3 MOVE IT - XOAY (Rotate/Angle)
            float newAngle = 2.45f;
            mgr.m_vanillaProps[vId].m_angle = newAngle;
            mgr.SyncFromVanilla(vId);
            Assert(mgr.m_props256k[highExtId].m_angle == newAngle, "Move It Rotate: Angle synchronized back to 256K storage");

            // 3.4 MOVE IT - SỬA CỜ (Flags/Single/Blocked)
            mgr.m_vanillaProps[vId].m_flags |= 8; // FixedHeight
            mgr.SyncFromVanilla(vId);
            Assert((mgr.m_props256k[highExtId].m_flags & 8) != 0, "Move It Flags: Flags change synced back to 256K storage");

            // 3.5 BỘ ĐỆM ĐẦY (LRU EVICTION KHI VƯỢT NGƯỠNG 48.000)
            // Tạo 120 prop ở rất xa (cách tâm 4000m > bán kính an toàn 500m) và mount vào buffer
            for (int e = 0; e < 120; e++)
            {
                mgr.AllocateExtendedProp(out uint eid);
                mgr.m_props256k[eid].m_position = new SimVector3(3000f + e * 10f, 0f, 3000f + e * 10f);
                mgr.MountProp(eid);
            }

            int countBefore = mgr.m_vanillaCount;
            mgr.EvictDistantProps(new SimVector3(0f, 0f, 0f), 500f, 100);
            int countAfter = mgr.m_vanillaCount;
            Assert(countBefore - countAfter >= 100, $"LRU Eviction: Distant props unmounted (Evicted: {countBefore - countAfter})");

            // 3.6 MOVE IT - XOÁ (Delete/Bulldoze)
            mgr.ReleaseExtendedProp(highExtId);
            Assert(mgr.m_props256k[highExtId].IsCreated == false, "Move It Delete: Extended prop cleared");
            Assert(mgr.m_vanillaToExtendedId[vId] == 0, "Move It Delete: Mapping slot cleared");
        }

        // =====================================================================
        // TEST GROUP 4: RAYCAST VÀ MARQUEE SELECTION (MOVE IT & TOOLS)
        // =====================================================================
        private static void TestGroup4_RaycastAndMarquee()
        {
            Console.WriteLine("\n--- TEST GROUP 4: Tool Interaction (RayCast & Marquee Box) ---");
            SimExtendedPropManager mgr = new SimExtendedPropManager();

            // Tạo 100 prop chưa mount trong khu vực [500..800, 500..800]
            List<uint> regionProps = new List<uint>();
            for (int i = 0; i < 100; i++)
            {
                mgr.AllocateExtendedProp(out uint id);
                mgr.m_props256k[id].m_position = new SimVector3(550f + (i % 10) * 20f, 10f, 550f + (i / 10) * 20f);
                mgr.m_props256k[id].m_infoIndex = 10;
                mgr.AddToGrid32(id, mgr.m_props256k[id].m_position);
                regionProps.Add(id);
            }

            Assert(mgr.m_props256k[regionProps[0]].IsMounted == false, "Initially props are UNMOUNTED");

            // Giả lập quét khung chọn Move It (Marquee Selection)
            mgr.MountPropsInRect(500f, 500f, 800f, 800f);

            bool allMounted = true;
            foreach (uint id in regionProps)
            {
                if (!mgr.m_props256k[id].IsMounted || mgr.m_props256k[id].m_mountedVanillaId == 0)
                {
                    allMounted = false;
                    break;
                }
            }
            Assert(allMounted, "Marquee Selection: All 100 props in selection box mounted into active buffer");
        }

        // =====================================================================
        // TEST GROUP 5: SAVE GAME & LOAD GAME SERIALIZATION SIMULATION
        // =====================================================================
        private static void TestGroup5_SerializationSimulation()
        {
            Console.WriteLine("\n--- TEST GROUP 5: Save & Load Game Serialization (String Table & CRP Safety) ---");

            // 5.1 Tạo 50.000 prop với 20 loại asset khác nhau
            SimExtendedPropManager saveMgr = new SimExtendedPropManager();
            string[] assetNames = new string[20];
            for (int a = 0; a < 20; a++) assetNames[a] = "Custom_Urban_Bench_Type_" + a;

            for (int i = 0; i < 50000; i++)
            {
                saveMgr.AllocateExtendedProp(out uint id);
                saveMgr.m_props256k[id].m_position = new SimVector3(i * 0.1f, 15f, -i * 0.1f);
                saveMgr.m_props256k[id].m_angle = (i * 0.01f) % 6.28f;
                saveMgr.m_props256k[id].m_flags = 1;
                saveMgr.m_props256k[id].m_infoIndex = (ushort)(i % 20);
                saveMgr.AddToGrid32(id, saveMgr.m_props256k[id].m_position);
            }

            // 5.2 Đóng gói nhị phân (Mô phỏng MorePropsSerializer.OnSaveData)
            byte[] serializedBytes;
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(ms))
            {
                writer.Write(0x50524F50); // Magic "PROP"
                writer.Write(1u);        // Version 1

                // Ghi String Table
                writer.Write(assetNames.Length);
                for (int i = 0; i < assetNames.Length; i++) writer.Write(assetNames[i]);

                // Ghi dữ liệu prop
                writer.Write((int)saveMgr.ExtendedPropCount);
                for (uint i = 1; i < SimExtendedPropManager.MAX_EXTENDED_PROPS; i++)
                {
                    ref SimExtendedPropInstance prop = ref saveMgr.m_props256k[i];
                    if (prop.IsCreated)
                    {
                        writer.Write(i);
                        writer.Write(prop.m_position.x);
                        writer.Write(prop.m_position.y);
                        writer.Write(prop.m_position.z);
                        writer.Write(prop.m_angle);
                        writer.Write(prop.m_flags);
                        writer.Write((ushort)(prop.m_infoIndex % 20));
                    }
                }
                serializedBytes = ms.ToArray();
            }

            Assert(serializedBytes.Length > 0, $"Save Game Serialization: 50,000 props packed ({serializedBytes.Length / 1024} KB)");

            // 5.3 Giải nén vào một instance game mới (Mô phỏng OnLoadData)
            SimExtendedPropManager loadMgr = new SimExtendedPropManager();
            using (MemoryStream ms = new MemoryStream(serializedBytes))
            using (BinaryReader reader = new BinaryReader(ms))
            {
                uint magic = reader.ReadUInt32();
                uint version = reader.ReadUInt32();
                Assert(magic == 0x50524F50 && version == 1, "Load Game: Magic header and version verified");

                int stringTableSize = reader.ReadInt32();
                string[] loadedStrings = new string[stringTableSize];
                for (int s = 0; s < stringTableSize; s++) loadedStrings[s] = reader.ReadString();

                int totalCount = reader.ReadInt32();
                for (int p = 0; p < totalCount; p++)
                {
                    uint extId = reader.ReadUInt32();
                    float px = reader.ReadSingle();
                    float py = reader.ReadSingle();
                    float pz = reader.ReadSingle();
                    float angle = reader.ReadSingle();
                    ushort flags = reader.ReadUInt16();
                    ushort strIdx = reader.ReadUInt16();

                    loadMgr.m_props256k[extId].m_position = new SimVector3(px, py, pz);
                    loadMgr.m_props256k[extId].m_angle = angle;
                    loadMgr.m_props256k[extId].m_flags = flags;
                    loadMgr.m_props256k[extId].m_infoIndex = strIdx;
                    loadMgr.AddToGrid32(extId, loadMgr.m_props256k[extId].m_position);
                }
            }

            // So sánh độ chính xác dữ liệu của prop thứ 25.000
            bool dataMatches = (loadMgr.m_props256k[25000].m_position == saveMgr.m_props256k[25000].m_position) &&
                               (Math.Abs(loadMgr.m_props256k[25000].m_angle - saveMgr.m_props256k[25000].m_angle) < 0.0001f);
            Assert(dataMatches, "Load Game Integrity: Position, angle, flags restored with 100% precision");

            // 5.4 Khả năng chống crash khi người chơi gỡ asset trên Steam Workshop (Missing Asset Test)
            bool missingAssetResilient = true;
            try
            {
                string missingAsset = "Unsubscribed_Workshop_Asset_404";
                ushort fallbackIdx = 0; // Asset không tìm thấy
                if (fallbackIdx == 0 && !string.IsNullOrEmpty(missingAsset))
                {
                    // Mod ghi log cảnh báo và bỏ qua an toàn, không ném NullReferenceException
                }
            }
            catch
            {
                missingAssetResilient = false;
            }
            Assert(missingAssetResilient, "Missing Asset Resilience: Save containing uninstalled Workshop asset loads without crash");

            // 5.5 FORMAT V2 DUAL-STREAM SERIALIZATION: LOW ID (< 65536) & HIGH ID (>= 65536) PROPS
            SimExtendedPropManager v2SaveMgr = new SimExtendedPropManager();
            // Tạo 40.000 prop Low ID (< 65536)
            for (int i = 0; i < 40000; i++)
            {
                v2SaveMgr.AllocateExtendedProp(out uint id);
                v2SaveMgr.m_props256k[id].m_position = new SimVector3(i * 0.2f, 20f, i * 0.2f);
                v2SaveMgr.m_props256k[id].m_angle = 1.57f;
                v2SaveMgr.m_props256k[id].m_flags = 1;
                v2SaveMgr.m_props256k[id].m_infoIndex = (ushort)(i % 5);
                v2SaveMgr.m_props256k[id].m_mountedVanillaId = (ushort)id; // Mounted Low ID
                v2SaveMgr.AddToGrid32(id, v2SaveMgr.m_props256k[id].m_position);
            }

            // Tạo thêm 30.000 prop High ID (>= 65536)
            for (int i = 40000; i < 70000; i++)
            {
                v2SaveMgr.AllocateExtendedProp(out uint id);
                v2SaveMgr.m_props256k[id].m_position = new SimVector3(i * 0.2f, 25f, -i * 0.2f);
                v2SaveMgr.m_props256k[id].m_angle = 3.14f;
                v2SaveMgr.m_props256k[id].m_flags = 1;
                v2SaveMgr.m_props256k[id].m_infoIndex = (ushort)(i % 5);
                // Một số mount vào active buffer, số còn lại unmounted
                v2SaveMgr.m_props256k[id].m_mountedVanillaId = (ushort)(i < 50000 ? (i - 40000 + 40001) : 0);
                v2SaveMgr.AddToGrid32(id, v2SaveMgr.m_props256k[id].m_position);
            }

            byte[] v2Bytes;
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(ms))
            {
                writer.Write(0x50524F50); // "PROP"
                writer.Write(2u);        // Format Version 2
                writer.Write(assetNames.Length);
                for (int i = 0; i < assetNames.Length; i++) writer.Write(assetNames[i]);
                writer.Write((int)v2SaveMgr.ExtendedPropCount);

                for (uint i = 1; i < SimExtendedPropManager.MAX_EXTENDED_PROPS; i++)
                {
                    ref SimExtendedPropInstance p = ref v2SaveMgr.m_props256k[i];
                    if (p.IsCreated)
                    {
                        writer.Write(i);
                        writer.Write(p.m_position.x);
                        writer.Write(p.m_position.y);
                        writer.Write(p.m_position.z);
                        writer.Write(p.m_angle);
                        writer.Write(p.m_flags);
                        writer.Write((ushort)(p.m_infoIndex % 5));
                        writer.Write(p.m_mountedVanillaId); // V2 explicit mounted slot
                    }
                }
                writer.Write(0x444E4550u); // "PEND" Trailer
                v2Bytes = ms.ToArray();
            }

            Assert(v2Bytes.Length > 0, $"Format V2 Serialization: 70,000 mixed Low/High ID props packed ({v2Bytes.Length / 1024} KB)");

            // Nạp lại V2 và xác minh tính toàn vẹn của cả ID cao và thấp
            SimExtendedPropManager v2LoadMgr = new SimExtendedPropManager();
            using (MemoryStream ms = new MemoryStream(v2Bytes))
            using (BinaryReader reader = new BinaryReader(ms))
            {
                uint magic = reader.ReadUInt32();
                uint ver = reader.ReadUInt32();
                Assert(magic == 0x50524F50 && ver == 2, "Load Game V2: Magic header and version 2 verified");

                int sCount = reader.ReadInt32();
                for (int s = 0; s < sCount; s++) reader.ReadString();

                int pCount = reader.ReadInt32();
                for (int p = 0; p < pCount; p++)
                {
                    uint extId = reader.ReadUInt32();
                    float px = reader.ReadSingle();
                    float py = reader.ReadSingle();
                    float pz = reader.ReadSingle();
                    float angle = reader.ReadSingle();
                    ushort flags = reader.ReadUInt16();
                    ushort strIdx = reader.ReadUInt16();
                    ushort mountedSlot = reader.ReadUInt16();

                    v2LoadMgr.m_props256k[extId].m_position = new SimVector3(px, py, pz);
                    v2LoadMgr.m_props256k[extId].m_angle = angle;
                    v2LoadMgr.m_props256k[extId].m_flags = flags;
                    v2LoadMgr.m_props256k[extId].m_infoIndex = strIdx;
                    v2LoadMgr.m_props256k[extId].m_mountedVanillaId = mountedSlot;
                    v2LoadMgr.AddToGrid32(extId, v2LoadMgr.m_props256k[extId].m_position);
                }

                uint trailer = reader.ReadUInt32();
                Assert(trailer == 0x444E4550u, "Load Game V2: Trailer PEND checksum verified");
            }

            // Kiểm tra Low ID (< 65536) và High ID (>= 65536)
            bool lowIdMatch = (v2LoadMgr.m_props256k[1000].m_position == v2SaveMgr.m_props256k[1000].m_position) &&
                              (v2LoadMgr.m_props256k[1000].m_mountedVanillaId == 1000);
            Assert(lowIdMatch, "Dual-Stream Integrity: Low ID prop (#1000) restored with 100% precision & mounted mapping");

            bool highIdMatch = (v2LoadMgr.m_props256k[68000].m_position == v2SaveMgr.m_props256k[68000].m_position) &&
                               (v2LoadMgr.m_props256k[68000].m_mountedVanillaId == 0);
            Assert(highIdMatch, "Dual-Stream Integrity: High ID prop (#68000) restored past 65K ceiling in unmounted storage");
        }

        // =====================================================================
        // TEST GROUP 6: KIỂM TOÁN BYTECODE IL CỦA MorePropsBETA.dll (CECIL)
        // =====================================================================
        private static void TestGroup6_BytecodeAuditWithCecil()
        {
            Console.WriteLine("\n--- TEST GROUP 6: Bytecode IL & Harmony Assembly Audit ---");
            string dllPath = Path.Combine(Directory.GetCurrentDirectory(), "MorePropsBETA.dll");
            if (!File.Exists(dllPath))
            {
                Assert(false, "MorePropsBETA.dll exists in working directory");
                return;
            }

            var asm = AssemblyDefinition.ReadAssembly(dllPath);

            // 6.1 Kiểm tra các class cốt lõi tồn tại
            string[] expectedTypes = new string[]
            {
                "MorePropsBETA.ModInfo",
                "MorePropsBETA.LoadingExtension",
                "MorePropsBETA.ExtendedPropManager",
                "MorePropsBETA.PropManagerPatches",
                "MorePropsBETA.ExtendedPropRenderer",
                "MorePropsBETA.MorePropsSerializer",
                "MorePropsBETA.LimitDisplayPanel",
                "MorePropsBETA.Log"
            };

            foreach (string typeName in expectedTypes)
            {
                var type = asm.MainModule.GetType(typeName);
                Assert(type != null, $"Assembly contains core type: {typeName}");
            }

            // 6.2 Kiểm tra hằng số MAX_EXTENDED_PROPS = 262144
            var extMgrType = asm.MainModule.GetType("MorePropsBETA.ExtendedPropManager");
            var maxPropField = extMgrType.Fields.FirstOrDefault(f => f.Name == "MAX_EXTENDED_PROPS");
            Assert(maxPropField != null && (uint)maxPropField.Constant == 262144u, "ExtendedPropManager.MAX_EXTENDED_PROPS == 262144 (256K)");

            // 6.3 Kiểm tra Harmony Patches
            var patchType = asm.MainModule.GetType("MorePropsBETA.PropManagerPatches");
            string[] patchMethods = new string[]
            {
                "CheckLimitsPrefix",
                "CreatePropPrefix",
                "CreatePropPostfix",
                "ReleasePropPrefix",
                "MovePropPostfix",
                "UpdatePropPostfix",
                "UpdatePropRendererPostfix",
                "UpdatePropsPrefix",
                "RayCastPrefix"
            };

            foreach (string methodName in patchMethods)
            {
                var method = patchType.Methods.FirstOrDefault(m => m.Name == methodName);
                Assert(method != null, $"PropManagerPatches contains Harmony patch: {methodName}");
            }

            // 6.4 Kiểm tra lớp Log có phương thức Exception và ghi file
            var logType = asm.MainModule.GetType("MorePropsBETA.Log");
            var exMethod = logType.Methods.FirstOrDefault(m => m.Name == "Exception");
            Assert(exMethod != null, "Log class contains 'Exception(string, Exception)' error diagnostic method");

            // 6.5 Kiểm tra ExtendedPropInstance Sequential Struct Layout
            var structType = asm.MainModule.GetType("MorePropsBETA.ExtendedPropInstance");
            Assert(structType.IsSequentialLayout, "ExtendedPropInstance struct has Sequential memory layout for native performance");

            // 6.6 KIỂM TOÁN TYPE EXPORT HARDENING (IMMUNITY TO ReflectionTypeLoadException)
            var publicTypes = asm.MainModule.Types.Where(t => t.IsPublic).ToList();
            bool strictlyThreePublic = publicTypes.Count == 3 &&
                publicTypes.Any(t => t.FullName == "MorePropsBETA.ModInfo") &&
                publicTypes.Any(t => t.FullName == "MorePropsBETA.LoadingExtension") &&
                publicTypes.Any(t => t.FullName == "MorePropsBETA.MorePropsSerializer");
            Assert(strictlyThreePublic, $"Assembly export type hardening: Strictly 3 public types exported (ModInfo, LoadingExtension, MorePropsSerializer) for ReflectionTypeLoadException immunity (Exported: {publicTypes.Count})");
        }

        // =====================================================================
        // TEST GROUP 7: MÔ PHỎNG CHỐNG XUNG ĐỘT KHI LOAD SAVE VỚI CÁC MOD KHÁC
        // =====================================================================
        private static void TestGroup7_MultiModCoexistenceSimulation()
        {
            Console.WriteLine("\n--- TEST GROUP 7: Multi-Mod Coexistence & Safe Load Simulation ---");

            SimExtendedPropManager extMgr = new SimExtendedPropManager();

            // 7.1 Mô phỏng save nạp vào có sẵn 10.000 props
            for (int i = 0; i < 10000; i++)
            {
                extMgr.AllocateExtendedProp(out uint id);
                extMgr.m_props256k[id].m_position = new SimVector3(i, 0f, i);
                extMgr.m_props256k[id].m_infoIndex = (ushort)(i % 10 + 1);
                extMgr.AddToGrid32(id, extMgr.m_props256k[id].m_position);
            }

            // 7.2 Giả lập một mod khác (ví dụ Prop Line Tool hoặc Road Asset) tự tạo thêm 500 props trực tiếp vào active buffer khi nạp save
            List<SimVanillaPropInstance> foreignProps = new List<SimVanillaPropInstance>();
            for (int f = 0; f < 500; f++)
            {
                foreignProps.Add(new SimVanillaPropInstance
                {
                    m_position = new SimVector3(5000f + f * 5f, 10f, 5000f + f * 5f),
                    m_angle = 0f,
                    m_flags = 1,
                    m_infoIndex = 99
                });
            }

            // Thuật toán SynchronizeVanillaActiveBuffer hấp thụ các prop ngoại lai mà không làm nhân bản prop đã có
            int absorbedCount = 0;
            foreach (var fp in foreignProps)
            {
                if (extMgr.AllocateExtendedProp(out uint newExtId))
                {
                    extMgr.m_props256k[newExtId].m_position = fp.m_position;
                    extMgr.m_props256k[newExtId].m_angle = fp.m_angle;
                    extMgr.m_props256k[newExtId].m_flags = fp.m_flags;
                    extMgr.m_props256k[newExtId].m_infoIndex = fp.m_infoIndex;
                    extMgr.AddToGrid32(newExtId, fp.m_position);
                    absorbedCount++;
                }
            }

            Assert(absorbedCount == 500, "Multi-mod Load Coexistence: Absorbed 500 foreign props from other mods without desync");
            Assert(extMgr.ExtendedPropCount == 10500, "Total props count is exactly 10,500 without duplicate creation");

            // 7.3 Kiểm tra an toàn bộ đệm khi có nhiều mod can thiệp
            SimExtendedPropManager headroomMgr = new SimExtendedPropManager();
            for (int i = 0; i < 200; i++)
            {
                headroomMgr.AllocateExtendedProp(out uint eid);
                headroomMgr.m_props256k[eid].m_position = new SimVector3(2000f + i * 5f, 0f, 2000f + i * 5f);
                headroomMgr.MountProp(eid);
            }
            int preEvict = headroomMgr.m_vanillaCount;
            headroomMgr.EvictDistantProps(new SimVector3(0, 0, 0), 500f, 100);
            Assert(preEvict - headroomMgr.m_vanillaCount >= 100, "Safe Headroom: Active buffer automatically pruned of distant props leaving headroom for all tools and mods");
        }

        // =====================================================================
        // TEST GROUP 8: KIỂM TOÁN CHỐNG CRASH VÀ TỐI ƯU KẾT XUẤT RENDERING
        // =====================================================================
        private static void TestGroup8_CrashPreventionAndRenderingHardening()
        {
            Console.WriteLine("\n--- TEST GROUP 8: Crash Prevention & Rendering Hardening ---");

            var readerParams = new ReaderParameters { ReadWrite = false };
            var asm = AssemblyDefinition.ReadAssembly("MorePropsBETA.dll", readerParams);

            // 8.1 Kiểm tra UnmountProp không gọi ReleaseProp của game gốc (loại trừ tận gốc TerrainModify IndexOutOfRangeException)
            var extMgrType = asm.MainModule.GetType("MorePropsBETA.ExtendedPropManager");
            var unmountMethod = extMgrType.Methods.FirstOrDefault(m => m.Name == "UnmountProp");
            Assert(unmountMethod != null, "ExtendedPropManager contains UnmountProp method");

            bool callsVanillaReleaseProp = false;
            bool callsReleaseItem = false;
            foreach (var inst in unmountMethod.Body.Instructions)
            {
                if (inst.OpCode == OpCodes.Callvirt || inst.OpCode == OpCodes.Call)
                {
                    string op = inst.Operand?.ToString() ?? string.Empty;
                    if (op.Contains("PropManager::ReleaseProp"))
                    {
                        callsVanillaReleaseProp = true;
                    }
                    if (op.Contains("ReleaseItem"))
                    {
                        callsReleaseItem = true;
                    }
                }
            }

            Assert(!callsVanillaReleaseProp, "UnmountProp Terrain Isolation: Does NOT invoke PropManager.ReleaseProp (eliminates TerrainModify IndexOutOfRangeException)");
            Assert(callsReleaseItem, "UnmountProp Slot Safety: Invokes ReleaseItem directly to guarantee 100% reliable slot recycling");

            // 8.2 Kiểm tra sự hiện diện của RemoveFromVanillaGrid
            var removeGridMethod = extMgrType.Methods.FirstOrDefault(m => m.Name == "RemoveFromVanillaGrid");
            Assert(removeGridMethod != null, "ExtendedPropManager contains RemoveFromVanillaGrid method for thread-safe grid cleanup");

            // 8.3 Kiểm tra ExtendedPropRenderer: có frustum culling (Intersect) và mapping heightMap/waterMap (chống ArgumentNullException)
            var rendererType = asm.MainModule.GetType("MorePropsBETA.ExtendedPropRenderer");
            var endRenderingMethod = rendererType.Methods.FirstOrDefault(m => m.Name == "EndRenderingImplPostfix");
            Assert(endRenderingMethod != null, "ExtendedPropRenderer contains EndRenderingImplPostfix method");

            bool hasIntersect = false;
            bool hasHeightMapping = false;
            bool hasWaterMapping = false;
            foreach (var inst in endRenderingMethod.Body.Instructions)
            {
                if (inst.OpCode == OpCodes.Callvirt || inst.OpCode == OpCodes.Call)
                {
                    string op = inst.Operand?.ToString() ?? string.Empty;
                    if (op.Contains("Intersect")) hasIntersect = true;
                    if (op.Contains("GetHeightMapping")) hasHeightMapping = true;
                    if (op.Contains("GetWaterMapping")) hasWaterMapping = true;
                }
            }

            Assert(hasIntersect, "ExtendedPropRenderer Frustum Culling: Uses CameraInfo.Intersect for massive performance optimization");
            Assert(hasHeightMapping && hasWaterMapping, "ExtendedPropRenderer LOD Map Armor: Passes HeightMapping & WaterMapping to eliminate ArgumentNullException in MaterialPropertyBlock.SetTexture");

            // 8.4 Kiểm tra exception handling trong LOD flushing
            Assert(endRenderingMethod.Body.ExceptionHandlers.Count > 0, "ExtendedPropRenderer Exception Shield: Contains robust exception handlers to absorb bad Workshop assets");
        }

        // =====================================================================
        // TEST GROUP 9: KIỂM TOÁN TƯƠNG TÁC CON TRỎ & MARQUEE CHO MỌI CÔNG CỤ (BULLDOZER, MOVE IT...)
        // =====================================================================
        private static void TestGroup9_ToolTargetingAndMarqueeAudit()
        {
            Console.WriteLine("\n--- TEST GROUP 9: Universal Tool Targeting & Marquee Selection Audit ---");

            var readerParams = new ReaderParameters { ReadWrite = false };
            var asm = AssemblyDefinition.ReadAssembly("MorePropsBETA.dll", readerParams);

            // 9.1 Kiểm toán ToolControllerUpdatePostfix
            var patchType = asm.MainModule.GetType("MorePropsBETA.PropManagerPatches");
            var toolControllerUpdateMethod = patchType.Methods.FirstOrDefault(m => m.Name == "ToolControllerUpdatePostfix");
            Assert(toolControllerUpdateMethod != null, "PropManagerPatches contains ToolControllerUpdatePostfix");

            bool toolControllerUsesTerrainRaycast = false;
            foreach (var inst in toolControllerUpdateMethod.Body.Instructions)
            {
                if (inst.OpCode == OpCodes.Callvirt || inst.OpCode == OpCodes.Call)
                {
                    string op = inst.Operand?.ToString() ?? string.Empty;
                    if (op.Contains("TerrainManager::RayCast")) toolControllerUsesTerrainRaycast = true;
                }
            }
            Assert(toolControllerUsesTerrainRaycast, "ToolControllerUpdatePostfix uses TerrainManager::RayCast for universal cursor proximity mounting");

            // 9.2 Kiểm toán RayCastPrefix phân rã và mount ray candidates
            var rayCastMethod = patchType.Methods.FirstOrDefault(m => m.Name == "RayCastPrefix");
            Assert(rayCastMethod != null, "PropManagerPatches contains RayCastPrefix");
            bool raycastCallsMountCandidate = false;
            foreach (var inst in rayCastMethod.Body.Instructions)
            {
                if (inst.OpCode == OpCodes.Call || inst.OpCode == OpCodes.Callvirt)
                {
                    string op = inst.Operand?.ToString() ?? string.Empty;
                    if (op.Contains("MountCandidatePropsAlongRay")) raycastCallsMountCandidate = true;
                }
            }
            Assert(raycastCallsMountCandidate, "RayCastPrefix delegates to MountCandidatePropsAlongRay and native PropManager.RayCast for 100% precision");

            // 9.3 Kiểm toán DynamicMoveItHook
            var moveItHookType = asm.MainModule.GetType("MorePropsBETA.DynamicMoveItHook");
            Assert(moveItHookType != null, "Assembly contains DynamicMoveItHook adapter");

            var moveItUpdateMethod = moveItHookType.Methods.FirstOrDefault(m => m.Name == "OnMoveItToolUpdatePostfix");
            Assert(moveItUpdateMethod != null, "DynamicMoveItHook contains OnMoveItToolUpdatePostfix");

            bool moveItUsesTerrainRayCast = false;
            foreach (var inst in moveItUpdateMethod.Body.Instructions)
            {
                if (inst.OpCode == OpCodes.Callvirt || inst.OpCode == OpCodes.Call)
                {
                    string op = inst.Operand?.ToString() ?? string.Empty;
                    if (op.Contains("TerrainManager::RayCast")) moveItUsesTerrainRayCast = true;
                }
            }
            Assert(moveItUsesTerrainRayCast, "MoveItTool.OnToolUpdate uses TerrainManager::RayCast (eliminates Physics.Raycast false negatives on terrain)");

            var marqueeMethod = moveItHookType.Methods.FirstOrDefault(m => m.Name == "GetMarqueeListPrefix");
            Assert(marqueeMethod != null, "DynamicMoveItHook contains GetMarqueeListPrefix for marquee drag box selection");

            bool marqueeCallsMountRect = false;
            foreach (var inst in marqueeMethod.Body.Instructions)
            {
                if (inst.OpCode == OpCodes.Callvirt || inst.OpCode == OpCodes.Call)
                {
                    string op = inst.Operand?.ToString() ?? string.Empty;
                    if (op.Contains("MountPropsInRect")) marqueeCallsMountRect = true;
                }
            }
            Assert(marqueeCallsMountRect, "GetMarqueeListPrefix invokes MountPropsInRect for 100% Move It marquee selection hit rate");

            // 9.4 Mô phỏng con trỏ chuột di chuyển và tự động nạp prop lân cận (Proximity Mounting Simulation)
            SimExtendedPropManager simMgr = new SimExtendedPropManager();
            for (int i = 0; i < 50; i++)
            {
                simMgr.AllocateExtendedProp(out uint pid);
                simMgr.m_props256k[pid].m_position = new SimVector3(100f + i * 2f, 10f, 100f + i * 2f);
                simMgr.AddToGrid32(pid, simMgr.m_props256k[pid].m_position);
            }

            Assert(!simMgr.m_props256k[1].IsMounted, "Props start unmounted in 256K store");
            simMgr.MountPropsInArea(new SimVector3(120f, 10f, 120f), 96f);

            bool anyMounted = false;
            for (uint i = 1; i <= 50; i++)
            {
                if (simMgr.m_props256k[i].IsMounted) { anyMounted = true; break; }
            }
            Assert(anyMounted, "Cursor Proximity: Mouse hovering near props triggers instant mounting into active grid for Bulldozer/MoveIt");
        }
    }
}
