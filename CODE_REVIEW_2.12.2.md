# Runtime NPC Editor 2.12.2 code review

Reviewed 2026-09-17. Scope: all Runtime NPC Editor production source files,
project configuration, README and injection map; relevant Silverpine 1.7.3
relationship/routine/serialization code; Modding Tools session behavior;
and the optional Dynamic NPC Relationships API signatures used by the bridge.
This is a static source review plus automated checks, not an in-game playtest.

## Confirmed findings fixed

| Priority | Finding and consequence | Correction |
| --- | --- | --- |
| High | `ApplyNpcOverride` replayed saved level and caps during loading, NPC registration and conditional-lore retrieval. Earned progress was reset. | Numeric writes exist only in the explicit Apply and cap-repair UI actions. Existing numeric locks are disabled on load. Native game saves own numeric persistence. |
| High | A pending word-cloud response could finish after a save switch and synchronize old knowledge into the currently loaded NPC. Iterating mutable collections across `await` could also abort generation. | Snapshot work lists; validate active run and entry ownership after each response; discard obsolete results; queue a subsequent close request without concurrent inference. |
| High | Original knowledge was cached by display name for the process lifetime, allowing another definition instance/run to receive an old baseline. | Cache by definition instance; restore originals and clear the cache when the run ID changes. Read-only override lookups no longer bypass that transition. |
| Medium | The same NPC pair could have contradictory overrides saved in both directions. Removing one allowed the other to reapply. | Saving consolidates the pair to one stored direction; removal clears both directions. Dynamic resume/enrollment refuses an outstanding manual pair override. |
| Medium | Relationship actions rebuilt the NPC list and reused an old numeric index. A changed list could affect the wrong target or throw. | Actions retain and validate the displayed NPC instance. |
| Medium | Unrestricted total caps could enter values Silverpine's progression code cannot handle. Daily cap was clamped below the native level + 10 ceiling at high levels. | Total-cap dropdown offers 14/49/79/100; daily values support 0-110. All numeric edits remain one-time changes. |
| Medium | Clicking New Entry and then Delete indexed one beyond the saved knowledge list. New Entry also left the previous visibility label displayed. | Validate saved selection bounds; immediately refresh the private/public indicator. |
| Medium | Renaming knowledge reused the previous word cloud even though entry names contribute to the excerpt. Placeholder names could duplicate resolved entries in the UI. | Treat name edits as text edits; merge names after variable resolution; build the editor list from original definitions plus overrides. |
| Medium | Deserialized ID-less routines could lose their original identity after an activity override, preventing later edits/removal from finding it. | Recover the key when edited fields uniquely identify a saved override. |
| Medium | A stale ACTIVE routine row could restart an old routine after the NPC had already moved to another activity. Disabled vanilla override definitions also appeared in the routine list. | Validate the active routine instance before restarting; filter inactive definitions. |
| Medium | Interrupting the world picker could leave the parent menu/input session in world-input mode. UI construction failures could leave an overlay behind. | Add idempotent picker cleanup on completion/disposal/destruction; destroy partially constructed overlays. |
| Medium | Direct JSON writes could truncate a file on failure. A failed load could later be overwritten by a new empty scope. Broken legacy migration blocked valid split files. UI actions could announce success after a failed write. | Write using atomic replacement and a previous-file `.bak`; protect unreadable per-save paths; isolate legacy migration failures; surface explicit commit failures in the UI. |

## Remaining findings and limits

- **Medium, existing routine identity limitation:** two routines without IDs
  and with identical travel/activity/visible text share one signature even if
  their coordinates or arguments differ. The game includes variants such as
  Gareth's guest-room cleaning. Editing that signature affects the group.
  Changing the key format requires a separate catalog/override migration;
  this release preserves existing keys. Uniquely named routine IDs avoid it.
- **Medium, compatibility limitation:** original knowledge is a snapshot.
  Another mod changing the same definition's knowledge after the editor has
  captured it can have those changes replaced by a later synchronization.
  Definition-instance/run isolation is fixed, but concurrent writers to the
  same instance still need an ownership/merge protocol.
- **Existing behavior:** disabling a personality override restores authored
  traits after reload, not immediately. The game does not serialize this
  field; the UI says reload is required. No historical traits snapshot exists.
- **Existing data limitation:** old releases did not preserve original player
  levels/caps or unmanaged NPC-pair values. Removing enforcement cannot undo
  a value already written into a native save. Player cap repair uses the
  game's legacy-save tier rules and retains the current level and title.
- **Expected compatibility boundary:** an explicitly configured player title
  replaces the relationship portion of the prompt. With no title configured,
  the title hooks return the existing prompt. Integration with other mods
  using that same prompt region still requires an in-game check.
- Lua return guarding preserves valid string results and falls back for
  non-string results; it does not repair arbitrary Lua runtime errors.

## Verification

- Release build against the local game and Modding Tools references.
- Dependency-free regression harness: 741 checks covering native cap ranges
  and stage boundaries, unsaved-entry bounds, variable-name matching, rename
  invalidation, atomic replacement/backups, and failure cleanup.
- Compiled-assembly inspection using Mono.Cecil checks every method including
  generated async/lambda bodies. Only Apply writes all three player numbers;
  only explicit cap repair writes the two caps; no background hook writes
  these fields or re-enables a legacy numeric lock.
- The bridge's current argument order matches the local Dynamic Relationships
  API. Its optional reflection-only dependency is retained.

Run after building the plugin:

```powershell
dotnet run --project tests/RegressionTests.csproj -c Release -- bin/Release/netstandard2.1/RuntimeNPCEditor.dll
```

The test project uses the game root three directories above `tests` by default;
override `GameRoot` if using a different checkout layout. The compiled-assembly
checks do not execute Unity hooks or the native UI.

## In-game verification still required

1. Upgrade a save with a numeric override, open the editor, verify live values,
   apply new numbers, converse, advance a day, save, and reload. Values must
   follow game progression rather than reverting to the editor's old JSON.
2. Select each total cap in the dropdown; test placement at 1080p and 4K.
3. Add/rename/delete knowledge, reopen the editor during generation, and switch
   runs while a request is pending. Check both run files and conversation lore.
4. Remove an NPC-pair override from the opposite NPC and resume Dynamic control.
5. Edit/remove/reload an ID-less active routine; interrupt tile picking using
   Escape, F8, and session closure. Check menus and input recover correctly.

No live game was launched or player save modified as part of these checks.
