# InventoryManagerLight

A lightweight Torch plugin for Space Engineers that automatically sorts and distributes items across containers, **off the game thread** — so your server keeps running smoothly while inventory work happens in the background.

> **Version:** 1.2.0  
> **Author:** Chris  
> **Plugin GUID:** `50bc17bd-b3d6-4da8-b332-c62e569f909c`  
> **Repository:** https://github.com/SilentAssassin82/InventoryManagerLight

---

## Overview

InventoryManagerLight (IML) scans all terminal blocks on your server, reads **custom data tags** you place on containers, and automatically moves items into the right boxes on a configurable tick interval. Everything runs off-thread — the game thread only applies the final transfers in small throttled batches.

**Key design goals:**
- No noticeable frame impact even on large grids with hundreds of containers
- Tag-based configuration — no config files to edit per-grid, just CustomData
- Works across multiple grids in the same conveyor group
- Production block awareness — won't drain assembler/refinery input queues

---

## Installation

1. Download or build `InventoryManagerLight.dll`
2. Drop the folder (containing `manifest.xml` + `InventoryManagerLight.dll`) into your Torch `Plugins` folder
3. Start/restart Torch
4. The plugin auto-enables. Use `!iml status` in the Torch console to confirm it's running

---

## How It Works

Every **AutoSortIntervalTicks** (default: 6 000 game ticks ≈ 2 minutes), IML:

1. Scans all terminal blocks across all grids
2. Groups them by **conveyor network** — each isolated network is sorted independently
3. Identifies containers with IML tags in their CustomData
4. Runs a background planner that decides which items to move where
5. Applies transfers on the game thread in small throttled chunks to stay within frame budget

---

## Container Tags (CustomData)

Tags go in a block's **CustomData** field (one per line, case-insensitive for the tag name).

### Assign a Category

```
IML:INGOTS
IML:ORE
IML:COMPONENTS
IML:TOOLS
IML:AMMO
IML:BOTTLES
IML:FOOD
IML:WEAPONS
IML:MISC
```

Tells IML this container should receive (and hold) items of that category. A container can have **only one primary category tag**.

| Tag | What goes in it |
|-----|----------------|
| `IML:INGOTS` | All ingots (Iron, Gold, Uranium, etc.) |
| `IML:ORE` | All ores |
| `IML:COMPONENTS` | All components (Steel Plate, Motor, etc.) |
| `IML:TOOLS` | Welders, Grinders, Drills |
| `IML:AMMO` | All ammunition |
| `IML:BOTTLES` | Hydrogen and Oxygen bottles |
| `IML:FOOD` | Seeds, consumables |
| `IML:WEAPONS` | Pistols, rifles, automatic rifles |
| `IML:MISC` | Catch-all — anything that doesn't match another category ends up here |

---

### Filter What a Container Accepts

Fine-tune exactly which subtypes a tagged container will accept or reject.

#### Deny specific subtypes

```
IML:INGOTS
IML:DENY=Gold,Platinum,Uranium
```

This container accepts all ingots **except** Gold, Platinum, and Uranium.

#### Allow only specific subtypes

```
IML:INGOTS
IML:ALLOW=Iron,Nickel,Silicon
```

This container **only** accepts Iron, Nickel, and Silicon ingots. Everything else goes elsewhere.

- Subtype names are the SubtypeId (e.g. `Iron`, `Stone`, `NATO_5p56x45mm`)
- DENY and ALLOW are mutually exclusive per container — don't use both on the same block
- Comma-separated, no spaces around commas

---

### Opt Out of Production Drain

```
IML:NoDrain
```

By default, IML will pull finished items out of production block output inventories (assembler output slot, refinery output slot) and route them to their category containers. Add `IML:NoDrain` to a production block to leave it alone.

> IML **never** touches production input queues regardless of tags.

---

## Assembler Auto-Queuing

IML can automatically queue blueprints in assemblers to maintain minimum stock levels. Two modes work simultaneously — per-assembler CustomData takes priority, global config is the fallback.

### Per-Assembler Tag (recommended)

Add one or more `IML:MIN=` lines to an assembler's **CustomData**. That assembler becomes the *exclusive* producer for the listed items — no other assembler will queue them.

> **Where to put it:** CustomData supports multiple lines and is recommended. The block name also works but only supports a single `IML:MIN=` entry.

**CustomData (multi-line, recommended):**
```
IML:MIN=SteelPlate:1000
IML:MIN=Motor:500
```

**Block name (single line, if you prefer):**
```
Assembler [IML:MIN=SteelPlate:1000,Motor:500]
```

Multiple items can be combined on a single line in either location:

```
IML:MIN=SteelPlate:1000,Motor:500,Construction:2000
```

**How it works:**
- Each scan pass reads the assembler's current queue + actual inventory for the listed items
- If `current stock + already queued < target`, the deficit is added to *this assembler's* queue
- Items declared via `IML:MIN=` are owned exclusively by that assembler — the global config fallback will not also queue them

**Example — dedicated assembler per product line:**

*Assembler A CustomData:*
```
IML:MIN=SteelPlate:2000
IML:MIN=InteriorPlate:1000
```

*Assembler B CustomData:*
```
IML:MIN=Motor:500,SmallTube:800
```

---

### Global Config Fallback

> **Note:** Per-assembler `IML:MIN=` tags in CustomData are the recommended approach. The global config fallback is set via `AssemblerThresholds` in `iml-config.xml` — no rebuilding required.

Add entries to `AssemblerThresholds` in `iml-config.xml`:

```xml
<AssemblerThresholds>
  <Item key="SteelPlate" value="500" />
  <Item key="Motor" value="200" />
</AssemblerThresholds>
```

> If an item appears in both a CustomData `IML:MIN=` tag **and** the global config, the CustomData assembler wins and the global entry is skipped for that item.

---

### Subtype Names

Keys are **SubtypeId** values as they appear in Space Engineers (case-insensitive). These reflect post-Economy-update vanilla SE — use `!iml stockdump <name>` to verify the exact SubtypeId on your server if unsure.

| Item | SubtypeId |
|------|-----------|
| Steel Plate | `SteelPlate` |
| Interior Plate | `InteriorPlate` |
| Metal Grid | `MetalGrid` |
| Construction Comp. | `Construction` |
| Small Steel Tube | `SmallTube` |
| Large Steel Tube | `LargeTube` |
| Girder | `Girder` |
| Bulletproof Glass | `BulletproofGlass` |
| Motor | `Motor` |
| Medical Component | `Medical` |
| Computer | `Computer` |
| Display | `Display` |
| Detector Component | `Detector` |
| Radio Comm. Component | `RadioCommunication` |
| Gravity Generator Comp. | `GravityGenerator` |
| Reactor Component | `Reactor` |
| Thruster Component | `Thrust` |
| Explosives | `Explosives` |
| Power Cell | `PowerCell` |
| Solar Cell | `SolarCell` |
| Superconductor | `SuperConductor` |

If a subtype name is wrong, IML will log a debug message: `IML: AssemblerManager: no blueprint for 'XYZ'` — check the Torch log and correct the spelling.

---

### Assembler Scan Interval

Auto-queuing runs every **`AssemblerScanIntervalTicks`** (default: 3 600 ticks ≈ 60 seconds). Set to `0` to disable assembler auto-queuing entirely.

---

### LCD Display Panels

```
IML:LCD
```

Turns a text panel into an IML status display. Shows item counts by category plus transfer statistics (items moved, operations completed).

#### Category-filtered LCD

```
IML:LCD=INGOTS
```

Shows only the specified category on this panel. Useful for dedicated displays next to specific storage areas.

Supported values: `INGOTS`, `ORE`, `COMPONENTS`, `TOOLS`, `AMMO`, `BOTTLES`, `FOOD`, `WEAPONS`, `MISC`

LCDs refresh every **LcdUpdateIntervalTicks** (default: 300 ticks ≈ 5 seconds).

---

## Console Commands

All commands are entered in the Torch console (or server chat with appropriate permissions).

| Command | Description |
|---------|-------------|
| `!iml status` | Shows plugin statistics: total sort passes, operations completed, items moved, and current config values |
| `!iml reload` | Reloads `iml-config.xml` and applies the new settings immediately — no server restart needed |
| `!iml sortall` | Triggers an immediate full sort pass across all grids right now |
| `!iml queueall` | Immediately runs the assembler auto-queue scan and prints per-assembler results (found/queued/OK/error) |
| `!iml sort <entityId>` | Triggers an immediate sort pass for the grid containing the given entity ID |
| `!iml list` | Lists all containers currently tracked by IML with their tags and categories |
| `!iml stockdump <SubtypeId>` | Shows every inventory slot containing the specified item, with amounts and slot type. Slots inside assembler/refinery input inventories are flagged `[SKIPPED by ScanAndQueue]`. Use this to diagnose unexpected stock counts — e.g. `!iml stockdump SteelPlate` |
| `!iml dump` | Dumps detailed internal state to the Torch log for debugging |

---

## Multi-Grid Support

IML respects **conveyor connectivity**. Containers are grouped by which conveyor network they belong to — items only move between containers that are actually connected. If you have two separate ships not docked together, they are sorted as independent networks.

When grids connect (docking, merge blocks, rotor/hinge attachment with conveyor ports), IML will pick up the combined network on the next sort pass.

---

## Configuration

IML stores its settings in **`iml-config.xml`**, created automatically next to `InventoryManagerLight.dll` on first run. Edit the file and run `!iml reload` to apply changes without restarting the server.

> **Location:** `Torch/Plugins/InventoryManagerLight/iml-config.xml`

In-game configuration is done entirely through block **CustomData** tags (see above).

Available settings:

| Setting | Default | Description |
|---------|---------|-------------|
| `AutoSortIntervalTicks` | `6000` | Ticks between automatic sort passes (~2 min at 60 UPS) |
| `LcdUpdateIntervalTicks` | `300` | Ticks between LCD refreshes (~5 sec). Set to `0` to disable |
| `DrainProductionOutputs` | `true` | Whether to pull finished items from assembler/refinery output slots |
| `AssemblerScanIntervalTicks` | `3600` | Ticks between assembler auto-queue scans (~60 sec). Set to `0` to disable |
| `RestrictToConveyorConnectedGrids` | `false` | If true, only sorts within connected conveyor networks (experimental) |
| `TransfersPerTick` | `50` | Max transfer operations applied per game tick |
| `MsBudgetPerTick` | `2` | Max milliseconds spent applying transfers per tick |
| `MaxSortMs` | `100` | Max milliseconds for block enumeration during a sort pass |
| `AssemblerThresholds` | *(empty)* | Global per-subtype minimum stock for assembler auto-queuing (fallback when no `IML:MIN=` tag claims the item) |
| `MinStockThresholds` | *(empty)* | Per-category low-stock alert thresholds for LCD panels and `!iml status` |

### Setting AssemblerThresholds in the config file

Add one `<Item>` per subtype inside `<AssemblerThresholds>`:

```xml
<ImlConfig>
  <AssemblerThresholds>
    <Item key="SteelPlate" value="500" />
    <Item key="Motor" value="200" />
  </AssemblerThresholds>
</ImlConfig>
```

Then run `!iml reload`. Per-assembler `IML:MIN=` tags in CustomData always take priority over these global fallbacks.

---

## Example CustomData Setups

**Dedicated iron ingot box:**
```
IML:INGOTS
IML:ALLOW=Iron
```

**General ingots except precious metals:**
```
IML:INGOTS
IML:DENY=Gold,Platinum,Uranium
```

**Component storage:**
```
IML:COMPONENTS
```

**Overflow / everything else:**
```
IML:MISC
```

**LCD next to ore storage:**
```
IML:LCD=ORE
```

**Assembler that should keep its output:**
```
IML:NoDrain
```

**Assembler dedicated to plates and tubes only:**
```
IML:MIN=SteelPlate:2000
IML:MIN=SmallTube:1000,LargeTube:500
```

**Assembler that handles all other components (global config fallback):**
*(no CustomData tag needed — add entries to `AssemblerThresholds` in `iml-config.xml` and run `!iml reload`)*

---

## Performance Notes

- All planning runs on a **background thread** — the game thread only sees small throttled transfer batches
- Each sort pass applies a capped number of transfer operations per tick to stay within frame budget
- Stats visible via `!iml status` include total items moved and ops completed since server start
- Tested on single-player equivalent load — **needs stress testing on high-block-count multiplayer servers** (see below)

---

## Stress Testing — Help Wanted

This plugin has been tested on a live server with a small number of accounts. To find edge cases and performance limits, we need people to **really push it**:

### What to test

- **Large grids** — 500+ containers all tagged, high item throughput
- **Many simultaneous grids** — lots of ships/stations sorting at the same time
- **Docking/undocking** — connect and disconnect grids mid-sort pass
- **Edge-case tags** — weird subtype names in DENY/ALLOW, multiple LCD panels on one grid
- **Production stress** — dozens of assemblers/refineries with DrainProductionOutputs enabled
- **Assembler auto-queuing** — multiple assemblers each with different `IML:MIN=` sets, verify no item is double-queued across assemblers; mix CustomData and global config thresholds on the same server
- **Creative mode** — overfill scenarios, volume bypass behavior
- **Mixed categories** — containers with ALLOW lists that overlap across category types
- **Rapid `!iml sortall`** — spam the command while grids are active to stress the background queue

### What to report

When something goes wrong, please grab:
1. Output from `!iml dump` (paste to the issue)
2. Output from `!iml status`
3. A description of your grid setup (block count, container count, categories used)
4. Any entries from the Torch log (`torch-server.log`) around the time of the issue

### Where to report

Open an issue at: https://github.com/SilentAssassin82/InventoryManagerLight

---

## Changelog

### v1.2.0
- **Persistent config file:** IML now reads and writes `iml-config.xml` next to the plugin DLL on first run. All server-tunable settings (`AutoSortIntervalTicks`, `DrainProductionOutputs`, `AssemblerThresholds`, `MinStockThresholds`, etc.) are now editable without rebuilding.
- **`!iml reload` command:** Applies a new `iml-config.xml` to the running plugin instantly — no server restart required.

### v1.1.2
- **Blueprint subtype fix:** Space Engineers' Economy update renamed several vanilla components (e.g. the item SubtypeId `MotorComponent` became `Motor`, `MedicalComponent` became `Medical`) while the blueprint SubtypeIds were left unchanged. IML now uses `MyDefinitionManager` to resolve the correct blueprint for each item at runtime, so queuing and queue-readback work correctly regardless of which SE version or update level the server is running. No config changes needed.

### v1.1.1
- **DC fix:** assembler `AddQueueItem` calls are now spread one-per-tick (deferred queue) instead of firing all at once, eliminating client disconnects when the K menu is open during a scan.
- **Stock count fix:** item totals are now counted across all inventory blocks in the same conveyor group (not just IML-tagged containers), so items sitting in untagged cargo containers are visible and do not trigger infinite re-queuing.
- `!iml stockdump <SubtypeId>` command added for diagnosing unexpected stock counts.
- Pirate/NPC faction containers are excluded from sorting.

### v1.1.0
- Initial public release.

---

## License

See repository for license details.
