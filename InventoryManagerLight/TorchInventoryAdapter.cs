#if TORCH
using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using System.Reflection;
using VRage;
using VRage.Library;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using Ingame = VRage.Game.ModAPI.Ingame;
using System.Diagnostics;

namespace InventoryManagerLight
{
    // Torch/SE adapter implementation. Compiled only when the TORCH symbol is defined
    // and the Space Engineers / Torch assemblies are referenced.
    public class TorchInventoryAdapter : IInventoryAdapter
    {
        private readonly ILogger _logger;

        public TorchInventoryAdapter(ILogger logger = null)
        {
            _logger = logger ?? new DefaultLogger();
        }
        private const int MaxTransferChunkDefault = 1000;
        // Note: This implementation assumes that the integer itemDef used by the planner
        // maps to the hash code of MyItemType.ToString() or another mapping you provide.

        public bool TryGetTotalAmount(long ownerId, VRage.Game.MyDefinitionId itemDef, out int amount)
        {
            amount = 0;
            try
            {
                var ent = MyAPIGateway.Entities.GetEntityById(ownerId) as VRage.ModAPI.IMyEntity;
                if (ent == null) return false;

                var block = ent as Sandbox.ModAPI.IMyTerminalBlock;
                if (block == null) return false;

                for (int i = 0; i < block.InventoryCount; i++)
                {
                    var inv = block.GetInventory(i);
                    if (inv == null) continue;
                    var items = new List<Ingame.MyInventoryItem>();
                    inv.GetItems(items);
                    foreach (var it in items)
                    {
                        try
                        {
                            var id = GetDefinitionIdFromStack(it);
                            if (id.Equals(itemDef))
                            {
                                amount += it.Amount.ToIntSafe();
                            }
                        }
                        catch { }
                    }
                }

                return true;
            }
            catch
            {
                amount = 0;
                return false;
            }
        }

        public bool CanAccept(long destOwnerId, VRage.Game.MyDefinitionId itemDef, int amount)
        {
            try
            {
                var ent = MyAPIGateway.Entities.GetEntityById(destOwnerId) as VRage.ModAPI.IMyEntity;
                if (ent == null) return false;
                var block = ent as Sandbox.ModAPI.IMyTerminalBlock;
                if (block == null) return false;

                for (int i = 0; i < block.InventoryCount; i++)
                {
                    var inv = block.GetInventory(i);
                    if (inv == null) continue;
                    var def = itemDef;
                    try
                    {
                        if (inv.CanItemsBeAdded((VRage.MyFixedPoint)amount, def)) return true;
                    }
                    catch { }
                }

                return false;
            }
            catch { return false; }
        }

        public int Transfer(long sourceOwnerId, long destOwnerId, VRage.Game.MyDefinitionId itemDef, int amount)
        {
            try
            {
                var srcEnt = MyAPIGateway.Entities.GetEntityById(sourceOwnerId) as VRage.ModAPI.IMyEntity;
                var dstEnt = MyAPIGateway.Entities.GetEntityById(destOwnerId) as VRage.ModAPI.IMyEntity;
                if (srcEnt == null || dstEnt == null) return 0;
                var srcBlock = srcEnt as Sandbox.ModAPI.IMyTerminalBlock;
                var dstBlock = dstEnt as Sandbox.ModAPI.IMyTerminalBlock;
                if (srcBlock == null || dstBlock == null) return 0;

                int remaining = amount;
                // Production blocks (assemblers/refineries) have inventory 0 = input queue, 1 = output.
                // Always start from index 1 for production blocks to avoid draining items mid-production.
                int srcInvStart = (srcBlock is Sandbox.ModAPI.IMyProductionBlock && srcBlock.InventoryCount >= 2) ? 1 : 0;
                for (int si = srcInvStart; si < srcBlock.InventoryCount && remaining > 0; si++)
                {
                    var srcInv = srcBlock.GetInventory(si);
                    if (srcInv == null) continue;

                    for (int di = 0; di < dstBlock.InventoryCount && remaining > 0; di++)
                    {
                        var dstInv = dstBlock.GetInventory(di);
                        if (dstInv == null) continue;

                        // Re-fetch items each pass: transferring a full stack removes the slot,
                        // compacting the list and invalidating all higher indices.
                        bool progress = true;
                        while (remaining > 0 && progress)
                        {
                            progress = false;
                            var items = new List<Ingame.MyInventoryItem>();
                            srcInv.GetItems(items);

                            for (int itemIndex = 0; itemIndex < items.Count && remaining > 0; itemIndex++)
                            {
                                var stack = items[itemIndex];
                                var id = GetDefinitionIdFromStack(stack);
                                if (!id.Equals(itemDef)) continue;

                                int stackAmt = stack.Amount.ToIntSafe();
                                if (stackAmt <= 0) continue;

                                int want = Math.Min(Math.Min(stackAmt, remaining), MaxTransferChunkDefault);
                                // Halving retry: TransferItemTo requires the full requested amount to fit.
                                // If the destination is partially full, step down until something moves
                                // or we confirm there is no room at all.
                                int chunk = want;
                                int moved = 0;
                                while (chunk > 0 && moved == 0)
                                {
                                    moved = TryTransferViaReflection(srcInv, dstInv, itemIndex, chunk);
                                    if (moved == 0) chunk /= 2;
                                }
                                // Creative mode: the API enforces block volume limits even though the
                                // game allows manual overfilling via the GUI. Bypass via reflection.
                                if (moved == 0 && MyAPIGateway.Session?.CreativeMode == true)
                                    moved = TryTransferCreativeBypass(srcInv, dstInv, itemIndex, want);
                                if (moved > 0)
                                {
                                    remaining -= moved;
                                    progress = true;
                                    break; // indices invalidated — re-fetch before continuing
                                }
                            }
                        }
                    }
                }

                return amount - remaining;
            }
            catch
            {
                return 0;
            }
        }

        // (Helpers for MyItemType removed — adapter uses MyDefinitionId directly.)

        // Transfer 'want' items at slot index 'itemIndex' from srcInv to dstInv.
        // TransferItemTo(dst, sourceItemIndex, targetItemIndex, stackIfPossible, amount, checkConnection) → bool
        // sourceItemIndex is the 0-based slot position, NOT ItemId.
        private int TryTransferViaReflection(VRage.Game.ModAPI.IMyInventory srcInv, VRage.Game.ModAPI.IMyInventory dstInv, int itemIndex, int want)
        {
            try
            {
                bool success = srcInv.TransferItemTo(dstInv, itemIndex, (int?)null, (bool?)true, (VRage.MyFixedPoint?)((VRage.MyFixedPoint)want), false);
                if (success)
                {
                    _logger.Debug($"Transferred {want} item(s) at slot {itemIndex}");
                    return want;
                }
                _logger.Debug($"TransferItemTo returned false for slot {itemIndex}");
            }
            catch (Exception ex)
            {
                _logger.Error("Transfer exception: " + ex.Message);
            }
            return 0;
        }

        // Cached FieldInfo for the internal m_maxVolume field on MyInventory.
        // The field is looked up once on first use and shared across all adapter instances.
        private static volatile System.Reflection.FieldInfo _inventoryMaxVolumeField;
        private static volatile bool _inventoryMaxVolumeFieldSearched;

        // Creative mode bypass: the ModAPI CanItemsBeAdded volume check rejects transfers to
        // containers that are at API capacity, even though the game lets players overfill them
        // manually in Creative mode. We temporarily null m_maxVolume on the destination so
        // CanItemsBeAdded skips the volume check, perform the transfer, then restore immediately.
        private int TryTransferCreativeBypass(VRage.Game.ModAPI.IMyInventory srcInv, VRage.Game.ModAPI.IMyInventory dstInv, int itemIndex, int want)
        {
            try
            {
                if (!_inventoryMaxVolumeFieldSearched)
                {
                    System.Reflection.FieldInfo found = null;
                    var t = dstInv.GetType();
                    while (t != null && found == null)
                    {
                        found = t.GetField("m_maxVolume",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        t = t.BaseType;
                    }
                    _inventoryMaxVolumeField = found;
                    _inventoryMaxVolumeFieldSearched = true;
                    if (found == null)
                        _logger.Warn("Creative bypass: m_maxVolume field not found — overfill transfers will not work");
                }

                if (_inventoryMaxVolumeField == null) return 0;

                var saved = _inventoryMaxVolumeField.GetValue(dstInv);
                _inventoryMaxVolumeField.SetValue(dstInv, null); // remove volume cap
                bool success = false;
                try
                {
                    success = srcInv.TransferItemTo(dstInv, itemIndex, (int?)null, (bool?)true,
                        (VRage.MyFixedPoint?)((VRage.MyFixedPoint)want), false);
                }
                finally
                {
                    _inventoryMaxVolumeField.SetValue(dstInv, saved); // always restore
                }

                if (success)
                {
                    _logger.Debug($"Creative bypass: force-transferred {want} item(s) at slot {itemIndex}");
                    return want;
                }
            }
            catch (Exception ex)
            {
                _logger.Error("Creative bypass exception: " + ex.Message);
            }
            return 0;
        }

        private VRage.Game.MyDefinitionId GetDefinitionIdFromStack(Ingame.MyInventoryItem stack)
        {
            try
            {
                // MyInventoryItem.Type holds TypeId (e.g. "MyObjectBuilder_Ingot") and SubtypeId (e.g. "Iron").
                // There is no Content property on this struct — that lives on MyObjectBuilder_PhysicalObject.
                VRage.Game.MyDefinitionId def;
                if (VRage.Game.MyDefinitionId.TryParse(stack.Type.TypeId, stack.Type.SubtypeId, out def))
                    return def;
            }
            catch { }
            return default(VRage.Game.MyDefinitionId);
        }
    }
}
#else
using System;
using VRage.Game;

namespace InventoryManagerLight
{
    // Stubbed version when TORCH symbol is not defined.
    public class TorchInventoryAdapter : IInventoryAdapter
    {
        public bool TryGetTotalAmount(long ownerId, MyDefinitionId itemDef, out int amount)
        {
            amount = 0;
            return false;
        }

        public bool CanAccept(long destOwnerId, MyDefinitionId itemDef, int amount)
        {
            return false;
        }

        public int Transfer(long sourceOwnerId, long destOwnerId, MyDefinitionId itemDef, int amount)
        {
            return 0;
        }
    }
}
#endif
