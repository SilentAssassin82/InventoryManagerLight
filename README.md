# InventoryManagerLight

A lightweight Torch plugin for Space Engineers that automatically sorts and distributes items across containers, **off the game thread** — so your server keeps running smoothly while inventory work happens in the background.

> **Version:** 1.4.3  
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

Tags go in a block's **CustomData** field (one per line, case-insensitive for the tag name). Alternatively, the primary category tag can be embedded directly in the **block name**:

```
Large Cargo Container [IML:INGOTS]
```

CustomData is the recommended location — modifier tags (`IML:DENY=`, `IML:ALLOW=`, `IML:FILL=`, `IML:PRIORITY=`) are always read from CustomData, so the block name can only usefully carry the category tag itself.

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

Tells IML this container should receive (and hold) items of that category.

Multiple categories can be combined on a single line, comma-separated:

```
IML:INGOTS,COMPONENTS
```

A container tagged with multiple categories acts as a shared store for all of them — useful on small ships where a single cargo box needs to hold several item types.

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

### Lock a Container

```
IML:COMPONENTS
IML:LOCKED
```

Prevents IML from moving anything into or out of this container — it is skipped entirely as both a source and a destination. The container still shows in `!iml status` with a `[LOCKED]` annotation so you can see it exists, but its item counts are excluded from totals.

Useful for:
- Personal stashes you don't want touched
- Containers being actively loaded/unloaded by players
- Temporarily pausing sorting on a specific box without removing all its tags

> Add `IML:LOCKED` alongside the category tag. A container with only `IML:LOCKED` and no category tag is already ignored by IML.

---

### Limit How Full a Container Gets

```
IML:COMPONENTS
IML:FILL=75
```

Prevents IML from filling this container beyond N% of its volume capacity. If the container is already at or above the limit it is skipped as a destination — items will overflow to the next available container of the same category.

Useful for:
- Leaving headroom next to production blocks that also deposit into the same container
- Keeping a buffer so incoming production output doesn't bounce

> `IML:FILL=100` (the default) means no limit. Values are percentages — `IML:FILL=50` stops filling at half capacity.

---

### Fill Higher-Priority Containers First

```
IML:COMPONENTS
IML:PRIORITY=10
```

When multiple containers share a category, higher-priority containers receive items first. Containers with the same priority receive items in arbitrary order. The default priority is `0`.

Useful for:
- Designating a "primary" storage box that fills before overflow boxes
- Ensuring critical stockpiles top up before secondary reserves

> Use any integer — `IML:PRIORITY=10` will fill before `IML:PRIORITY=5`, which fills before a container with no priority tag (`0`).

---

### Container Groups

Append a group label to the category tag to create isolated sorting pools within a single conveyor network:

```
IML:INGOTS:MedBay
IML:COMPONENTS:MedBay
```

All containers sharing the `:MedBay` label form one independent sort pool. Containers with a different label — or no label — form separate pools. Items flow preferentially between containers in the same group; if a group has a surplus that can't be absorbed internally, items will spill to other containers of the same category.

**Syntax variants — all equivalent:**
```
IML:INGOTS:Bay1
IML:Bay1:INGOTS
```

Useful for:
- Ships with distinct functional areas (medical bay, engineering deck, bridge) on the same conveyor network
- Sub-stations that should maintain their own stock independently of the main base
- Any scenario where you want soft isolation without physically disconnecting conveyors

> The group label is short and arbitrary — `Bay1`, `Med`, `Eng`, `A`, `B` all work. Labels are case-insensitive.

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

#### Range syntax: `min-max`

```
IML:MIN=SteelPlate:500-2000
```

Specifying a range tells IML to **queue up to the max** while treating the **min as a floor alert**:

- IML adds to the queue until `stock + already queued` reaches **2000**
- If stock drops below **500**, it shows as low on LCD panels and `!iml status` just like a `MinStockThresholds` alert
- Without a range (`IML:MIN=SteelPlate:1000`), min and max are the same — behaviour is unchanged

Mixing single values and ranges on the same assembler is fine:
```
IML:MIN=SteelPlate:500-2000
IML:MIN=Motor:300
IML:MIN=Computer:200-1000
```

#### Player-priority yield

When a player manually queues something in an assembler IML is managing, IML detects the non-IML item and **yields** — it skips adding its own maintenance items to that assembler for that scan pass. This gives the player's more time-critical work full assembler capacity without competition.

- IML items are still **claimed** for that assembler (the global-config fallback won't try to queue them elsewhere)
- On the next scan pass, once the player's item has finished or been removed, IML tops back up normally
- The `!iml queueall` output will show `YIELD — 'ItemName' (player work) in queue; IML maintenance paused this pass` for each yielded assembler

> This works best with the range syntax — a large top-up queue (`SteelPlate:500-2000`) is exactly the kind of low-urgency background work that should step aside for player crafts.

**How it works:**
- Each scan pass reads the assembler's current queue + actual inventory for the listed items
- If `current stock + already queued < max`, the deficit is added to *this assembler's* queue
- Items declared via `IML:MIN=` are owned exclusively by that assembler — the global config fallback will not also queue them
- If the queue contains items the assembler's `IML:MIN=` set doesn't own, IML yields for this pass

**Example — dedicated assembler per product line:**

*Assembler A CustomData:*
```
IML:MIN=SteelPlate:500-2000
IML:MIN=InteriorPlate:200-1000
```

*Assembler B CustomData:*
```
IML:MIN=Motor:100-500,SmallTube:200-800
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
| Medical Kit | `Medkit` |
| Gatling Ammo Box | `NATO_25x184mm` |
| Autocannon Magazine | `AutocannonClip` |
| Assault Cannon Shell | `MediumCalibreAmmo` |
| Artillery Shell (250mm) | `LargeCalibreAmmo` |
| Small Railgun Sabot | `SmallRailgunAmmo` |
| Large Railgun Sabot | `LargeRailgunAmmo` |

If a subtype name is wrong, IML will log a debug message: `IML: AssemblerManager: no blueprint for 'XYZ'` — check the Torch log and correct the spelling.

---

### Assembler Scan Interval

Auto-queuing runs every **`AssemblerScanIntervalTicks`** (default: 3 600 ticks ≈ 60 seconds). Set to `0` to disable assembler auto-queuing entirely.

---

### LCD Display Panels

```
IML:LCD
```

Turns a text panel into an IML status display. Panels render using SE's native **sprite/script API** — no mods required. Each panel gets a dark background, a cyan header, coloured progress bars, and per-row alert indicators.

**Visual breakdown:**
- **Header** — cyan `[IML Status]` / `[IML: INGOTS]` title
- **Item rows** — white item name and count; category-filtered panels include the item's in-game inventory icon as a small thumbnail
- **Progress bar** — solid filled bar: green when healthy, amber when below threshold
- **Alert indicator** — amber `!` drawn only on the low-stock row, not the whole panel
- **Footer** — dimmed `Moved / Ops` transfer stats
- **Background** — near-black, set automatically; no manual panel slider adjustment needed

#### Status LCD (all categories)

```
IML:LCD
```

Shows all tracked categories with container counts, progress bars (when `MinStockThresholds` are configured), and transfer stats.

#### Category-filtered LCD

```
IML:LCD=INGOTS
```

Shows only that category — each subtype listed with its in-game icon thumbnail and quantity, sorted by amount descending, with a separator, total line, and progress bar at the bottom. The `!` alert appears only on the total row when stock is low.

Supported values: any built-in category (`INGOTS`, `ORE`, `COMPONENTS`, `TOOLS`, `AMMO`, `BOTTLES`, `FOOD`, `WEAPONS`, `MISC`) or any custom category name you've defined in `<CustomCategories>`.

#### Summary LCD

```
IML:LCD=SUMMARY
```

Shows every category that has a `MinStockThresholds` entry, sorted by worst deficit first (lowest stock ratio at the top). Categories without a threshold follow, sorted by item count. Useful as an at-a-glance "anything critical?" panel.

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
| `!iml refreshdefs` | Scans all loaded game definitions and writes every item subtype not covered by any category to `iml-unknown-items.txt` in the plugin folder. Responds with a one-line summary and the file path. Useful for identifying modded items to add to `<CustomCategories>` in `iml-config.xml` |
| `!iml tagall <gridId\|name> <category>` | Adds an IML tag to the CustomData of every eligible inventory container on the specified grid. Skips production blocks, LCD panels, and locked containers. Accepts a bare category name (`COMPONENTS`) or the full tag (`IML:COMPONENTS`) |
| `!iml cleartags <gridId\|name>` | Removes all IML tag lines from CustomData on every unlocked container on the specified grid. Block names are left untouched. Locked containers are skipped |
| `!iml backuptags` | Exports all IML tags from every grid on the server to a timestamped file in the plugin folder (e.g. `iml-tags-20250612-143022.txt`) |

> **Grid names with spaces:** use the numeric entity ID from `!iml list` — the command parser splits on spaces so multi-word grid names won't work as a name argument. Entity IDs are unambiguous and always safe.

---

## Multi-Grid Support

IML respects **conveyor connectivity**. Containers are grouped by which conveyor network they belong to — items only move between containers that are actually connected. If you have two separate ships not docked together, they are sorted as independent networks.

When grids connect (docking, merge blocks, rotor/hinge attachment with conveyor ports), IML will pick up the combined network on the next sort pass.

---

## In-Game Sort Triggers

Two ways to trigger an immediate sort from inside the game without using the Torch console:

### Terminal Action — "IML: Sort Now"

Every terminal block gains an **IML: Sort Now** action visible in the block's **Setup Actions** picker. Assign it to a button panel, cockpit toolbar, or timer block for hands-free in-game sorting.

- Triggers a full sort across the entire conveyor network (same as `!iml sortall`)
- 5-second per-block cooldown to prevent spam
- Works from any terminal block — cargo containers, assemblers, anything

### CustomData Flag — `IML:SortNow=1`

Write `IML:SortNow=1` to any block's CustomData to request a sort:

```
IML:SortNow=1
```

IML polls for this flag every ~10 seconds, triggers a full sort pass, then automatically clears the flag. Useful when an in-game script or programmable block needs to request a sort by writing to CustomData.

> Both triggers run the same full sort pass as `!iml sortall`. Neither is limited to a single grid or container.

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
| `QueueApplyDelayTicks` | `300` | Ticks to wait after an assembler scan before applying queue additions (~5 sec). Prevents K-menu client disconnects. Set to `0` for immediate apply |
| `SortScanIntervalTicks` | `600` | Ticks between `IML:SortNow=1` flag polls (~10 sec at 60 UPS). Set to `0` to disable the SortNow polling entirely |
| `RequireContainerGroupMatch` | `false` | If `true`, items never cross container-group boundaries — strict group isolation. If `false` (default), same-group containers are preferred but IML will use other groups as a fallback when no matching container exists in the group |
| `CategorySortIntervalTicks` | *(empty)* | Per-category sort interval overrides. When any entry is present, the effective sort interval is the **minimum** of `AutoSortIntervalTicks` and all configured category values. Useful for keeping ammo topped up on a tighter schedule than the global interval — see below |  
| `LowStockSortIntervalTicks` | `1200` | Fast-rescan interval (ticks) used when any `MinStockThresholds` category drops below its threshold. Overrides `AutoSortIntervalTicks` in the same min-of-all logic. Default ≈ 20 sec at 60 UPS. Set to `0` to disable fast rescan |  
| `CustomCategories` | *(empty)* | Admin-defined categories for modded items — see [Custom Categories](#custom-categories-mod-support) below |

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

### Setting MinStockThresholds in the config file

Add one `<Item>` per category inside `<MinStockThresholds>`:

```xml
<ImlConfig>
  <MinStockThresholds>
    <Item key="INGOTS" value="5000" />
    <Item key="COMPONENTS" value="2000" />
    <Item key="AMMO" value="1000" />
  </MinStockThresholds>
</ImlConfig>
```

Then run `!iml reload`. When a category's total item count drops below its threshold, `!iml status` flags it with `[!]` and LCD panels show the progress bar in a low-stock state.

> **Low-stock fast rescan:** When any threshold-tracked category is below its limit, the next sort fires at `LowStockSortIntervalTicks` (default ≈ 20 sec) instead of waiting for the full `AutoSortIntervalTicks` cycle. This means ammo and other critical items re-stock much faster without reducing the global sort interval for everything else.

---

### Setting CategorySortIntervalTicks in the config file

Add one `<Item>` per category you want on its own schedule:

```xml
<ImlConfig>
  <CategorySortIntervalTicks>
    <Item key="AMMO" value="600" />
    <Item key="BOTTLES" value="1200" />
  </CategorySortIntervalTicks>
</ImlConfig>
```

The effective sort interval each tick is the **minimum** of `AutoSortIntervalTicks`, every value in `CategorySortIntervalTicks`, and `LowStockSortIntervalTicks` when a low-stock condition is active. Sort passes are always full — these settings control *when* the pass fires, not what it sorts.

> **PvP scenario:** set `AMMO` to `600` (~10 sec) so turrets never wait 2 minutes to re-stock during a fight, while ore and components sort at the normal 2-minute cadence.

Then run `!iml reload`.

---

## Custom Categories (Mod Support)

> ⚠️ **Untested with real mods** — the custom category and wildcard matching features are implemented and build-verified, but have not yet been tested against an actual modded server. If you run into unexpected behaviour (items not routing, wildcards not matching, LCD not showing the category) please open an issue with your `<CustomCategories>` config and the output of `!iml refreshdefs`.

IML supports admin-defined categories for modded items.

```xml
<ImlConfig>
  <CustomCategories>
    <Category name="ADVANCEDPARTS">
      <Subtype>AdvancedSteelPlate</Subtype>
      <Subtype>HeavyArmorPlate</Subtype>
    </Category>
    <Category name="MODAMMO">
      <Subtype>LargeModRound</Subtype>
      <Subtype>SmallModRound</Subtype>
    </Category>
  </CustomCategories>
</ImlConfig>
```

Then run `!iml reload`. Tag containers with `IML:ADVANCEDPARTS` or `IML:MODAMMO` exactly as you would a built-in category.

### Wildcard SubtypeId Matching

Subtype entries inside `<Category>` support `*` (any number of characters) and `?` (exactly one character) wildcards, so you don't need to list every variant individually:

```xml
<Category name="ADVANCEDPARTS">
  <Subtype>Advanced*</Subtype>
  <Subtype>*HeavyArmor*</Subtype>
</Category>
```

- `Advanced*` matches `AdvancedSteelPlate`, `AdvancedMotor`, `AdvancedReactor`, etc.
- `*HeavyArmor*` matches anything with `HeavyArmor` anywhere in its SubtypeId
- `?` matches exactly one character — `Iron?` matches `IronA` but not `Iron` or `IronAB`
- Wildcards and exact names can be mixed freely in the same category
- Matching is **case-insensitive**

**Finding modded SubtypeIds:**

Use `!iml refreshdefs` — it writes every item subtype not covered by any category to `iml-unknown-items.txt` in the plugin folder and prints a one-line summary to the Torch console. Check the file to identify the exact SubtypeId strings (or patterns) to add to `<CustomCategories>`.

> Custom category names are case-insensitive.

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

### v1.4.3
- **`!iml refreshdefs` — file output:** Instead of spamming the Torch console with one line per unknown item, the command now writes the full list to `iml-unknown-items.txt` in the plugin folder and prints a single summary line (`X unknown subtypes found — written to <path>`). The file is easy to open, search, and copy from.
- **Wildcard SubtypeId matching in `<CustomCategories>`:** `*` (any sequence) and `?` (single character) wildcards are now supported inside `<Subtype>` entries. `Advanced*` matches all subtypes beginning with `Advanced`; `*HeavyArmor*` matches anything containing that string. Exact names and patterns can be mixed freely in the same category. Matching is case-insensitive.
- **Per-category sort intervals (`CategorySortIntervalTicks`):** New config section lets you assign an independent sort interval to individual categories. The plugin fires a sort pass at the minimum of `AutoSortIntervalTicks` and all configured category values — a short `AMMO` interval keeps turrets stocked without shortening the global cycle for everything else.
- **Low-stock fast rescan (`LowStockSortIntervalTicks`):** When any `MinStockThresholds` category drops below its threshold, the next sort fires at this interval (default 1 200 ticks ≈ 20 sec) rather than waiting for the full global cycle. Critical stockpiles re-stock in seconds during PvP instead of up to 2 minutes later.

### v1.4.2
- **LCD per-conveyor-group isolation:** Each LCD panel now displays inventory totals from its own conveyor cluster only. Previously all IML-tagged containers server-wide contributed to a single aggregate, so Player A's `[IML:LCD=INGOTS]` panel would show items from Player B's unconnected INGOTS chest as well. Sorting was already correctly isolated by group; LCD stats now match.

### v1.4.1
- **Shrink-to-fit LCD scaling:** When content rows exceed the panel height the renderer now measures the total required height and scales everything down uniformly (font size, row height, bar height, icon size, padding — all multiplied by the same factor, minimum `0.4`). Panels with few categories are unaffected; panels with many categories no longer clip the footer or bottom separator.
- **Duplicate item rows fixed:** The category detail view (`IML:LCD=INGOTS` etc.) was showing the same SubtypeId twice when items of different TypeIds shared a name — most visibly when ore was present in an ingots chest (`MyObjectBuilder_Ingot/Nickel` and `MyObjectBuilder_Ore/Nickel` both displayed as `Nickel`). Colliding display names are now disambiguated with a short type qualifier, e.g. `Nickel [Ingot]` and `Nickel [Ore]`.

### v1.4.0
- **Sprite-based LCD rendering:** All IML LCD panels now use SE's `ContentType.SCRIPT` sprite API instead of `WriteText`. No mod dependency — this is vanilla SE.
  - **Item icons:** Category-filtered panels (`IML:LCD=INGOTS` etc.) show each item's in-game inventory icon as a small thumbnail next to its name.
  - **Per-element colour:** The alert `!` is drawn amber only on the affected row. The rest of the panel stays white. Previously the whole panel's font colour changed.
  - **Solid progress bars:** Filled portion is green (healthy) or amber (low stock); empty portion is dark grey. Replaces Unicode block-character bars.
  - **Cyan header:** The `[IML: CATEGORY]` title line is always cyan regardless of alert state.
  - **Dark background:** `ScriptBackgroundColor` is set to near-black automatically on every update — no manual slider adjustment needed when placing a new panel.
- **`TypeId/SubtypeId` sprite key:** The internal scan loop now stores the full item type path (e.g. `MyObjectBuilder_Ingot/Iron`) as the subtype key, which doubles as the SE sprite name for icon rendering. Display names strip the TypeId prefix so item names appear unchanged on screen.

### v1.3.0
- **`OnCustomActionGetter` allocation fix:** Removed a debug log call that allocated a new logger object and string-interpolated a block ID on every terminal action list request. This handler fires server-side whenever any client opens a terminal screen, so on an active server it could run thousands of times per minute with no benefit.
- **`ScanFilters` prefix fix:** All five filter directives (`DENY=`, `ALLOW=`, `LOCKED`, `FILL=`, `PRIORITY=`) previously hardcoded `"IML:"` as the tag prefix with fixed substring offsets. They now correctly use the configured `ContainerTagPrefix` — previously they would silently break if the prefix was ever changed.
- **Config fix:** `ConfigManager` was not persisting or loading `QueueApplyDelayTicks`, `SortScanIntervalTicks`, or `RequireContainerGroupMatch` to/from `iml-config.xml`. These three settings were effectively read-only at their default values regardless of what was in the config file.
- **README:** Documented four previously undocumented features: multi-category syntax (`IML:INGOTS,COMPONENTS`), block-name tagging, Container Groups (`:Label` suffix), and In-Game Sort Triggers (terminal action + `IML:SortNow=1` CustomData flag).

### v1.2.9
- **Ingredient resource-check before queuing:** Before adding items to an assembler queue, IML now checks whether the conveyor network has enough ingredients to craft the requested amount. If materials are partially available, IML queues only as many as the ingredients support (**partial fill**); if none are available at all, the queue addition is skipped for this pass. Both outcomes are reported in `!iml queueall` output (`PARTIAL — capped at X/Y` or `BLOCKED — ingredients unavailable`).
- **Ore-to-ingot hint:** When an assembler is blocked because ingots are missing, IML checks whether the corresponding ore exists in the network. If it does, the diagnostic line reads `BLOCKED — ingredients unavailable; ore in network — refinery will provide ingots on next pass`, so you know the blockage is temporary and will resolve automatically once the refinery processes the ore.
- No active refinery manipulation — IML never modifies refinery queues or priorities. The hint is informational only; the refinery processes ore at its own rate and IML tops up the deficit on the next scan pass.

### v1.2.8
- **`IML:MIN=` range syntax (`min-max`):** Per-assembler tags now support `IML:MIN=SteelPlate:500-2000`. IML queues until projected stock reaches the **max**, while the **min** acts as a low-stock alert floor (same as `MinStockThresholds`). Single-value tags (`SteelPlate:1000`) are unchanged — min and max are equal.
- **Player-priority yield:** During each assembler scan, if an assembler's queue contains items not in its `IML:MIN=` set (player-initiated crafts), IML skips adding its own maintenance items to that assembler for that pass. The player's more time-critical work gets full assembler capacity. IML tops back up automatically on the next scan pass once the player's items are done. Works for both per-assembler (CustomData) and global-config assemblers.

### v1.2.7
- **LCD formatting overhaul:** Dropped fixed-width column padding (which misaligned on SE's proportional font). All LCD modes now use clean per-line labels with natural number formatting.
- **Progress bars:** When `MinStockThresholds` are configured, every category on `IML:LCD` and `IML:LCD=CATEGORY` panels now shows a Unicode block-character bar (`[████░░░░░░]`) with current/threshold and a percentage. Bars appear on threshold-tracked categories only — untracked categories show a plain count.
- **`IML:LCD=SUMMARY`:** New LCD mode. Shows all categories that have a `MinStockThresholds` entry sorted by worst deficit first (lowest stock ratio at the top), followed by untracked categories sorted by item count. Ideal as a dedicated "anything critical?" panel.

### v1.2.6
- **K-menu crash fix (`!iml queueall`):** `TriggerAssemblerScan` now respects the `MaxSortMs` time budget — if block enumeration takes too long under server load the scan is aborted and a warning is printed rather than stalling the game thread.
- **Queue apply grace period:** After any assembler scan (periodic or manual), IML now waits `QueueApplyDelayTicks` ticks (default: 300 ≈ 5 seconds) before firing the first `AddQueueItem` call. This gives players time to close the assembler K-menu before queue-change replication packets arrive, preventing client disconnects.
- `QueueApplyDelayTicks` is configurable in `iml-config.xml`. Set to `0` to restore the previous immediate-apply behaviour.

### v1.2.5
- **Category LCD breakdown:** `IML:LCD=INGOTS` (and any other category filter) now lists each item subtype with its quantity, sorted by amount descending, with a total line at the bottom. Low-stock `[!]` alert still appears on the total.

### v1.2.4
- **Per-conveyor-group stock counts:** `IML:MIN=` thresholds are now evaluated independently per conveyor network. Two separate bases each maintain their own stock levels rather than sharing a combined total across the server.
- **`!iml stockdump` improvements:** Items in disconnected or unmanaged grids are annotated with `[SEPARATE GRID]` and an "accessible to sorter" total is shown when the server-wide total differs from what IML can actually reach.

### v1.2.2
- **`IML:FILL=N` tag:** Limits a container to N% of its volume capacity as a destination. When the container is at or above the limit, IML skips it and overflows to the next matching container. Useful for leaving headroom next to production blocks.
- **`IML:PRIORITY=N` tag:** Higher-priority containers of a category receive items first during a sort pass. Default priority is `0`; higher integers fill sooner. Useful for designating primary storage before overflow boxes.

### v1.2.1
- **`IML:LOCKED` tag:** Add `IML:LOCKED` to any tagged container to prevent IML from moving items into or out of it. The container remains visible in `!iml status` with a `[LOCKED]` annotation. Useful for personal stashes, containers being actively worked on, or temporarily pausing sorting without removing category tags.

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
