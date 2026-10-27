# Runtime NPC Editor

A BepInEx runtime editor for changing loaded NPC descriptions, personality
traits, and the destination/activity fields of their existing routines. Version
2 uses native Silverpine button, font, input-field, transition, and scaling
templates instead of IMGUI.

**Current version:** 2.12.3

The complete developer-facing save and prompt integration map is in
[INJECTION_POINTS.md](./INJECTION_POINTS.md).

The editor registers through the shared Modding Tools
`InventoryModTools.Register` API and does not create its own pause-menu tab or
button. Modding Tools supplies the shared Newtonsoft.Json 13.0.3 assembly; do
not install another copy beside RuntimeNPCEditor.

## Usage

1. Load a game.
2. Open the pause/inventory menu with `I`.
3. Select the `Mods` tab and click `NPC Editor`.
4. Select an enabled NPC.
5. Edit text fields or the NPC's current routine.
6. Save the override.

The description fields initially call the game's resolved public and private
description paths. The routine browser does not make speculative hourly calls.
For any NPC with definition-backed `possibleRoutines` (including base-NPC
overrides), it instead clones those definitions directly. Browsing those
routines therefore does not execute `GetTargetRoutineID` or produce
schedule-script side effects.
For other base routines, the plugin passively records each new normal routine
when it becomes active (including an active routine restored from a save) and
exposes those observed definitions in the editor. Temporary/scripted override
routines and routines already modified by this editor are excluded so they do
not contaminate the catalog. Base NPCs are keyed by their enum identity;
custom NPCs are keyed by their final custom-content name instead of their
temporary NPC slot, so their catalog follows them if slot assignments change.

The Routines tab also provides **Analyze / Catalog** for the selected routine.
It rejects temporary scripted routines and editor-modified data, reports
entries that are already known, and permanently records or refreshes a clean
routine when appropriate.

Observed routines are permanently stored separately from save overrides in:

`BepInEx/config/RuntimeNPCEditor/routine-catalog.json`

The catalog uses Base64-encoded output from the game's own
`NPCRoutine.Serialize` implementation, preserving polymorphic routine
arguments. It is global base-game knowledge rather than save-specific state and
is loaded when the plugin starts.

The routine editor's **Pick World Tile** button temporarily hides the editor,
closes the parent Modding Tools pause menu without releasing its session,
accepts a click on any loaded valid turf, and copies that position into the
target X/Y fields. Escape cancels; either path restores both menus.

For custom NPCs, the plugin leaves every valid `GetTargetRoutineID` result
unchanged and adds an in-memory safety guard around the definition's Lua
selector. If a malformed or incomplete selector returns `nil` or another
non-string value, that one selection falls back to `silverpine_leisure`
instead of producing the game's `didn't return a System.String` error.

Routine overrides preserve the game's hourly schedule and all existing routine
arguments. A routine is matched by its explicit ID where available, otherwise
by its original pathing, destination, and visible activity strings.

Overrides are scoped to the game's internal run ID. Each save is stored in its
own file:

`BepInEx/config/RuntimeNPCEditor/saves/<runID>.json`

Version 2.8 automatically migrates the former combined `overrides.json` file.
After every non-empty scope is written successfully, the original is moved to
`BepInEx/config/RuntimeNPCEditor/legacy/overrides-pre-2.8.0.json`. Empty
transient startup scopes are discarded during migration.

Background load and prompt hooks may select an existing run-ID scope but do
not create a missing one. A new scope is created, populated with save metadata,
and written only when the in-game NPC Editor is first opened in that save.
Save identity is refreshed again before every commit.

The editor does not restart temporary/scripted override routines. A saved
change will be applied when the corresponding normal routine is next created.
New overrides retain the routine's original target and activity fields.
Removing an override from the active normal routine restores and restarts it
immediately; scripted override routines are left untouched.

The **Knowledge** tab edits the same named lore entries used by the game's NPC
definition editor. Each entry can be public (eligible to appear in acquainted
NPC prompts when contextually relevant) or private (owner prompt only).
Overrides are save-scoped and base definitions remain recoverable.
New or text-edited entries are queued when the editor closes. The game then
generates their word clouds sequentially (one entry at a time) and caches the
result in the override database. Changing only public/private visibility reuses
the existing cache.
Runtime knowledge entries support `%NAME%` for the owning NPC and
`%PLAYER_NAME%` for the current player. Placeholders remain unchanged in the
saved override and are resolved only when used or submitted for word-cloud
generation.
Save-scoped entries are marked `[MOD]` in the Knowledge list. **Delete Mod
Entry** removes only those entries; deleting an override of a game-provided
entry reveals its original definition again.
Mod entries are synchronized into the game's native knowledge definition
before conditional lore is selected. Silverpine's normal word-cloud and
relevance logic determines which entries appear in each conversation prompt;
the plugin does not force entries that the selector omitted.

The **Relationships** tab sets the selected NPC's player relationship level,
daily cap, and total cap **once** when **Apply Player Values Once** is clicked.
All three values then follow the game's normal progression. Conversations,
save loading, and other editor actions never replay these numeric edits.
**Save the game** to persist them; the editor JSON is not their save mechanism.
The fields show current live values. Total cap is a dropdown containing the
four native stages: **14, 49, 79, 100**. Daily cap is an absolute ceiling, not
a daily increment; the game normally resets it to current level + 10 each day.

Upgrading disables numeric locks from older editor versions automatically,
without resetting the values already in the game save. The temporary repair
button from 2.12.2 is removed in 2.12.3; no repair action is needed to stop the
old reset bug. Use the one-time Apply button for any further manual changes,
then save the game to keep them.

The tab also edits NPC-to-NPC relationship tiers and optional custom prompt
labels. NPC-to-NPC tier changes are applied symmetrically because
the base game expects both sides of a relationship to agree.
When Dynamic NPC Relationships is installed, Runtime NPC Editor adds a
dedicated **Dynamic** tab. Its save-wide toggle starts off for a new run;
Dynamic Relationships creates no per-run relationship JSON until that toggle
is enabled and the player saves the game. Disabling it later retains existing
progress in the JSON. Dynamic-tab changes take effect immediately in memory but
are written to that JSON only by Silverpine's next game save. The tab also
displays each managed pair's saved stage,
score, direct interaction count, next promotion review, configured
Friend/Lover/Married promotion and retention thresholds, and suspension state. It also
shows the deterministic Enemy, Stranger, and Acquaintance thresholds and provides
controls for editing the save-scoped score, current dynamic tier, and full tier
lock. The score is clamped to Dynamic Relationships' configured range. A tier
lock freezes both the score and selected tier against conversation, rumor,
daily-decay, and external changes, and bypasses the associated sentiment and
promotion model queries. Manual editor changes remain available while locked.
It is separate from the rejection-based romantic promotion ceiling. The page also supports
explicitly enrolling eligible unlabelled Stranger pairs, resuming a suspended
pair, and resetting its dynamic progress. Suspended pairs must be resumed
before their score, tier, or tier lock can be edited. The tab is not created when the mod
is absent. Older Dynamic Relationships versions without the state-edit bridge
remain readable but do not show the edit controls.

Saving a manual relationship on the normal Relationships tab suspends dynamic
control of that pair. **Remove NPC Pair Override** removes saved enforcement
in both directions and resumes dynamic control when available. Without a
dynamic controller, current native values remain; this is not a historical
undo of values already saved by the game. This integration
uses reflection, so Runtime NPC Editor retains no hard assembly dependency on
Dynamic NPC Relationships.
The player can also be given an independent custom relationship title (for
example, ward, mentor, spouse, or sworn sibling). This changes prompt wording
without enabling or changing the numeric player relationship values. The
title is injected through the final knowledge/prompt path so other relationship
patches cannot overwrite it.

## Reliability and review

Per-save override files and the routine catalog are written by atomic
replacement, with the previous complete file retained as `.bak`. A per-save
file that failed to load will not be overwritten with a new empty scope.
Repair/restore that file and restart the game to re-enable saving its overrides.
An unreadable legacy combined database does not prevent valid per-save files
from loading.

The full source review for 2.12.2, fixed findings, remaining limitations, and
manual in-game checks are recorded in [CODE_REVIEW_2.12.2.md](./CODE_REVIEW_2.12.2.md).

## Credits

Created by **Saelac and ChatGPT**.
