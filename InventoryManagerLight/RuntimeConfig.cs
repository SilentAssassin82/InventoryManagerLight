using System;
using System.Collections.Generic;

namespace InventoryManagerLight
{
    public class RuntimeConfig
    {
        // Snapshot interval in ticks (game ticks)
        public int SnapshotIntervalTicks { get; set; } = 10;

        // Max number of transfer ops to apply per tick
        public int TransfersPerTick { get; set; } = 50;

        // Max milliseconds to spend applying transfers per tick
        public int MsBudgetPerTick { get; set; } = 2;

        // Planner thread sleep between planning runs in ms
        public int PlannerSleepMs { get; set; } = 50;

        // Max chunk size for a single transfer operation (applier may split large moves)
        public int MaxTransferChunk { get; set; } = 1000;

        // Container tag prefix used to mark managed inventories (case-insensitive)
        public string ContainerTagPrefix { get; set; } = "IML:";

        // Mapping from category name to comma-separated list of definition id substrings.
        // Example: { "INGOTS": "Ingot,Stone" }
        public Dictionary<string, string> CategoryMappings { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "INGOTS", "Ingot" },
            { "ORE", "Ore" },
            { "COMPONENTS", "Component" },
            // SE hand tools are PhysicalGunObject subtypes: Welder*Item, AngleGrinder*Item, HandDrill*Item.
            // 'Tool' never appears in those strings, so use the actual subtype prefixes instead.
            { "TOOLS", "Welder,Grinder,Drill" },
            { "AMMO", "Ammo" },
            // GasContainerObject = hydrogen bottles; OxygenContainerObject = oxygen bottles.
            { "BOTTLES", "GasContainerObject,OxygenContainerObject" },
            // SeedItem = plantable seeds/food; ConsumableItem = edibles/medical.
            { "FOOD", "SeedItem,ConsumableItem" },
            // Ranged weapons (pistols, rifles, etc.) — subtypes contain Pistol, Rifle, or AutomaticRifle.
            { "WEAPONS", "Pistol,Rifle,AutomaticRifle" },
        };

        public enum LogLevel
        {
            Error = 0,
            Warn = 1,
            Info = 2,
            Debug = 3
        }

        // Minimum level to log
        public LogLevel LoggingLevel { get; set; } = LogLevel.Info;

        // Weights for planner scoring: higher demand weight favors fulfilling demand, distance weight penalizes farther sources
        // These are simple linear weights used by Planner to compute score = demand*DemandWeight - distance*DistanceWeight
        public double DemandWeight { get; set; } = 1.0;
        public double DistanceWeight { get; set; } = 0.5;

        // Consumer scanner interval (in game ticks). Scanner will run every N ticks. Set to 0 to disable periodic scanning.
        public int ScannerIntervalTicks { get; set; } = 10;

        // Interval in ticks to poll blocks for a SortNow button flag in CustomData (IML:SortNow=...).
        // Set to 0 to disable automatic polling; otherwise plugin will scan every N ticks.
        public int SortScanIntervalTicks { get; set; } = 10;

        // Number of game ticks between automatic sort passes (60 ticks ≈ 1 second).
        // Set to 0 to disable periodic auto-sort and rely on '!iml sortall' or the SortNow button.
        // Default: 6000 (~100 seconds).
        public int AutoSortIntervalTicks { get; set; } = 6000;

        // If true, the output inventories (index 1) of assemblers and refineries are drained into
        // the correct category containers automatically. Input inventories (index 0) are never touched.
        // To exclude a specific production block, add 'IML:NoDrain' to its CustomData or block name.
        // Default: true.
        public bool DrainProductionOutputs { get; set; } = true;

        // Number of game ticks between LCD panel content refreshes (60 ticks ≈ 1 second).
        // Tag an LCD panel with [IML:LCD] (all categories) or [IML:LCD=INGOTS] (single category)
        // in its block name or CustomData to have IML write status to it automatically.
        // Set to 0 to disable LCD updates.
        // Default: 300 (~5 seconds).
        public int LcdUpdateIntervalTicks { get; set; } = 300;

        // Demand decay factor applied each scan. Values multiplied by this factor (0..1). 0.5 halves demand each scan.
        public double DemandDecayFactor { get; set; } = 0.5;

        // If true, planner/scanner will only consider sources on the same grid or connected via conveyors.
        // When enabled, unreachable/unconnected candidate owners are treated as infinite distance.
        public bool RestrictToConveyorConnectedGrids { get; set; } = false;

        // If true, a container will only be considered managed if its container-level group (if present)
        // matches the grid/group context. This helps keep multiple independent loops on the same grid
        // separated. When false (default) container-level groups are optional and fallback to grid group.
        public bool RequireContainerGroupMatch { get; set; } = false;
    }
}
