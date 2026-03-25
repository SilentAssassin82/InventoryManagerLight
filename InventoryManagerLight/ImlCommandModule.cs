#if TORCH
using System.Collections.Generic;
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
            Context.Respond(count > 0
                ? $"IML: Sort triggered for {count} managed container(s)."
                : "IML: No managed containers found. Tag containers with 'IML:CATEGORY' in the block name or CustomData.");
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
        }
    }
    #endif
