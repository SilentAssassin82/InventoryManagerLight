using System;

namespace InventoryManagerLight
{
    /// <summary>
    /// Adapter abstraction for interacting with the game's inventory API.
    /// Implement this interface in the game environment using IMyInventory, MyInventoryItem, MyItemType, etc.
    /// The scaffold uses this to avoid directly referencing game assemblies in the core logic.
    /// </summary>
    public interface IInventoryAdapter
    {
        // Try to get the total available amount of an item for a given owner (sum across inventories).
        bool TryGetTotalAmount(long ownerId, VRage.Game.MyDefinitionId itemDef, out int amount);

        // Check whether the destination can accept the specified amount of item.
        bool CanAccept(long destOwnerId, VRage.Game.MyDefinitionId itemDef, int amount);

        // Attempt to transfer up to 'amount' items from sourceOwner to destOwner.
        // Returns the number of items actually moved.
        int Transfer(long sourceOwnerId, long destOwnerId, VRage.Game.MyDefinitionId itemDef, int amount);
    }

    /// <summary>
    /// Default in-process adapter used by the scaffold when no real game adapter is provided.
    /// It simulates success for testing. Replace or extend with real game implementation.
    /// </summary>
    public class DefaultInventoryAdapter : IInventoryAdapter
    {
        public bool TryGetTotalAmount(long ownerId, VRage.Game.MyDefinitionId itemDef, out int amount)
        {
            amount = 10000; // simulate large supply
            return true;
        }

        public bool CanAccept(long destOwnerId, VRage.Game.MyDefinitionId itemDef, int amount)
        {
            return true; // always accept in scaffold
        }

        public int Transfer(long sourceOwnerId, long destOwnerId, VRage.Game.MyDefinitionId itemDef, int amount)
        {
            // simulate always moving requested amount
            return amount;
        }
    }
}
