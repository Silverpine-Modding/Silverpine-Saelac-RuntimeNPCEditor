# Runtime NPC Editor Save Injection Points

This document describes how Runtime NPC Editor 2.8.0 selects save-scoped
data and injects it into Silverpine. Method names refer to
[`Plugin.cs`](./Plugin.cs).

## Meaning of "new save"

Runtime NPC Editor scopes individual override files by Silverpine's internal
`SaveUI.runID`.

- Loading an existing save selects the `EditorDatabase.saves[runID]` entry
  previously written for that run.
- Starting a genuinely new game produces a new run ID, but background hooks
  do not create an entry for it.
- The first time the NPC Editor opens in that run, it creates the
  `SaveOverrides` entry, records save metadata, and writes that run's file.
- Overrides from one run are not intentionally copied into another run.

The separate `routine-catalog.json` file is global. It is not scoped to a run
ID, so clean observed routines remain available when another save is started
or loaded. The catalog supplies routine definitions to the editor; it does not
apply routine overrides by itself.

## Startup registration

`Plugin.Awake` performs all process-lifetime registration:

1. Stores the managed `Plugin.Instance` and logger.
2. Registers the NPC Editor with `InventoryModTools`.
3. Loads every `saves/<runID>.json` file into `EditorDatabase`, migrating the
   former combined `overrides.json` when present.
4. Loads `routine-catalog.json` into the in-memory observed-routine catalog.
5. Applies every Harmony patch in the assembly.
6. Subscribes `HandleFinishedLoadingSave` to
   `SerializationManager.OnFinishedLoadingSave`.

Silverpine destroys the Unity-native `BaseUnityPlugin` component shortly after
`Awake`. The managed object is deliberately retained by the registered
delegates, and the static Harmony patches remain active. Code testing the
retained service must use `ReferenceEquals(Plugin.Instance, null)`, not
Unity's overloaded `Plugin.Instance == null`, because Unity reports a destroyed
component as null even while its managed fields and methods remain usable.

`OnDestroy` saves metadata and the database. It intentionally does not
unsubscribe the save-load event and does not unpatch Harmony.

## Save selection

`RefreshSaveIdentity` is the central save-binding function.

1. Read private `SaveUI.runID` through `RunIdField`.
2. Ignore missing or empty IDs.
3. If the ID already equals `currentRunId`, refresh `CurrentSave` from the
   matching database entry.
4. If the ID changed, select `Database.saves[runID]` when it already exists;
   otherwise use a temporary empty in-memory scope.
5. Reset the deferred NPC-application pass.
6. Immediately call `ApplyNpcOverridesToLoadedNpcs`.

`TryGetActiveOverride` repeats the `SaveUI.runID` lookup before every
field-specific injection. This prevents a stale `CurrentSave` reference from
leaking overrides from a previous or transient run into the active save. It
returns no override when the active run ID has no persisted scope.

The editor-opening paths call `RefreshSaveIdentity(createIfMissing: true)`,
update `SaveInfo`, and immediately save the database. No background
load/prompt/spawn path passes `createIfMissing: true`.

## Load and registration sequence

The paths below overlap intentionally. NPC registration occurs in stages, so
one callback cannot reliably see every base and custom NPC.

| Order | Game injection point | Runtime NPC Editor action |
|---|---|---|
| 1 | `Plugin.Awake` | Load both JSON databases, install Harmony patches, and subscribe to save completion. |
| 2 | `Player.Update` postfix | Call `RunManagedUpdate` even after Unity destroys the original plugin component. |
| 3 | `RunManagedUpdate` | Refresh the run ID and schedule repeated applications when the loaded NPC count changes. |
| 4 | `NeuralNPC.Start` postfix | Apply the selected save's direct overrides to that individual NPC as it starts. |
| 5 | `CustomNPCManager.SpawnInNPCs` postfix, priority last | Refresh the save and reapply after custom NPC names and slot assignments are registered. |
| 6 | `SerializationManager.OnFinishedLoadingSave` | After three `ActionQueue` frames, refresh identity, update metadata, save the database, and apply to all registered NPCs. |
| 7 | Deferred application | Apply to all loaded NPCs eight times at 0.5-second intervals whenever the NPC registry count changes. |

The three-frame delay after save completion allows deserialized objects and
registries to settle. The deferred passes handle NPCs that appear after that
event.

For a new game, the same spawn, NPC-start, and managed-update paths run against
a temporary empty scope. No per-run file is created until the player opens the
NPC Editor in that run.

## NPC identity

`GetNpcStorageKey` defines the keys inside `SaveOverrides.npcs`.

- Base NPC: the stable `NPCName` enum string, such as `Acacia`.
- Custom NPC: `"custom:" + npc.GetFinalName()`, such as
  `custom:Seraphine`.

Custom NPC slots are not persistence keys. A custom NPC may move between
`CustomNPCSlot` values across loads; its final name remains the lookup key.

## Direct NPC-state application

`ApplyNpcOverridesToLoadedNpcs` enumerates `NeuralNPC.neuralNPCs` and calls
`ApplyNpcOverride` for every loaded NPC.

`ApplyNpcOverride` directly handles:

- personality traits;
- knowledge-definition synchronization;
- symmetric NPC-to-NPC relationship tiers and custom labels.

It does not directly replace the final description string, player relationship
title prompt text, or future routine objects. Those values are enforced at
their game consumption points below.

As of 2.12.2, numeric player relationship values are written only by the
explicit one-time Apply button (and the optional cap-repair button). They are
not part of `ApplyNpcOverride`, save-load replay, or knowledge synchronization.
The base game's `NeuralNPC.Serialize/Deserialize` persists these fields when
the player saves. `NormalizeSave` disables legacy `playerRelationshipEnabled`
flags, retaining the old recorded numbers solely as reference data.

The original-knowledge cache is keyed by definition instance. A run-ID change
restores the cached originals before the new run is applied. Word-cloud
generation snapshots its iteration lists and validates the active save and
entry ownership again after inference; stale results are discarded.

## Description injection

Harmony target:
`NeuralNPC.GetFinalCharacterDescription(bool isPrivate)`, postfix.

When `descriptionEnabled` is true, the postfix replaces the resolved return
value with the saved public or private description. A blank private
description falls back to the public description. It resolves:

- `%PLAYER_NAME%` to the current player name;
- `%NAME%` to the NPC's final name.

The NPC's currently applied personality traits are appended in the same format
expected by the base prompt.

`GetOriginalResolvedDescription` temporarily enables
`SuppressDescriptionOverride` when the editor needs to display the underlying
game/custom-content description instead of its own override.

## Knowledge injection

### Definition synchronization

`ApplyKnowledgeOverrides` updates the NPC's
`CustomContentDefinition_NPC.knowledgeEntries`.

- The original definition list is cloned once and retained in
  `OriginalKnowledge`.
- Each application reconstructs the working list from that original, then
  replaces or adds the save-scoped entries.
- Base NPCs without a definition receive a minimal enabled definition when
  there are mod knowledge entries.
- A disabled vanilla override definition is not activated or mutated.
- Cached word-cloud nouns, verbs, adjectives, and keywords are copied into the
  runtime entry.

### Conversation-time synchronization

Harmony target: `Lorebook.GetConditionalLore`, prefix.

The prefix refreshes save identity and synchronizes the speaking NPC before
the game selects lore. There is no postfix insertion: Silverpine's native
word-cloud and relevance logic decides which synchronized entries appear in
the resulting prompt.

Knowledge text resolves `%PLAYER_NAME%` and `%NAME%` at runtime. The JSON keeps
the placeholders unchanged.

## Player relationship title injection

The custom player title is independent of numeric player edits. Migration of
the obsolete `playerRelationshipEnabled` flag does not disable the title.

Two prompt points enforce the title:

1. `NeuralNPC.GetRelationshipString`, postfix with priority last, replaces the
   relationship sentence with:
   `PLAYER is the TITLE of NPC.`
2. `NeuralNPC.GetCombinedPrompt`, asynchronous postfix with priority last,
   replaces the relationship region between the completed player-description
   and NPC-description sections.

The second patch protects the title from later prompt assembly that might
discard or regenerate the earlier relationship string.

## NPC-to-NPC relationship injection

`ApplyNpcRelationshipOverrides` resolves each target by the same stable NPC
storage key. It then writes:

- the relationship tier in both NPCs' `relationships` dictionaries;
- the custom relationship label in both NPCs'
  `customRelationshipNames` dictionaries.

Applying both directions matches the base game's expectation that the
relationship pair agrees.

## Routine injection

Routine overrides mutate the routine instance returned or restored by the
game. They preserve the hourly schedule and unrelated serialized arguments.

The routine key is captured before mutation:

- `id:<routine.id>` when a meaningful explicit ID exists;
- otherwise
  `sig:<pathingActivity>|<activity>|<visibleActivity>`, normalized to
  lowercase and trimmed.

Using the original key is essential because an edited routine may change the
same activity strings used by its fallback signature.

### Future or newly selected routines

Harmony target: `NPCRoutineLibrary.GetNPCRoutine`, postfix.

The method arguments are `(int hour, NPCName npcName)`. The postfix uses the
second argument (`__1`) as the owner; the hour must never be interpreted as an
NPC enum value. The postfix records routines requested naturally by the game,
resolves the NPC by stable storage key when possible, and mutates:

- `targetPosition`;
- `pathingActivity`;
- `activity`;
- `visibleActivity`.

For early base-NPC calls that occur before the NPC registry is available, the
base `NPCName` enum key is used directly.

Opening the editor does not call `GetNPCRoutine` for all 24 hours. Its list is
built from the active routine, definition-backed `possibleRoutines`, and clean
permanently observed routines.

### Active routine restored from a save

Harmony target: `NPCRoutineExecutor.Deserialize`, postfix.

The postfix observes the clean deserialized active routine and immediately
applies its matching override. This is the injection point that allows changed
coordinates and activity fields to affect a routine already active when the
save loads.

### Routines starting during play

Harmony target: `NPCRoutineExecutor.StartNPCRoutine`, prefix.

This path records newly active clean routines for the permanent catalog.
Temporary/scripted override routines and routines already modified by Runtime
NPC Editor are excluded to avoid contaminating the base catalog.

## Persistence files

`BepInEx/config/RuntimeNPCEditor/saves/<runID>.json`

- one file per `runID`;
- contains save metadata and NPC overrides;
- contains description, personality, numeric player relationship, player
  title, knowledge, NPC relationships, and routine mutations.

The pre-2.8 combined `overrides.json` is migrated once. Non-empty scopes become
individual files, empty transient startup scopes are discarded, and the
original is moved into the `legacy` directory.

`BepInEx/config/RuntimeNPCEditor/routine-catalog.json`

- keyed by stable NPC storage key and original routine key;
- contains Base64 output from the game's `NPCRoutine.Serialize`;
- global across saves;
- supplies known clean routine definitions to the editor.

## Safe extension checklist

When adding another save-scoped field:

1. Add it to `NpcOverride` or the appropriate nested persisted type.
2. Preserve the existing `runID` and `GetNpcStorageKey` lookups.
3. Decide whether the value can be applied directly in `ApplyNpcOverride` or
   must be patched where the game consumes it.
4. Add a spawn/load path if the target object may not exist at save
   completion.
5. Add a prompt-time fallback if later game code can reconstruct and overwrite
   the value.
6. For mutable game objects such as routines, capture identity before applying
   edits.
7. Do not rely on `Plugin.Update`; keep the corresponding work reachable from
   the surviving static `Player.Update` postfix.
8. Do not use Unity's overloaded null comparison for the retained
   `Plugin.Instance`.
