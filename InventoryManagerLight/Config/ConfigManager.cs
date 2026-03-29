using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Serialization;

namespace InventoryManagerLight
{
    // Handles loading and saving iml-config.xml next to the plugin DLL.
    // On first run a default file is written so admins can see all available settings.
    // Call Load(config) to re-apply the file to the existing RuntimeConfig at any time (!iml reload).
    public class ConfigManager
    {
        private static readonly XmlSerializer _serializer = new XmlSerializer(typeof(ImlConfigData));

        private readonly ILogger _logger;
        public string FilePath { get; }

        public ConfigManager(string pluginDir, ILogger logger = null)
        {
            FilePath = Path.Combine(pluginDir, "iml-config.xml");
            _logger = logger;
        }

        // Load from file into an existing RuntimeConfig. Creates a default file if none exists.
        public void LoadOrCreate(RuntimeConfig config)
        {
            if (!File.Exists(FilePath))
            {
                Save(config);
                _logger?.Info($"IML: No config file found — created default at {FilePath}");
                return;
            }
            Load(config);
        }

        // Re-read the file and apply to the existing RuntimeConfig (used by !iml reload).
        public void Load(RuntimeConfig config)
        {
            try
            {
                ImlConfigData data;
                using (var reader = XmlReader.Create(FilePath))
                    data = (ImlConfigData)_serializer.Deserialize(reader);
                ApplyToConfig(data, config);
                _logger?.Info($"IML: Config loaded from {FilePath}");
            }
            catch (Exception ex)
            {
                _logger?.Warn($"IML: Failed to load config from {FilePath}: {ex.Message} — keeping current values");
            }
        }

        // Write current RuntimeConfig values to the file (called on first run to produce a default).
        public void Save(RuntimeConfig config)
        {
            try
            {
                var data = FromConfig(config);
                var settings = new XmlWriterSettings { Indent = true, IndentChars = "  " };
                using (var writer = XmlWriter.Create(FilePath, settings))
                    _serializer.Serialize(writer, data);
            }
            catch (Exception ex)
            {
                _logger?.Warn($"IML: Failed to save config to {FilePath}: {ex.Message}");
            }
        }

        private static ImlConfigData FromConfig(RuntimeConfig c)
        {
            var d = new ImlConfigData
            {
                AutoSortIntervalTicks       = c.AutoSortIntervalTicks,
                LcdUpdateIntervalTicks      = c.LcdUpdateIntervalTicks,
                DrainProductionOutputs      = c.DrainProductionOutputs,
                AssemblerScanIntervalTicks  = c.AssemblerScanIntervalTicks,
                RestrictToConveyorConnectedGrids = c.RestrictToConveyorConnectedGrids,
                TransfersPerTick            = c.TransfersPerTick,
                MsBudgetPerTick             = c.MsBudgetPerTick,
                MaxSortMs                   = c.MaxSortMs,
            };
            foreach (var kv in c.AssemblerThresholds)
                d.AssemblerThresholds.Add(new ImlConfigEntry { Key = kv.Key, Value = kv.Value });
            foreach (var kv in c.MinStockThresholds)
                d.MinStockThresholds.Add(new ImlConfigEntry { Key = kv.Key, Value = kv.Value });
            foreach (var kv in c.CustomCategories)
                d.CustomCategories.Add(new CustomCategoryData { Name = kv.Key, Subtypes = new List<string>(kv.Value) });
            return d;
        }

        private static void ApplyToConfig(ImlConfigData d, RuntimeConfig c)
        {
            c.AutoSortIntervalTicks      = d.AutoSortIntervalTicks;
            c.LcdUpdateIntervalTicks     = d.LcdUpdateIntervalTicks;
            c.DrainProductionOutputs     = d.DrainProductionOutputs;
            c.AssemblerScanIntervalTicks = d.AssemblerScanIntervalTicks;
            c.RestrictToConveyorConnectedGrids = d.RestrictToConveyorConnectedGrids;
            c.TransfersPerTick           = d.TransfersPerTick;
            c.MsBudgetPerTick            = d.MsBudgetPerTick;
            c.MaxSortMs                  = d.MaxSortMs;

            c.AssemblerThresholds.Clear();
            foreach (var e in d.AssemblerThresholds)
                if (!string.IsNullOrWhiteSpace(e.Key))
                    c.AssemblerThresholds[e.Key] = e.Value;

            c.MinStockThresholds.Clear();
            foreach (var e in d.MinStockThresholds)
                if (!string.IsNullOrWhiteSpace(e.Key))
                    c.MinStockThresholds[e.Key] = e.Value;

            c.CustomCategories.Clear();
            foreach (var cat in d.CustomCategories)
            {
                if (string.IsNullOrWhiteSpace(cat.Name) || cat.Subtypes == null) continue;
                var subtypes = cat.Subtypes.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
                if (subtypes.Count > 0)
                    c.CustomCategories[cat.Name.Trim()] = subtypes;
            }
        }
    }

    // XML-serialisable representation of the user-facing subset of RuntimeConfig.
    [XmlRoot("ImlConfig")]
    public class ImlConfigData
    {
        // Ticks between automatic sort passes (60 ticks ≈ 1 second). Default: 6000 (~2 min).
        public int AutoSortIntervalTicks { get; set; } = 6000;

        // Ticks between LCD panel refreshes. Default: 300 (~5 sec). Set to 0 to disable.
        public int LcdUpdateIntervalTicks { get; set; } = 300;

        // Pull finished items from assembler/refinery output slots into category containers.
        public bool DrainProductionOutputs { get; set; } = true;

        // Ticks between assembler auto-queue scans. Default: 3600 (~60 sec). Set to 0 to disable.
        public int AssemblerScanIntervalTicks { get; set; } = 3600;

        // Only sort within conveyor-connected networks (experimental). Default: false.
        public bool RestrictToConveyorConnectedGrids { get; set; } = false;

        // Max transfer operations applied per game tick. Default: 50.
        public int TransfersPerTick { get; set; } = 50;

        // Max milliseconds spent applying transfers per tick. Default: 2.
        public int MsBudgetPerTick { get; set; } = 2;

        // Max milliseconds for block enumeration during a sort pass. Default: 100.
        public int MaxSortMs { get; set; } = 100;

        // Global minimum stock targets for assembler auto-queuing (fallback when no IML:MIN= tag claims the item).
        // Example: <Item key="SteelPlate" value="500" />
        [XmlArray("AssemblerThresholds")]
        [XmlArrayItem("Item")]
        public List<ImlConfigEntry> AssemblerThresholds { get; set; } = new List<ImlConfigEntry>();

        // Per-category low-stock alert thresholds for LCD panels and !iml status.
        // Example: <Item key="COMPONENTS" value="1000" />
        [XmlArray("MinStockThresholds")]
        [XmlArrayItem("Item")]
        public List<ImlConfigEntry> MinStockThresholds { get; set; } = new List<ImlConfigEntry>();

        // Admin-defined custom categories. Each entry maps a category name to a list of exact
        // SubtypeId strings. Players tag containers with IML:CategoryName like built-in ones.
        // Example:
        //   <Category name="MyModdedStuff">
        //     <Subtype>AdvancedSteelPlate</Subtype>
        //   </Category>
        [XmlArray("CustomCategories")]
        [XmlArrayItem("Category")]
        public List<CustomCategoryData> CustomCategories { get; set; } = new List<CustomCategoryData>();
    }

    // A key/value pair that XmlSerializer can handle (Dictionary<K,V> is not supported natively).
    public class ImlConfigEntry
    {
        [XmlAttribute("key")]   public string Key   { get; set; }
        [XmlAttribute("value")] public int    Value { get; set; }
    }

    // One custom category entry in the XML config.
    public class CustomCategoryData
    {
        [XmlAttribute("name")]
        public string Name { get; set; }

        [XmlElement("Subtype")]
        public List<string> Subtypes { get; set; } = new List<string>();
    }
}
