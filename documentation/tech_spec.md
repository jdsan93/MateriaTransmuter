# Tech Spec: Materia Transmuter

**Author:** Juan David Rodríguez Torres
**Date:** 2026-09-20
**Status:** MVP Complete

> Broad orientation doc for agents working on this repo. It describes *what* the pieces
> are and *how the flow fits together*, not the internals of individual methods or files.
> Read this first, then read the specific code you need to touch.

---

## 1. Overview & Context
- **Goal:** A personal Dalamud plugin that bulk-transmutes unwanted materia at Mutamix
  Bubblypots (Central Thanalan X:23.7, Y:13.6) until the player has N copies of a chosen
  type and grade, or until eligible fodder in the player bags runs out.
- **Game rule:** Mutamix takes **5 materia** and returns **1 random materia**. The result is
  never one of the five submitted types. Its grade matches a submitted grade; a rare upgrade
  to the next grade can happen and is treated as a bonus, not as the targeting goal.
- **Determinism first:** Planning and spending are fully deterministic. There is no LLM.
  Native UI is driven through the game's `AgentTradeMultiple` APIs, not synthesized drag-drop
  clicks (those crash the client).
- **Stack:** C# / `net10.0-windows`, Dalamud.NET.Sdk 15, FFXIVClientStructs, Lumina Excel
  sheets (`Materia`, `Item`), Dalamud ImGui windows. Commands: `/mt` and `/transmute`.
- **Target Components:** `TransmuteController` (run state machine), `GameTransmuteClient`
  (Mutamix / TradeMultiple / Talk / YesNo), `TransmutePlanner` (5-slot batches),
  `MateriaCatalog` (the 13 current transmutable families), `PlayerInventory` (bags 1–4 only),
  `MainWindow` + `ConfigWindow`, and `Configuration` (Dalamud plugin config).

---

## 2. Scope
- Choose a **desired type**, **grade**, and **count**; optionally a keep-list of type/grade
  pairs that must never be spent.
- Scan **only the four player inventory bags**. Never retainers, never the chocobo saddlebag,
  never armory or key items.
- Spend only materia of the **same grade** as the target. Never spend the target type itself.
- Each batch of 5 prefers **as many unique types as possible**, then the type with the most
  remaining copies, then the **smallest stack** of that type (to free bag slots).
- Quantity offered is always **1**, even when a stack is larger.
- Repeat until the desired count is reached or fewer than 5 eligible pieces remain.
- Advance Mutamix Talk and related yes/no prompts (including first-transmute flavor text and
  the grade-up “happy-day” line). Ignore upgrade cutscenes for targeting; wait them out and
  continue.
- In-game preview of the next 5-piece offer, with short stat labels (Crit, Det, DH, …).

Out of scope for this MVP: official Dalamud plugin-repo submission, retainer/chocobo fodder,
targeting a specific upgrade grade, and clicking inventory grids via `AtkComponentDragDrop`.

---

## 3. Data Contracts & Architecture
### 3.1 Persistence
- **`Configuration`** is the single source of truth for player intent. Dalamud serializes it.
  It holds `DesiredMateriaTypeId`, `DesiredGrade`, `DesiredCount`, `Blacklist` (keep-list
  entries of type id + grade), `ActionDelayMs` (default 650), and `DebugLogging`.
- Run progress (`ProducedThisRun`, `TransmutesThisRun`, current batch) lives only on
  `TransmuteController` for the session. It is not persisted.
- **`MateriaCatalog`** is rebuilt from Lumina on load / “Reload materia data”. It maps item
  ids to `(TypeId, Grade, TypeName)` and exposes the 13 current families in UI order:
  Savage Aim, Savage Might, Heavens' Eye, Quickarm, Quicktongue, Battledance, Piety,
  Craftsman's Competence / Command / Cunning, Gatherer's Grasp / Guerdon / Guile.

### 3.2 Runtime pieces
- **`PlayerInventory`** reads `InventoryManager` slots 0–34 on `Inventory1`–`Inventory4`
  (Dalamud `GameInventory` is the fallback). Output is a list of stacks: item, type, grade,
  container, slot, quantity.
- **`TransmutePlanner`** filters fodder (same grade, not target, not keep-listed, not already
  sitting in the trade window) and fills 5 `PlannedPiece`s.
- **`GameTransmuteClient`** talks to native UI. Adds and confirms go through
  `AgentTradeMultiple` (`AddItem` for a stack of 1, `AddItemQuantity(item, 1)` otherwise,
  then `Confirm()`). Talk / SelectYesno / SelectIconString (“Transmute Materia”, never the
  “Materia Transmutation” help line, never Removal) use addon `FireCallback`. Filled-slot
  count and reserved slots are read from **agent slots**, not stale `TradeMultiple` AtkValues.
- **`TransmuteController`** is a framework-tick state machine:
  `Idle → WaitingForWindow → Adding → Confirming → WaitingForResult →` (loop)
  `WaitingForWindow` or `Finished`.

Representative agent slot dump after a successful fill (what `/xllog` should show):

```
agent=[Inventory3:14#5709x1, Inventory3:26#5694x1, Inventory3:33#5699x1, Inventory4:2#5704x1, Inventory3:23#5684x1]
```

---

## 4. Execution Step-by-Step (broad flow)
1. **Setup:** Player puts fodder in bags, sets type/grade/count/keep-list, talks to Mutamix,
   chooses **Transmute Materia**. `/mt` → Start. The plugin may click Talk (`Pshhh...`) and
   the SelectIconString option if the window is not open yet.
2. **Plan:** Scan bags, exclude reserved agent slots, pick 5 pieces. Log eligible count and
   the five offers. If fewer than 5 eligible remain, stop with that reason.
3. **Add:** For each planned piece, call the trade agent with quantity 1. Do not click
   inventory grid drag-drop nodes. If `InputNumeric` appears, confirm 1.
4. **Confirm once:** When the agent reports 5/5, call `AgentTradeMultiple.Confirm()` a
   single time, snapshot bag counts, then wait for Mutamix.
5. **Dialogue:** Click Talk and yes/no prompts that look like transmutation (transmute,
   materia, mutamix, shifty, mixy, uplander, need-not, reborn). Reset the wait timer while
   dialogue is still moving.
6. **Result:** A reward that **stacks** onto an existing pile fires `ItemChanged`, not
   `ItemAdded`. Listen to both, and poll bag counts against the pre-confirm snapshot.
   Log `Result: …` to `/xllog` and chat. Grade-ups (e.g. Guerdon I → Guerdon II) count as a
   roll, not as produced target-grade materia, and must not stop the run.
7. **Clear then loop:** Trust agent filled count. Stale TradeMultiple AtkValues can still
   say 5/5 when the window is empty — do not wait forever on that. When the agent is empty
   (or after a short grace), plan the next batch.
8. **Stop:** Desired count reached, not enough fodder, player hits Stop, or logout.

---

## 5. Edge Cases & Guardrails
- **Never synthesize `AtkComponentDragDrop.ReceiveEvent`.** Fake MouseDown / DragDropClick
  on grid slots crashes the client (`AtkComponentDragDrop.ReceiveEvent+0x74`). Adding is
  agent-only. Also never `ReceiveEvent` on a grid/parent with the bag slot as the event id.
- **Do not treat `OccupiedInEvent` as “player busy”.** Mutamix menus set that flag; blocking
  on it prevents Talk and TradeMultiple from being driven.
- **Select the right Mutamix option.** Popup text lives in `PopupMenu.EntryNames`, not text
  nodes. Pick **Transmute Materia**. Skip **Materia Transmutation** (help) and Removal.
- **Excel `Item.Icon` is not the UI icon.** Matching 20209 vs drag-drop `GetIconId()` 20970
  will never succeed; do not gate clicks on that.
- **Inventory layout vs bag index.** 1-bag: tab = bag. 2-bag (`InventoryLarge`): tab =
  bag/2, `InventoryGrid0` is the left bag on that page, `InventoryGrid1` the right. 4-bag
  expansion uses `InventoryGrid0E`–`3E`. Tab 2 on the 2-bag window is key items, not bag 4.
- **Filled count comes from the agent.** After a transmute, TradeMultiple AtkValues often
  stay at 5/5 while the window is visually empty. Waiting on AtkValues stalls the run with
  no timeout. `GetFilledSlotCount` / `GetOfferedSlots` must use `AgentTradeMultiple.Slots`.
- **Stacking results.** `ItemAdded` misses “You obtain …” when the item merges into an
  existing stack. Always handle quantity increases. Snapshot-at-confirm plus poll is the
  backup if events are late.
- **Confirm is not a loop.** `Confirm()` can keep returning true while 5 items are still
  showing. Submit once, then Talk / YesNo / result — do not re-confirm leftovers.
- **Quantity is always 1.** Never dump a whole stack into a slot.
- **Bags only, same grade, never the target type, keep-list is absolute.**
- **Logs:** Plugin output is `/xllog` (and chat for start/stop/results). Enable debug
  logging in Settings for addon/agent traces. Do not log secrets (there should be none).

---

## 6. Testing & Acceptance Criteria
- [x] In-game MVP: open TradeMultiple, fill 5 different types at quantity 1, confirm, click
      Mutamix Talk, detect the result (including stacked obtains), start the next batch.
- [x] Grade-up roll (e.g. Materia I → II, “Materia reborn glows…”) is logged, does not count
      as the target grade, and the run continues.
- [x] First-transmute Mutamix flavor Talk is advanced (`Pshhh…`, chance-game / uplander).
- [x] Window reports `filled=0/5` from the agent after a batch clears; next plan is not
      blocked by stale 5/5 AtkValues.
- [x] Unique types first, then most copies of a type, then smallest stack of that type.
- [x] Stops cleanly when fewer than 5 eligible same-grade pieces remain.
- [x] `dotnet build MateriaTransmuter.slnx --configuration Debug` succeeds.
- [ ] No automated unit tests yet; regressions are caught by a live Mutamix run and `/xllog`.
- [ ] Client must not crash. If UI automation is extended, keep using agent/callback APIs —
      do not reintroduce drag-drop `ReceiveEvent`.
- [ ] Reload the Debug DLL from `/xlplugins` after each build before a live test.
