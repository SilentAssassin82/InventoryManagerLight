#if TORCH
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Torch.Commands;

namespace InventoryManagerLight
{
    // Torch console command module. Commands are invoked from the server console or by admins in-game.
    // Usage:
    //   !iml dump              - dump diagnostics to the server log
    //   !iml sort <entityId>   - sort a specific block by its entity id
    //   !iml sortall           - sort all IML-tagged containers
    //   !iml list              - list all IML-tagged containers with entity ids
    //   !iml status            - show per-category container count and item totals
    [Category("iml")]
    public class ImlCommandModule : CommandModule
    {
        private InventoryManagerPlugin Plugin => InventoryManagerPlugin.Instance;

        [Command("dump", "Dumps IML diagnostics to the server log.")]
        public void Dump()
        {
            var p = Plugin;
            if (p?.Manager == null) { Context.Respond("IML: Plugin not ready."); return; }
            p.Manager.DumpState();
            Context.Respond("IML: Diagnostics dumped — check server log.");
        }

        [Command("sort", "Triggers a sort pass for the block with the given entity id.")]
        public void Sort(long blockEntityId)
        {
            var p = Plugin;
            if (p?.Manager == null) { Context.Respond("IML: Plugin not ready."); return; }
            p.Manager.TriggerSortForOwner(blockEntityId);
            Context.Respond($"IML: Sort triggered for entity {blockEntityId}. Use '!iml list' to find entity ids.");
        }

        [Command("sortall", "Triggers a sort pass for all IML-tagged containers.")]
        public void SortAll()
        {
            var p = Plugin;
            if (p?.Manager == null) { Context.Respond("IML: Plugin not ready."); return; }
            int count = p.Manager.TriggerSortAll();
            if (count == -1)
                Context.Respond("IML: Sort aborted — server is under load (block enumeration exceeded time budget). Try again in a moment, or increase MaxSortMs in the plugin config.");
            else if (count > 0)
                Context.Respond($"IML: Sort triggered for {count} managed container(s).");
            else
                Context.Respond("IML: No managed containers found. Tag containers with 'IML:CATEGORY' in the block name or CustomData.");
        }

        [Command("queueall", "Immediately runs the assembler auto-queue scan and reports results.")]
        public void QueueAll()
        {
            var p = Plugin;
            if (p?.Manager == null) { Context.Respond("IML: Plugin not ready."); return; }
            var lines = p.Manager.TriggerAssemblerScan();
            if (lines == null || lines.Count == 0)
            {
                Context.Respond("IML: Assembler scan returned no output. Make sure at least one assembler has IML:MIN= in its CustomData or block name.");
                return;
            }
            var sb = new StringBuilder();
            sb.AppendLine("IML: Assembler scan results:");
            foreach (var line in lines)
                sb.AppendLine(line);
            Context.Respond(sb.ToString().TrimEnd());
        }

        [Command("list", "Lists all IML-tagged containers with their entity ids.")]
        public void List()
        {
            var p = Plugin;
            if (p?.Manager == null) { Context.Respond("IML: Plugin not ready."); return; }
            List<string> lines = p.Manager.GetManagedContainerInfo();
            if (lines == null || lines.Count == 0)
            {
                Context.Respond("IML: No managed containers found. Tag containers with 'IML:CATEGORY' in the block name or CustomData.\nExample name: 'Large Cargo Container [IML:INGOTS]'");
                return;
            }
            var sb = new StringBuilder();
            sb.AppendLine($"IML: {lines.Count} managed container(s) — use entity id with '!iml sort <id>':");
            foreach (var line in lines)
                sb.AppendLine("  " + line);
            Context.Respond(sb.ToString().TrimEnd());
        }
            [Command("status", "Shows a per-category summary of managed containers and item totals.")]
            public void Status()
            {
                var p = Plugin;
                if (p?.Manager == null) { Context.Respond("IML: Plugin not ready."); return; }
                var lines = p.Manager.GetStatusSummary();
                if (lines == null || lines.Count == 0)
                {
                    Context.Respond("IML: No managed containers found. Tag containers with 'IML:CATEGORY' in the block name or CustomData.");
                    return;
                }
                var sb = new StringBuilder();
                sb.AppendLine("IML: Inventory status by category:");
                foreach (var line in lines)
                    sb.AppendLine("  " + line);
                Context.Respond(sb.ToString().TrimEnd());
            }

        [Command("stockdump", "Shows every inventory slot that contains the given item subtype.")]
        public void StockDump(string subtype)
        {
            var p = Plugin;
            if (p?.Manager == null) { Context.Respond("IML: Plugin not ready."); return; }
            if (string.IsNullOrWhiteSpace(subtype))
            {
                Context.Respond("IML: Usage: !iml stockdump <SubtypeId>   e.g. !iml stockdump SteelPlate");
                return;
            }
            var lines = p.Manager.StockDump(subtype);
            var sb = new StringBuilder();
            foreach (var line in lines)
                sb.AppendLine(line);
            Context.Respond(sb.ToString().TrimEnd());
        }

        [Command("reload", "Reloads iml-config.xml without restarting the server.")]
        public void Reload()
        {
            var p = Plugin;
            if (p?.Manager == null || p.ConfigManager == null) { Context.Respond("IML: Plugin not ready."); return; }
            p.ConfigManager.Load(p.Config);
            p.Manager.RebuildCategoryResolver();
            Context.Respond($"IML: Config reloaded from {p.ConfigManager.FilePath}");
        }

        [Command("refreshdefs", "Scans all loaded game definitions and logs any item subtypes not covered by a category.")]
        public void RefreshDefs()
        {
            var p = Plugin;
            if (p?.Manager == null) { Context.Respond("IML: Plugin not ready."); return; }
            var lines = p.Manager.GetUnknownSubtypes();
            if (lines == null || lines.Count == 0) { Context.Respond("IML: No output from definition scan."); return; }
            var sb = new StringBuilder();
            foreach (var line in lines)
                sb.AppendLine(line);
            Context.Respond(sb.ToString().TrimEnd());
        }

        [Command("tagall", "Adds an IML tag to all inventory containers on a grid. Usage: !iml tagall <gridId|name> <category>")]
        public void TagAll(string gridIdOrName, string category)
        {
            var p = Plugin;
            if (p?.Manager == null) { Context.Respond("IML: Plugin not ready."); return; }
            if (string.IsNullOrWhiteSpace(gridIdOrName) || string.IsNullOrWhiteSpace(category))
            {
                Context.Respond("IML: Usage: !iml tagall <gridId|name> <category>   e.g. !iml tagall 12345 COMPONENTS");
                return;
            }
            var lines = p.Manager.BulkTagGrid(gridIdOrName, category);
            var sb = new StringBuilder();
            foreach (var line in lines) sb.AppendLine(line);
            Context.Respond(sb.ToString().TrimEnd());
        }

        [Command("cleartags", "Removes all IML tags from CustomData on unlocked containers of a grid. Usage: !iml cleartags <gridId|name>")]
        public void ClearTags(string gridIdOrName)
        {
            var p = Plugin;
            if (p?.Manager == null) { Context.Respond("IML: Plugin not ready."); return; }
            if (string.IsNullOrWhiteSpace(gridIdOrName))
            {
                Context.Respond("IML: Usage: !iml cleartags <gridId|name>   e.g. !iml cleartags 12345");
                return;
            }
            var lines = p.Manager.ClearTagsOnGrid(gridIdOrName);
            var sb = new StringBuilder();
            foreach (var line in lines) sb.AppendLine(line);
            Context.Respond(sb.ToString().TrimEnd());
        }

        [Command("backuptags", "Exports all current IML tags across every grid to a timestamped file in the plugin folder.")]
        public void BackupTags()
        {
            var p = Plugin;
            if (p?.Manager == null || p.ConfigManager == null) { Context.Respond("IML: Plugin not ready."); return; }
            var pluginDir = Path.GetDirectoryName(p.ConfigManager.FilePath) ?? ".";
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var outputPath = Path.Combine(pluginDir, $"iml-tags-{timestamp}.txt");
            var lines = p.Manager.BackupTags(outputPath);
            var sb = new StringBuilder();
            foreach (var line in lines) sb.AppendLine(line);
            Context.Respond(sb.ToString().TrimEnd());
        }
        }
    }
    #endif
