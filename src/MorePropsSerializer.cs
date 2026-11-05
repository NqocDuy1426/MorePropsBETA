using System;
using System.IO;
using System.Collections.Generic;
using ICities;
using ColossalFramework;
using UnityEngine;

namespace MorePropsBETA
{
    public sealed class MorePropsSerializer : SerializableDataExtensionBase
    {
        public const string DATA_CONTAINER_ID = "MoreProps256K_Container_v1";
        private const uint SERIALIZER_MAGIC = 0x50524F50; // "PROP"
        private const uint FORMAT_VERSION = 2;            // V2: Ghi trực tiếp m_mountedVanillaId và trailer PEND
        private const uint TRAILER_MAGIC = 0x444E4550;    // "PEND"

        public override void OnSaveData()
        {
            base.OnSaveData();

            ExtendedPropManager extMgr = ExtendedPropManager.Instance;
            if (extMgr == null) return;

            try
            {
                // Đồng bộ toàn bộ thông số từ Active Buffer trước khi lưu
                extMgr.SyncAllFromVanilla();

                using (MemoryStream ms = new MemoryStream())
                using (BinaryWriter writer = new BinaryWriter(ms))
                {
                    writer.Write(SERIALIZER_MAGIC);
                    writer.Write(FORMAT_VERSION);

                    // Thu thập String Table để lưu tên string của Prop thay vì lưu infoIndex thô
                    List<string> propNameTable = new List<string>();
                    Dictionary<string, ushort> nameToIndexMap = new Dictionary<string, ushort>();

                    for (uint i = 1; i < ExtendedPropManager.MAX_EXTENDED_PROPS; i++)
                    {
                        if (extMgr.m_props256k[i].IsCreated)
                        {
                            PropInfo info = PrefabCollection<PropInfo>.GetPrefab(extMgr.m_props256k[i].m_infoIndex);
                            string propName = info != null ? info.name : string.Empty;

                            if (!nameToIndexMap.ContainsKey(propName))
                            {
                                nameToIndexMap.Add(propName, (ushort)propNameTable.Count);
                                propNameTable.Add(propName);
                            }
                        }
                    }

                    // 1. Ghi bảng chuỗi (String Table)
                    writer.Write(propNameTable.Count);
                    for (int i = 0; i < propNameTable.Count; i++)
                    {
                        writer.Write(propNameTable[i]);
                    }

                    // 2. Ghi tổng số lượng prop mở rộng
                    int createdCount = 0;
                    for (uint i = 1; i < ExtendedPropManager.MAX_EXTENDED_PROPS; i++)
                    {
                        if (extMgr.m_props256k[i].IsCreated)
                        {
                            createdCount++;
                        }
                    }

                    writer.Write(createdCount);

                    // 3. Ghi dữ liệu từng prop (Format V2: bao gồm m_mountedVanillaId)
                    for (uint i = 1; i < ExtendedPropManager.MAX_EXTENDED_PROPS; i++)
                    {
                        ref ExtendedPropInstance prop = ref extMgr.m_props256k[i];
                        if (prop.IsCreated)
                        {
                            writer.Write(i);
                            writer.Write(prop.m_position.x);
                            writer.Write(prop.m_position.y);
                            writer.Write(prop.m_position.z);
                            writer.Write(prop.m_angle);
                            writer.Write(prop.m_flags);

                            PropInfo info = PrefabCollection<PropInfo>.GetPrefab(prop.m_infoIndex);
                            string name = info != null ? info.name : string.Empty;
                            writer.Write(nameToIndexMap[name]);
                            writer.Write(prop.m_mountedVanillaId); // V2: Khôi phục mapping 100% không cần fuzzy search
                        }
                    }

                    // 4. Trailer checksum kết thúc
                    writer.Write(TRAILER_MAGIC);

                    serializableDataManager.SaveData(DATA_CONTAINER_ID, ms.ToArray());
                    Log.Info($"Successfully saved {createdCount} extended props (Format V{FORMAT_VERSION}) to custom stream.");
                }
            }
            catch (Exception ex)
            {
                Log.Exception("MorePropsSerializer.OnSaveData", ex);
            }
        }

        public override void OnLoadData()
        {
            base.OnLoadData();

            ExtendedPropManager extMgr = ExtendedPropManager.Instance;
            if (extMgr == null)
            {
                ExtendedPropManager.EnsureInitialized();
                extMgr = ExtendedPropManager.Instance;
            }

            extMgr.Reset();

            byte[] data = serializableDataManager.LoadData(DATA_CONTAINER_ID);
            if (data == null || data.Length == 0)
            {
                Log.Info("No extended prop container found in save. Importing existing vanilla props into 256K storage...");
                ImportVanillaPropsIntoExtended(extMgr);
                return;
            }

            try
            {
                using (MemoryStream ms = new MemoryStream(data))
                using (BinaryReader reader = new BinaryReader(ms))
                {
                    uint magic = reader.ReadUInt32();
                    if (magic != SERIALIZER_MAGIC)
                    {
                        Log.Error("Invalid magic header in save data. Falling back to vanilla import.");
                        ImportVanillaPropsIntoExtended(extMgr);
                        return;
                    }

                    uint version = reader.ReadUInt32();
                    if (version != 1 && version != 2)
                    {
                        Log.Error($"Unsupported format version {version}. Falling back to vanilla import.");
                        ImportVanillaPropsIntoExtended(extMgr);
                        return;
                    }

                    // 1. Đọc String Table và phân giải Prefab Index an toàn
                    int nameTableSize = reader.ReadInt32();
                    ushort[] resolvedPrefabIndexes = new ushort[nameTableSize];

                    for (int i = 0; i < nameTableSize; i++)
                    {
                        string propName = reader.ReadString();
                        resolvedPrefabIndexes[i] = ResolvePropPrefabIndex(propName);
                    }

                    // 2. Đọc danh sách Prop
                    int totalProps = reader.ReadInt32();
                    for (int i = 0; i < totalProps; i++)
                    {
                        uint extId = reader.ReadUInt32();
                        float px = reader.ReadSingle();
                        float py = reader.ReadSingle();
                        float pz = reader.ReadSingle();
                        float angle = reader.ReadSingle();
                        ushort flags = reader.ReadUInt16();
                        ushort stringTableIndex = reader.ReadUInt16();
                        ushort mountedVanillaId = (version >= 2) ? reader.ReadUInt16() : (ushort)0;

                        ushort infoIndex = stringTableIndex < nameTableSize ? resolvedPrefabIndexes[stringTableIndex] : (ushort)0;

                        if (infoIndex != 0 && extId > 0 && extId < ExtendedPropManager.MAX_EXTENDED_PROPS)
                        {
                            ref ExtendedPropInstance extProp = ref extMgr.m_props256k[extId];
                            extProp.m_position = new Vector3(px, py, pz);
                            extProp.m_angle = angle;
                            extProp.m_flags = flags;
                            extProp.m_infoIndex = infoIndex;
                            extProp.m_mountedVanillaId = mountedVanillaId;
                            extProp.m_lastAccessTime = Time.realtimeSinceStartup;

                            extMgr.AddToGrid32(extId, extProp.m_position);
                        }
                    }

                    // Kiểm tra trailer nếu là V2
                    if (version >= 2 && ms.Position + 4 <= ms.Length)
                    {
                        uint trailer = reader.ReadUInt32();
                        if (trailer != TRAILER_MAGIC)
                        {
                            Log.Warning("Trailer magic mismatch or corrupted stream. Data loaded gracefully.");
                        }
                    }

                    // 3. Đồng bộ hóa sạch sẽ và an toàn tuyệt đối với Active Buffer của game gốc
                    SynchronizeVanillaActiveBuffer(extMgr);

                    // 4. Tái cấu trúc FreeList và đồng bộ chính xác ExtendedPropCount
                    extMgr.RebuildFreeListAndCount();

                    Log.Info($"Successfully restored {totalProps} extended props from save (V{version}).");
                }
            }
            catch (Exception ex)
            {
                Log.Exception("MorePropsSerializer.OnLoadData", ex);
                ImportVanillaPropsIntoExtended(extMgr);
            }
        }

        /// <summary>
        /// Phân giải Prefab Index an toàn, có fallback cho Workshop asset name variation và chống crash khi gỡ mod/asset.
        /// </summary>
        private static ushort ResolvePropPrefabIndex(string propName)
        {
            if (string.IsNullOrEmpty(propName)) return 0;

            try
            {
                PropInfo info = PrefabCollection<PropInfo>.FindLoaded(propName);
                if (info != null) return (ushort)info.m_prefabDataIndex;

                // Fallback 1: Thử thêm suffix "_Data"
                info = PrefabCollection<PropInfo>.FindLoaded(propName + "_Data");
                if (info != null) return (ushort)info.m_prefabDataIndex;

                // Fallback 2: Thử tách prefix Workshop ID (e.g. "123456789.MyProp_Data" -> "MyProp")
                int dotIdx = propName.IndexOf('.');
                if (dotIdx >= 0 && dotIdx < propName.Length - 1)
                {
                    string stripped = propName.Substring(dotIdx + 1);
                    info = PrefabCollection<PropInfo>.FindLoaded(stripped);
                    if (info != null) return (ushort)info.m_prefabDataIndex;

                    info = PrefabCollection<PropInfo>.FindLoaded(stripped + "_Data");
                    if (info != null) return (ushort)info.m_prefabDataIndex;
                }

                // Fallback 3: Tìm kiếm so sánh không phân biệt hoa thường
                int prefabCount = PrefabCollection<PropInfo>.PrefabCount();
                for (int i = 0; i < prefabCount; i++)
                {
                    PropInfo candidate = PrefabCollection<PropInfo>.GetPrefab((uint)i);
                    if (candidate != null && string.Equals(candidate.name, propName, StringComparison.OrdinalIgnoreCase))
                    {
                        return (ushort)candidate.m_prefabDataIndex;
                    }
                }
            }
            catch
            {
                // Bỏ qua lỗi prefab lookup
            }

            Log.Warning($"Missing prop asset '{propName}' from save. Safely skipped without corruption.");
            return 0;
        }

        /// <summary>
        /// Import toàn bộ prop từ save game gốc vào ExtendedPropManager (dành cho new game hoặc save chưa cài MoreProps).
        /// </summary>
        private static void ImportVanillaPropsIntoExtended(ExtendedPropManager extMgr)
        {
            PropManager pm = Singleton<PropManager>.instance;
            if (pm == null || extMgr == null) return;

            extMgr.m_isInternalOperation = true;
            try
            {
                extMgr.Reset();
                int imported = 0;
                int maxProps = Math.Min((int)pm.m_props.m_size, pm.m_props.m_buffer.Length);

                for (int i = 1; i < maxProps; i++)
                {
                    ushort vId = (ushort)i;
                    ref PropInstance vProp = ref pm.m_props.m_buffer[vId];
                    if ((vProp.m_flags & (ushort)PropInstance.Flags.Created) != 0 && vProp.m_infoIndex != 0)
                    {
                        if (extMgr.AllocateExtendedProp(out uint extId))
                        {
                            ref ExtendedPropInstance extProp = ref extMgr.m_props256k[extId];
                            extProp.m_position = vProp.Position;
                            extProp.m_angle = vProp.Angle;
                            extProp.m_flags = vProp.m_flags;
                            extProp.m_infoIndex = vProp.m_infoIndex;
                            extProp.m_mountedVanillaId = vId;
                            extProp.m_lastAccessTime = Time.realtimeSinceStartup;

                            extMgr.m_vanillaToExtendedId[vId] = extId;
                            extMgr.m_extendedToVanillaId[extId] = vId;

                            extMgr.AddToGrid32(extId, extProp.m_position);

                            if (pm.m_updatedProps != null && (vId >> 6) < pm.m_updatedProps.Length)
                            {
                                pm.m_updatedProps[vId >> 6] |= 1UL << (vId & 63);
                                pm.m_propsUpdated = true;
                            }

                            imported++;
                        }
                    }
                }

                extMgr.RebuildFreeListAndCount();
                Log.Info($"Imported {imported} existing vanilla props into 256K storage.");
            }
            catch (Exception ex)
            {
                Log.Exception("ImportVanillaPropsIntoExtended", ex);
            }
            finally
            {
                extMgr.m_isInternalOperation = false;
            }
        }

        /// <summary>
        /// Đồng bộ hóa hai chiều giữa m_props256k và PropManager.m_props.m_buffer:
        /// 1. Khôi phục liên kết trực tiếp đã lưu (V2 explicit mount mapping).
        /// 2. Khôi phục liên kết đồng nhất Low ID (vId == extId) cho save cũ V1.
        /// 3. Khôi phục liên kết lân cận không gian cho các prop còn lại.
        /// 4. Hấp thụ bất kỳ prop nào do mod khác thêm vào trong quá trình nạp.
        /// 5. Tuyệt đối không tạo bản sao trùng lặp (No Duplicates).
        /// </summary>
        private static void SynchronizeVanillaActiveBuffer(ExtendedPropManager extMgr)
        {
            PropManager pm = Singleton<PropManager>.instance;
            if (pm == null || extMgr == null) return;

            extMgr.m_isInternalOperation = true;
            try
            {
                int maxVanillaProps = Math.Min((int)pm.m_props.m_size, pm.m_props.m_buffer.Length);

                // --- BƯỚC 1: Khôi phục mapping trực tiếp từ V2 ---
                for (uint i = 1; i < ExtendedPropManager.MAX_EXTENDED_PROPS; i++)
                {
                    ref ExtendedPropInstance extProp = ref extMgr.m_props256k[i];
                    if (extProp.IsCreated && extProp.m_mountedVanillaId != 0)
                    {
                        ushort vId = extProp.m_mountedVanillaId;
                        if (vId < maxVanillaProps)
                        {
                            ref PropInstance vProp = ref pm.m_props.m_buffer[vId];
                            if ((vProp.m_flags & (ushort)PropInstance.Flags.Created) != 0 && vProp.m_infoIndex != 0)
                            {
                                extMgr.m_vanillaToExtendedId[vId] = i;
                                extMgr.m_extendedToVanillaId[i] = vId;
                                continue;
                            }
                        }
                        // Nếu slot vanilla đó không còn tồn tại hoặc không hợp lệ, chuyển về trạng thái unmounted an toàn
                        extProp.m_mountedVanillaId = 0;
                    }
                }

                // --- BƯỚC 2: Đồng bộ Low ID trực tiếp (vId == extId) cho các slot vanilla chưa được liên kết ---
                for (int i = 1; i < maxVanillaProps; i++)
                {
                    ushort vId = (ushort)i;
                    if (extMgr.m_vanillaToExtendedId[vId] != 0) continue;

                    ref PropInstance vProp = ref pm.m_props.m_buffer[vId];
                    if ((vProp.m_flags & (ushort)PropInstance.Flags.Created) == 0 || vProp.m_infoIndex == 0) continue;

                    uint extId = (uint)vId;
                    if (extId < ExtendedPropManager.MAX_EXTENDED_PROPS)
                    {
                        ref ExtendedPropInstance extProp = ref extMgr.m_props256k[extId];
                        if (extProp.IsCreated && !extProp.IsMounted && extProp.m_infoIndex == vProp.m_infoIndex)
                        {
                            extProp.m_mountedVanillaId = vId;
                            extMgr.m_vanillaToExtendedId[vId] = extId;
                            extMgr.m_extendedToVanillaId[extId] = vId;
                        }
                    }
                }

                // --- BƯỚC 3: Tìm kiếm lân cận không gian (Spatial Match) cho các slot vanilla còn sót lại ---
                for (int i = 1; i < maxVanillaProps; i++)
                {
                    ushort vId = (ushort)i;
                    if (extMgr.m_vanillaToExtendedId[vId] != 0) continue;

                    ref PropInstance vProp = ref pm.m_props.m_buffer[vId];
                    if ((vProp.m_flags & (ushort)PropInstance.Flags.Created) == 0 || vProp.m_infoIndex == 0) continue;

                    Vector3 pos = vProp.Position;
                    int gx = ExtendedPropManager.WorldToGridX(pos.x);
                    int gz = ExtendedPropManager.WorldToGridZ(pos.z);

                    uint matchedExtId = 0;
                    float bestDistSqr = 0.25f; // Bán kính dung sai 0.5m

                    // Quét ô hiện tại và các ô lân cận để tránh lỗi lệch biên ô lưới
                    for (int dz = -1; dz <= 1 && matchedExtId == 0; dz++)
                    {
                        int cz = Mathf.Clamp(gz + dz, 0, ExtendedPropManager.PROPGRID_RESOLUTION - 1);
                        for (int dx = -1; dx <= 1 && matchedExtId == 0; dx++)
                        {
                            int cx = Mathf.Clamp(gx + dx, 0, ExtendedPropManager.PROPGRID_RESOLUTION - 1);
                            uint cell = (uint)(cz * ExtendedPropManager.PROPGRID_RESOLUTION + cx);

                            uint curExtId = extMgr.m_propGrid32[cell];
                            int walk = 0;
                            while (curExtId != 0)
                            {
                                if (++walk > 65536) break;
                                ref ExtendedPropInstance extProp = ref extMgr.m_props256k[curExtId];
                                if (extProp.IsCreated && !extProp.IsMounted && extProp.m_infoIndex == vProp.m_infoIndex)
                                {
                                    float dSqr = (extProp.m_position - pos).sqrMagnitude;
                                    if (dSqr < bestDistSqr)
                                    {
                                        bestDistSqr = dSqr;
                                        matchedExtId = curExtId;
                                    }
                                }
                                curExtId = extProp.m_nextGridProp;
                            }
                        }
                    }

                    if (matchedExtId != 0)
                    {
                        extMgr.m_props256k[matchedExtId].m_mountedVanillaId = vId;
                        extMgr.m_vanillaToExtendedId[vId] = matchedExtId;
                        extMgr.m_extendedToVanillaId[matchedExtId] = vId;
                    }
                    else
                    {
                        // --- BƯỚC 4: Hấp thụ prop mới từ mod khác (không để sót bất kỳ prop nào) ---
                        if (extMgr.AllocateExtendedProp(out uint newExtId))
                        {
                            ref ExtendedPropInstance newExtProp = ref extMgr.m_props256k[newExtId];
                            newExtProp.m_position = pos;
                            newExtProp.m_angle = vProp.Angle;
                            newExtProp.m_flags = vProp.m_flags;
                            newExtProp.m_infoIndex = vProp.m_infoIndex;
                            newExtProp.m_mountedVanillaId = vId;
                            newExtProp.m_lastAccessTime = Time.realtimeSinceStartup;

                            extMgr.m_vanillaToExtendedId[vId] = newExtId;
                            extMgr.m_extendedToVanillaId[newExtId] = vId;
                            extMgr.AddToGrid32(newExtId, pos);
                        }
                    }
                }

                // Cập nhật bitmask render cho tất cả các prop đã mount
                for (int i = 1; i < maxVanillaProps; i++)
                {
                    if (extMgr.m_vanillaToExtendedId[i] != 0 && pm.m_updatedProps != null && (i >> 6) < pm.m_updatedProps.Length)
                    {
                        pm.m_updatedProps[i >> 6] |= 1UL << (i & 63);
                        pm.m_propsUpdated = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception("SynchronizeVanillaActiveBuffer", ex);
            }
            finally
            {
                extMgr.m_isInternalOperation = false;
            }
        }
    }
}
