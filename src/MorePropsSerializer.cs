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
        private const uint FORMAT_VERSION = 1;

        public override void OnSaveData()
        {
            base.OnSaveData();

            ExtendedPropManager extMgr = ExtendedPropManager.Instance;
            if (extMgr == null) return;

            try
            {
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
                        }
                    }

                    serializableDataManager.SaveData(DATA_CONTAINER_ID, ms.ToArray());
                    Log.Info($"Successfully saved {createdCount} extended props to custom stream.");
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
                        Log.Error("Invalid magic header in save data. Aborting load.");
                        return;
                    }

                    uint version = reader.ReadUInt32();
                    if (version != FORMAT_VERSION)
                    {
                        Log.Error("Unsupported format version: " + version);
                        return;
                    }

                    // 1. Đọc String Table và phân giải Prefab Index
                    int nameTableSize = reader.ReadInt32();
                    ushort[] resolvedPrefabIndexes = new ushort[nameTableSize];

                    for (int i = 0; i < nameTableSize; i++)
                    {
                        string propName = reader.ReadString();
                        PropInfo info = PrefabCollection<PropInfo>.FindLoaded(propName);
                        if (info != null)
                        {
                            resolvedPrefabIndexes[i] = (ushort)info.m_prefabDataIndex;
                        }
                        else
                        {
                            resolvedPrefabIndexes[i] = 0;
                            Log.Warning($"Missing prop asset '{propName}' from save. Skipping.");
                        }
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

                        ushort infoIndex = stringTableIndex < nameTableSize ? resolvedPrefabIndexes[stringTableIndex] : (ushort)0;

                        if (infoIndex != 0 && extId < ExtendedPropManager.MAX_EXTENDED_PROPS)
                        {
                            ref ExtendedPropInstance extProp = ref extMgr.m_props256k[extId];
                            extProp.m_position = new Vector3(px, py, pz);
                            extProp.m_angle = angle;
                            extProp.m_flags = flags;
                            extProp.m_infoIndex = infoIndex;
                            extProp.m_mountedVanillaId = 0;
                            extProp.m_lastAccessTime = Time.realtimeSinceStartup;

                            extMgr.AddToGrid32(extId, extProp.m_position);
                        }
                    }

                    // 3. Liên kết các prop vanilla đã được game nạp sẵn vào buffer với mảng 256K
                    LinkExistingVanillaProps(extMgr);

                    Log.Info($"Successfully restored {totalProps} extended props.");
                }
            }
            catch (Exception ex)
            {
                Log.Exception("MorePropsSerializer.OnLoadData", ex);
            }
        }

        private static void ImportVanillaPropsIntoExtended(ExtendedPropManager extMgr)
        {
            PropManager pm = Singleton<PropManager>.instance;
            if (pm == null) return;

            int imported = 0;
            for (ushort vId = 1; vId < pm.m_props.m_size; vId++)
            {
                ref PropInstance vProp = ref pm.m_props.m_buffer[vId];
                if ((vProp.m_flags & (ushort)PropInstance.Flags.Created) != 0)
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
                        imported++;
                    }
                }
            }

            Debug.Log($"[MorePropsBETA] Imported {imported} existing vanilla props into 256K storage.");
        }

        private static void LinkExistingVanillaProps(ExtendedPropManager extMgr)
        {
            PropManager pm = Singleton<PropManager>.instance;
            if (pm == null) return;

            for (ushort vId = 1; vId < pm.m_props.m_size; vId++)
            {
                ref PropInstance vProp = ref pm.m_props.m_buffer[vId];
                if ((vProp.m_flags & (ushort)PropInstance.Flags.Created) != 0)
                {
                    // Tìm kiếm prop tương ứng trong m_props256k theo vị trí gần nhất
                    Vector3 pos = vProp.Position;
                    int gx = ExtendedPropManager.WorldToGridX(pos.x);
                    int gz = ExtendedPropManager.WorldToGridZ(pos.z);
                    uint cell = (uint)(gz * ExtendedPropManager.PROPGRID_RESOLUTION + gx);

                    uint extId = extMgr.m_propGrid32[cell];
                    while (extId != 0)
                    {
                        ref ExtendedPropInstance extProp = ref extMgr.m_props256k[extId];
                        if (extProp.IsCreated && !extProp.IsMounted && (extProp.m_position - pos).sqrMagnitude < 0.01f)
                        {
                            extProp.m_mountedVanillaId = vId;
                            extMgr.m_vanillaToExtendedId[vId] = extId;
                            extMgr.m_extendedToVanillaId[extId] = vId;
                            break;
                        }
                        extId = extProp.m_nextGridProp;
                    }
                }
            }
        }
    }
}
