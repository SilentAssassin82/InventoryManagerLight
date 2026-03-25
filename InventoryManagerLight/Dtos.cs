using System;
using System.Collections.Generic;
#if TORCH
using VRage.Game;
#else
// lightweight stub for MyDefinitionId when building outside of Torch/SE
namespace VRage.Game
{
    public struct MyDefinitionId
    {
        public string TypeId;
        public string SubtypeId;

        public static MyDefinitionId FromString(string s)
        {
            return new MyDefinitionId { TypeId = s, SubtypeId = null };
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((TypeId != null ? TypeId.GetHashCode() : 0) * 397) ^ (SubtypeId != null ? SubtypeId.GetHashCode() : 0);
            }
        }

        public override string ToString()
        {
            return TypeId + "/" + SubtypeId;
        }
    }
}
#endif

namespace InventoryManagerLight
{
    // Small value-type snapshot of an inventory entry
    public struct InventorySnapshot
    {
        public long OwnerId; // e.g., entity id owning the inventory
        public VRage.Game.MyDefinitionId ItemDefinitionId; // concrete item def id
        public int Amount;
        public long GridId; // grid that contains this inventory
        // Optional container metadata captured on the game thread
        public string ContainerName;
        public string ContainerCustomData;
    }

    // A single transfer operation planned to run on the game thread
    public struct TransferOp
    {
        public long SourceOwner;
        public long DestinationOwner;
        public VRage.Game.MyDefinitionId ItemDefinitionId;
        public int Amount;
    }

    public enum TransferStatus
    {
        Success,
        Partial,
        Failed
    }

    public struct TransferResult
    {
        public int Moved;
        public TransferStatus Status;
    }

    // Request from applier to replan a remaining transfer or resolve failure
    public class ReplanRequest
    {
        public TransferOp RemainingOp;
        public string Reason;
    }

    // A batch of transfer ops
    public class TransferBatch
    {
        public List<TransferOp> Ops = new List<TransferOp>();
    }
}
