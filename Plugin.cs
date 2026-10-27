#nullable enable

using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Silverpine.ModdingTools;
using UnityEngine;

namespace RuntimeNPCEditor;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInDependency(
    Silverpine.ModdingTools.Plugin.PluginGuid,
    BepInDependency.DependencyFlags.HardDependency)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "renegadex.silverpine.runtimenpceditor";
    public const string PluginName = "Runtime NPC Editor";
    public const string PluginVersion = "2.12.3";

    internal static Plugin Instance = null!;
    internal static ManualLogSource Log = null!;
    internal static EditorDatabase Database = new();
    internal static SaveOverrides CurrentSave = new();
    [ThreadStatic]
    private static bool suppressDescriptionOverride;

    private static readonly FieldInfo RunIdField =
        AccessTools.Field(typeof(SaveUI), "runID");
    private static readonly ConditionalWeakTable<NPCRoutine, RoutineIdentity>
        RoutineIdentities = new();
    private static readonly Dictionary<string, Dictionary<string, NPCRoutine>>
        ObservedRoutines = new();
    private static RoutineCatalogDatabase RoutineCatalog = new();
    private static readonly Dictionary<CustomContentDefinition_NPC,
        List<CustomContentDefinition_NPC.KnowledgeEntry>>
        OriginalKnowledge = new();
    private sealed class RoutineIdentity
    {
        internal string Key = "";
    }

    private ConfigEntry<KeyboardShortcut> openShortcut = null!;
    private Harmony harmony = null!;
    private string currentRunId = "";
    private ModToolSession? modToolsSession;
    private NativeNPCEditorWindow? editorWindow;
    private bool generatingKnowledgeWordClouds;
    private SaveOverrides? pendingKnowledgeSave;
    private readonly HashSet<string> unreadableSaveFiles =
        new(StringComparer.OrdinalIgnoreCase);
    private int observedLoadedNpcCount = -1;
    private int deferredOverrideApplyPasses;
    private float nextDeferredOverrideApplyTime;

    private void Awake()
    {
        Instance = this;
        Log = Logger;
        openShortcut = Config.Bind(
            "Controls",
            "OpenEditor",
            new KeyboardShortcut(KeyCode.F8),
            "Open or close the Runtime NPC Editor while a game is loaded.");
        InventoryModTools.RegisterSession(
            PluginGuid + ".editor",
            "NPC Editor",
            (inventory, session) =>
                OpenFromModTools(session),
            order: 300);

        LoadDatabase();
        LoadRoutineCatalog();
        harmony = new Harmony(PluginGuid);
        harmony.PatchAll(typeof(Plugin).Assembly);
        SerializationManager.OnFinishedLoadingSave -=
            HandleFinishedLoadingSave;
        SerializationManager.OnFinishedLoadingSave +=
            HandleFinishedLoadingSave;
    }

    public void Update()
    {
        RunManagedUpdate();
    }

    internal void RunManagedUpdate()
    {
        RefreshSaveIdentity();
        ApplyDeferredLoadedNpcOverrides();
    }

    private void ApplyDeferredLoadedNpcOverrides()
    {
        if (string.IsNullOrEmpty(currentRunId)
            || NeuralNPC.neuralNPCs == null)
        {
            return;
        }

        int loadedNpcCount = NeuralNPC.neuralNPCs.Values.Count(
            npc => npc != null);
        if (loadedNpcCount != observedLoadedNpcCount)
        {
            observedLoadedNpcCount = loadedNpcCount;
            deferredOverrideApplyPasses = 8;
            nextDeferredOverrideApplyTime = Time.unscaledTime;
        }

        if (loadedNpcCount == 0
            || deferredOverrideApplyPasses <= 0
            || Time.unscaledTime < nextDeferredOverrideApplyTime)
        {
            return;
        }

        ApplyNpcOverridesToLoadedNpcs();
        deferredOverrideApplyPasses--;
        nextDeferredOverrideApplyTime = Time.unscaledTime + 0.5f;
    }

    private void OnDestroy()
    {
        UpdateCurrentSaveMetadata();
        modToolsSession?.Close();
        modToolsSession = null;
        SaveDatabase();
    }

    private static void HandleFinishedLoadingSave()
    {
        if (ReferenceEquals(Instance, null))
        {
            Log.LogError(
                "Save loading completed "
                + "without a retained managed plugin service.");
            return;
        }
        if (ActionQueue.Instance != null)
        {
            ActionQueue.Instance.DoAfterXFrames(
                3,
                () => Instance.ApplyOverridesAfterSaveLoad(
                    "OnFinishedLoadingSave + 3 frames"));
        }
        else
        {
            Instance.ApplyOverridesAfterSaveLoad(
                "OnFinishedLoadingSave");
        }
    }

    internal void ApplyOverridesAfterSaveLoad(string source)
    {
        try
        {
            RefreshSaveIdentity();
            UpdateCurrentSaveMetadata();
            SaveDatabase();
            ApplyNpcOverridesToLoadedNpcs();
        }
        catch (Exception exception)
        {
            Log.LogError(
                "Post-save-load override "
                + $"application from {source} failed: {exception}");
        }
    }

    internal void ToggleEditor()
    {
        try
        {
            if (editorWindow != null)
            {
                CloseEditor();
                return;
            }
            if (InventoryUI.Instance == null)
            {
                Logger.LogWarning(
                    "NPC Editor cannot open before InventoryUI exists.");
                return;
            }
            BindEditorToCurrentSave();
            editorWindow = NativeNPCEditorWindow.Create(
                InventoryUI.Instance,
                CloseEditor);
        }
        catch (Exception exception)
        {
            editorWindow = null;
            Logger.LogError("Failed to open the NPC editor: " + exception);
        }
    }

    internal bool IsConfiguredTogglePressed()
    {
        return Input.GetKeyDown(KeyCode.F8)
            || openShortcut.Value.IsDown();
    }

    private void OpenFromModTools(ModToolSession session)
    {
        BindEditorToCurrentSave();
        modToolsSession?.Close();
        modToolsSession = session;
        session.Closed += () => OnModToolsSessionClosed(session);
        if (editorWindow != null)
        {
            editorWindow.BringToFront();
            return;
        }
        try
        {
            editorWindow = NativeNPCEditorWindow.Create(
                InventoryUI.Instance,
                CloseEditor);
        }
        catch
        {
            modToolsSession = null;
            session.Close();
            throw;
        }
    }

    private void OnModToolsSessionClosed(ModToolSession session)
    {
        if (!ReferenceEquals(modToolsSession, session))
            return;

        CloseEditor();
    }

    private void BindEditorToCurrentSave()
    {
        RefreshSaveIdentity(createIfMissing: true);
        UpdateCurrentSaveMetadata();
        SaveDatabase();
    }

    internal void CloseEditor()
    {
        NativeNPCEditorWindow? window = editorWindow;
        editorWindow = null;
        if (window != null)
            window.Dispose();
        ModToolSession? session = modToolsSession;
        modToolsSession = null;
        session?.Close();
        _ = GeneratePendingKnowledgeWordClouds(CurrentSave);
    }

    private async Task GeneratePendingKnowledgeWordClouds(
        SaveOverrides save)
    {
        if (generatingKnowledgeWordClouds)
        {
            pendingKnowledgeSave = save;
            return;
        }
        generatingKnowledgeWordClouds = true;
        try
        {
            foreach (KeyValuePair<string, NpcOverride> npcPair in save.npcs.ToArray())
            {
                foreach (KnowledgeOverride entry in npcPair.Value.knowledge
                             .Where(item => item.wordCloudDirty
                                 || (!item.playerNameResolvedInWordCloud
                                     && (item.entryName.Contains(
                                             "%PLAYER_NAME%")
                                         || item.lore.Contains(
                                             "%PLAYER_NAME%")))).ToArray())
                {
                    try
                    {
                        if (!IsActiveKnowledgeSave(save)
                            || InferenceServerSetupHandler.Instance == null)
                            return;
                        if (!npcPair.Value.knowledge.Contains(entry))
                            continue;
                        string npcName = npcPair.Key.StartsWith(
                            "custom:", StringComparison.Ordinal)
                            ? npcPair.Key.Substring("custom:".Length)
                            : npcPair.Key;
                        string excerpt =
                            ResolveKnowledgeVariables(
                                entry.entryName, npcName)
                            + ": "
                            + ResolveKnowledgeVariables(
                                entry.lore, npcName);
                        CustomContentDefinition_NPC.WordCloud cloud =
                            await CustomContentDefinition_NPC.WordCloud.Create(
                                excerpt,
                                word => !string.Equals(
                                    word,
                                    npcName,
                                    StringComparison.OrdinalIgnoreCase));
                        // A save switch, deletion, or replacement while awaiting
                        // inference must not update a different save/NPC/entry.
                        if (!IsActiveKnowledgeSave(save))
                            return;
                        if (!save.npcs.TryGetValue(npcPair.Key, out NpcOverride active)
                            || !ReferenceEquals(active, npcPair.Value)
                            || !active.knowledge.Contains(entry))
                            continue;
                        entry.nouns = cloud.nouns;
                        entry.verbs = cloud.verbs;
                        entry.adjectives = cloud.adjectives;
                        entry.keywords = cloud.keywords;
                        entry.wordCloudDirty = false;
                        entry.playerNameResolvedInWordCloud = true;
                        WriteSaveFile(save.saveInfo.runId, save);
                        ApplyKnowledgeForStorageKey(
                            npcPair.Key, npcPair.Value);
                    }
                    catch (Exception exception)
                    {
                        Log.LogWarning(
                            $"Knowledge word-cloud generation for "
                            + $"{npcPair.Key}/{entry.entryName} will retry: "
                            + exception.Message);
                        return;
                    }
                }
            }
        }
        finally
        {
            generatingKnowledgeWordClouds = false;
            SaveOverrides? pending = pendingKnowledgeSave;
            pendingKnowledgeSave = null;
            if (pending != null && IsActiveKnowledgeSave(pending))
                _ = GeneratePendingKnowledgeWordClouds(pending);
        }
    }

    private static bool IsActiveKnowledgeSave(SaveOverrides save) =>
        Player.Instance != null && SaveUI.Instance != null && RunIdField != null
        && ReferenceEquals(CurrentSave, save)
        && !string.IsNullOrEmpty(save.saveInfo.runId)
        && string.Equals(RunIdField.GetValue(SaveUI.Instance) as string,
            save.saveInfo.runId, StringComparison.Ordinal);

    private static void ApplyKnowledgeForStorageKey(
        string storageKey,
        NpcOverride data)
    {
        if (NeuralNPC.neuralNPCs == null)
            return;
        NeuralNPC? npc = NeuralNPC.neuralNPCs.Values.FirstOrDefault(
            candidate => candidate != null
                && GetNpcStorageKey(candidate) == storageKey);
        if (npc != null)
            ApplyKnowledgeOverrides(npc, data);
    }

    internal void RefreshSaveIdentity(bool createIfMissing = false)
    {
        if (SaveUI.Instance == null)
            return;
        if (RunIdField == null)
            return;

        string runId = RunIdField.GetValue(SaveUI.Instance) as string ?? "";
        if (string.IsNullOrEmpty(runId))
            return;
        if (runId == currentRunId)
        {
            if (Database.saves.TryGetValue(
                    runId, out SaveOverrides alreadyActive))
            {
                CurrentSave = alreadyActive;
            }
            else if (createIfMissing)
            {
                CurrentSave = new SaveOverrides();
                Database.saves[runId] = CurrentSave;
            }
            return;
        }

        // Definitions can survive a save switch. Remove this run's inserted
        // knowledge before another run uses those same definition instances.
        foreach (var original in OriginalKnowledge)
            original.Key.knowledgeEntries = original.Value.DeepClone();
        OriginalKnowledge.Clear();
        currentRunId = runId;
        observedLoadedNpcCount = -1;
        deferredOverrideApplyPasses = 8;
        nextDeferredOverrideApplyTime = Time.unscaledTime;
        if (!Database.saves.TryGetValue(runId, out SaveOverrides save))
        {
            save = new SaveOverrides();
            if (createIfMissing)
                Database.saves[runId] = save;
        }
        CurrentSave = save;
        ApplyNpcOverridesToLoadedNpcs();
    }

    internal static bool TryGetActiveOverride(
        string storageKey,
        out NpcOverride data)
    {
        data = null!;
        if (SaveUI.Instance == null || RunIdField == null)
            return false;

        string runId =
            RunIdField.GetValue(SaveUI.Instance) as string ?? "";
        if (string.IsNullOrEmpty(runId)
            || !Database.saves.TryGetValue(
                runId, out SaveOverrides activeSave))
        {
            return false;
        }

        CurrentSave = activeSave;
        return activeSave.npcs.TryGetValue(storageKey, out data);
    }

    internal static void ApplyNpcOverridesToLoadedNpcs()
    {
        if (NeuralNPC.neuralNPCs == null)
            return;

        foreach (NeuralNPC npc in NeuralNPC.neuralNPCs.Values)
            ApplyNpcOverride(npc);
    }

    internal static void ApplyNpcOverride(NeuralNPC npc)
    {
        string key = GetNpcStorageKey(npc);
        if (!TryGetActiveOverride(key, out NpcOverride data))
            return;
        if (data.personalityEnabled)
        {
            npc.personalityTraits = data.personalityTraits;
        }
        // Numeric player relationships are one-time edits to native save
        // state. Never replay them during loading or prompt synchronization.
        ApplyKnowledgeOverrides(npc, data);
        ApplyNpcRelationshipOverrides(npc, data);
    }

    internal static void ApplyKnowledgeOverrides(
        NeuralNPC npc,
        NpcOverride data)
    {
        string name = npc.GetFinalName();
        if (!CustomContentDefinition_NPC.loaded.TryGetValue(
                name, out CustomContentDefinition_NPC definition))
        {
            if (data.knowledge.Count == 0)
                return;
            definition = new CustomContentDefinition_NPC
            {
                customContentName = name,
                enabled = true,
                overrideExisting = !npc.IsCustomNPC()
            };
            CustomContentDefinition_NPC.loaded[name] = definition;
        }
        else if (!IsNativeKnowledgeDefinitionActive(npc, definition))
        {
            // Do not activate or mutate a disabled vanilla override. Its other
            // description, routine, and relationship fields must stay disabled.
            return;
        }
        if (!OriginalKnowledge.ContainsKey(definition))
            OriginalKnowledge[definition] = definition.knowledgeEntries.DeepClone();

        definition.knowledgeEntries =
            OriginalKnowledge[definition].DeepClone();
        foreach (KnowledgeOverride edited in data.knowledge)
        {
            var runtimeEntry =
                new CustomContentDefinition_NPC.KnowledgeEntry(
                    ResolveKnowledgeVariables(edited.entryName, name),
                    ResolveKnowledgeVariables(edited.lore, name),
                    edited.isPublic);
            runtimeEntry.wordCloud.nouns = edited.nouns.ToList();
            runtimeEntry.wordCloud.verbs = edited.verbs.ToList();
            runtimeEntry.wordCloud.adjectives =
                edited.adjectives.ToList();
            runtimeEntry.wordCloud.keywords = edited.keywords.ToList();
            int existingIndex = definition.knowledgeEntries.FindIndex(
                entry => KnowledgeNamesMatch(
                    entry.entryName, edited.entryName, name));
            if (existingIndex >= 0)
                definition.knowledgeEntries[existingIndex] = runtimeEntry;
            else
                definition.knowledgeEntries.Add(runtimeEntry);
        }
    }

    internal static bool IsNativeKnowledgeDefinitionActive(
        NeuralNPC npc,
        CustomContentDefinition_NPC definition) =>
        definition.enabled
        && (npc.IsCustomNPC() || definition.overrideExisting);

    internal static List<CustomContentDefinition_NPC.KnowledgeEntry>
        GetOriginalKnowledge(CustomContentDefinition_NPC definition) =>
        OriginalKnowledge.TryGetValue(definition, out var entries)
            ? entries : definition.knowledgeEntries;

    internal static bool KnowledgeNamesMatch(
        string first, string second, string npcName) =>
        EditorDataRules.KnowledgeNamesMatch(first, second,
            Player.Instance != null ? Player.Instance.playerName : "", npcName);

    private static string ResolveKnowledgeVariables(
        string value,
        string npcName) =>
        EditorDataRules.ResolveKnowledgeVariables(value,
            Player.Instance != null ? Player.Instance.playerName : "", npcName);

    internal static void ApplyNpcRelationshipOverrides(
        NeuralNPC npc,
        NpcOverride data)
    {
        if (NeuralNPC.neuralNPCs == null)
            return;
        foreach (KeyValuePair<string, RelationshipOverride> pair
                 in data.relationships)
        {
            NeuralNPC? target = NeuralNPC.neuralNPCs.Values.FirstOrDefault(
                candidate => candidate != null
                    && GetNpcStorageKey(candidate) == pair.Key);
            if (target == null || target == npc)
                continue;
            NPCRelationship relationship = (NPCRelationship)Mathf.Clamp(
                pair.Value.level, 0, 3);
            npc.relationships[target.npcName] = relationship;
            target.relationships[npc.npcName] = relationship;
            SetCustomRelationshipName(
                npc, target.npcName, pair.Value.customName);
            SetCustomRelationshipName(
                target, npc.npcName, pair.Value.customName);
        }
    }

    internal static string GetPlayerRelationshipTitleStatement(
        NeuralNPC npc)
    {
        if (npc == null || Player.Instance == null || !TryGetActiveOverride(
                GetNpcStorageKey(npc), out NpcOverride data)
            || string.IsNullOrWhiteSpace(data.playerRelationshipTitle))
        {
            return "";
        }
        string title = data.playerRelationshipTitle.Trim().TrimEnd('.');
        return Player.Instance.playerName + " is the "
            + title + " of " + npc.GetFinalName() + ".";
    }

    private static void SetCustomRelationshipName(
        NeuralNPC npc,
        NPCName target,
        string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            npc.customRelationshipNames.Remove(target);
        else
            npc.customRelationshipNames[target] = value.Trim();
    }

    internal static string GetNpcStorageKey(NeuralNPC npc) =>
        npc.IsCustomNPC()
            ? "custom:" + npc.GetFinalName()
            : npc.npcName.ToString();

    internal static string GetBaseNpcStorageKey(NPCName npcName) =>
        npcName.ToString();

    internal static string GetRoutineKey(NPCRoutine routine)
    {
        if (RoutineIdentities.TryGetValue(
                routine, out RoutineIdentity identity))
            return identity.Key;

        return CreateRoutineKey(routine);
    }

    private static string CreateRoutineKey(NPCRoutine routine)
    {
        if (!string.IsNullOrWhiteSpace(routine.id)
            && routine.id != "new_routine")
        {
            return "id:" + routine.id.Trim();
        }

        return "sig:"
            + Normalize(routine.pathingActivity) + "|"
            + Normalize(routine.activity) + "|"
            + Normalize(routine.visibleActivity);
    }

    internal static void RememberOriginalRoutineKey(NPCRoutine routine)
    {
        RoutineIdentities.Remove(routine);
        RoutineIdentities.Add(
            routine,
            new RoutineIdentity { Key = CreateRoutineKey(routine) });
    }

    internal static void RecoverSavedRoutineIdentity(
        NeuralNPC? npc, NPCRoutine? routine)
    {
        if (npc == null || routine == null
            || !TryGetActiveOverride(GetNpcStorageKey(npc), out NpcOverride data)
            || data.routines.ContainsKey(CreateRoutineKey(routine)))
            return;
        // ID-less routines serialize the edited activity text. Recover their
        // original key only if the stored edited fields identify one override.
        var matches = data.routines.Where(pair =>
            pair.Value.targetX == routine.targetPosition.x
            && pair.Value.targetY == routine.targetPosition.y
            && pair.Value.pathingActivity == routine.pathingActivity
            && pair.Value.activity == routine.activity
            && pair.Value.visibleActivity == routine.visibleActivity).ToArray();
        if (matches.Length != 1)
            return;
        RoutineIdentities.Remove(routine);
        RoutineIdentities.Add(routine,
            new RoutineIdentity { Key = matches[0].Key });
    }

    internal static void ObserveRoutine(
        string npcKey,
        NPCRoutine routine)
    {
        try
        {
            string key = CreateRoutineKey(routine);
            if (!ObservedRoutines.TryGetValue(
                    npcKey, out Dictionary<string, NPCRoutine> observed))
            {
                observed = new Dictionary<string, NPCRoutine>();
                ObservedRoutines[npcKey] = observed;
            }
            NPCRoutine clone = routine.DeepClone();
            RememberOriginalRoutineKey(clone);
            observed[key] = clone;
            if (!RoutineCatalog.npcs.TryGetValue(
                    npcKey, out Dictionary<string, string> catalog))
            {
                catalog = new Dictionary<string, string>();
                RoutineCatalog.npcs[npcKey] = catalog;
            }
            string serialized = SerializeRoutine(clone);
            if (!catalog.TryGetValue(key, out string existing)
                || existing != serialized)
            {
                catalog[key] = serialized;
                SaveRoutineCatalog();
            }
        }
        catch (Exception exception)
        {
            Log.LogWarning(
                $"Could not remember routine for {npcKey}: "
                + exception.Message);
        }
    }

    internal static void ObserveActiveBaseRoutine(
        NPCRoutineExecutor executor,
        NPCRoutine? routine)
    {
        try
        {
            NeuralNPC? npc = executor.neuralNPC;
            if (npc == null
                || routine == null
                || executor.IsCurrentRoutineOverrideRoutine()
                || IsEditorModifiedRoutine(npc, routine))
            {
                return;
            }

            RememberOriginalRoutineKey(routine);
            ObserveRoutine(GetNpcStorageKey(npc), routine);
        }
        catch (Exception exception)
        {
            Log.LogWarning(
                "Could not inspect an active routine: "
                + exception.Message);
        }
    }

    private static bool IsEditorModifiedRoutine(
        NeuralNPC npc,
        NPCRoutine routine)
    {
        if (!TryGetActiveOverride(
                GetNpcStorageKey(npc), out NpcOverride npcOverride))
        {
            return false;
        }

        string key = GetRoutineKey(routine);
        if (npcOverride.routines.ContainsKey(key))
            return true;

        return npcOverride.routines.Values.Any(edited =>
            edited.targetX == routine.targetPosition.x
            && edited.targetY == routine.targetPosition.y
            && edited.pathingActivity == routine.pathingActivity
            && edited.activity == routine.activity
            && edited.visibleActivity == routine.visibleActivity);
    }

    internal static List<NPCRoutine> GetObservedRoutines(NeuralNPC npc)
    {
        string npcKey = GetNpcStorageKey(npc);
        if (!ObservedRoutines.TryGetValue(
                npcKey, out Dictionary<string, NPCRoutine> observed))
            return new List<NPCRoutine>();
        return observed.Values.Select(routine =>
        {
            NPCRoutine clone = routine.DeepClone();
            RememberOriginalRoutineKey(clone);
            return clone;
        }).ToList();
    }

    internal static string AnalyzeAndRecordRoutine(
        NeuralNPC npc,
        NPCRoutine routine,
        bool isActive)
    {
        NPCRoutineExecutor? executor =
            npc.GetComponent<NPCRoutineExecutor>();
        if (isActive
            && executor != null
            && executor.IsCurrentRoutineOverrideRoutine())
        {
            return "Not recorded: this is a temporary/scripted override "
                + "routine, not the NPC's normal schedule.";
        }

        if (IsEditorModifiedRoutine(npc, routine))
        {
            return "Not recorded: this routine contains an NPC Editor "
                + "override, so it is not clean base/custom-content data.";
        }

        string npcKey = GetNpcStorageKey(npc);
        string routineKey = CreateRoutineKey(routine);
        string serialized;
        try
        {
            serialized = SerializeRoutine(routine);
        }
        catch (Exception exception)
        {
            return "Not recorded: the routine could not be serialized ("
                + exception.Message + ").";
        }

        if (RoutineCatalog.npcs.TryGetValue(
                npcKey, out Dictionary<string, string> catalog)
            && catalog.TryGetValue(routineKey, out string existing)
            && existing == serialized)
        {
            return "Catalog check: this clean routine is already in the "
                + "permanent record.";
        }

        bool updatingExisting =
            catalog != null && catalog.ContainsKey(routineKey);
        ObserveRoutine(npcKey, routine);
        return updatingExisting
            ? "Catalog check: the routine was clean and its permanent "
                + "record was updated."
            : "Catalog check: the routine was clean and has been added "
                + "to the permanent record.";
    }

    internal static bool TryGetOriginalRoutine(
        NeuralNPC npc,
        string key,
        out NPCRoutine routine)
    {
        routine = null!;
        string npcKey = GetNpcStorageKey(npc);
        if (!ObservedRoutines.TryGetValue(
                npcKey, out Dictionary<string, NPCRoutine> observed)
            || !observed.TryGetValue(key, out NPCRoutine original))
            return false;
        routine = original.DeepClone();
        RememberOriginalRoutineKey(routine);
        return true;
    }

    private static string RoutineCatalogPath =>
        Path.Combine(
            Paths.ConfigPath,
            "RuntimeNPCEditor",
            "routine-catalog.json");

    private static string SerializeRoutine(NPCRoutine routine)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        routine.Serialize(writer);
        writer.Flush();
        return Convert.ToBase64String(stream.ToArray());
    }

    private static NPCRoutine DeserializeRoutine(string encoded)
    {
        byte[] bytes = Convert.FromBase64String(encoded);
        using var stream = new MemoryStream(bytes);
        using var reader = new BinaryReader(stream);
        var routine = new NPCRoutine();
        routine.Deserialize(reader);
        return routine;
    }

    private static void LoadRoutineCatalog()
    {
        try
        {
            if (File.Exists(RoutineCatalogPath))
            {
                RoutineCatalog =
                    JsonConvert.DeserializeObject<RoutineCatalogDatabase>(
                        File.ReadAllText(RoutineCatalogPath))
                    ?? new RoutineCatalogDatabase();
            }

            foreach (KeyValuePair<string, Dictionary<string, string>> npcEntry
                     in RoutineCatalog.npcs)
            {
                var observed = new Dictionary<string, NPCRoutine>();
                foreach (KeyValuePair<string, string> routineEntry
                         in npcEntry.Value)
                {
                    try
                    {
                        NPCRoutine routine =
                            DeserializeRoutine(routineEntry.Value);
                        RememberOriginalRoutineKey(routine);
                        observed[routineEntry.Key] = routine;
                    }
                    catch (Exception exception)
                    {
                        Log.LogWarning(
                            $"Could not load catalog routine "
                            + $"{npcEntry.Key}/{routineEntry.Key}: "
                            + exception.Message);
                    }
                }
                if (observed.Count > 0)
                    ObservedRoutines[npcEntry.Key] = observed;
            }
        }
        catch (Exception exception)
        {
            Log.LogError(
                "Could not load the routine catalog: " + exception);
            RoutineCatalog = new RoutineCatalogDatabase();
        }
    }

    private static void SaveRoutineCatalog()
    {
        try
        {
            Directory.CreateDirectory(
                Path.GetDirectoryName(RoutineCatalogPath));
            AtomicFile.WriteAllText(
                RoutineCatalogPath,
                JsonConvert.SerializeObject(
                    RoutineCatalog,
                    Formatting.Indented));
        }
        catch (Exception exception)
        {
            Log.LogError(
                "Could not save the routine catalog: " + exception);
        }
    }

    private static string Normalize(string value) =>
        (value ?? "").Trim().ToLowerInvariant();

    internal static void ApplyRoutineOverride(
        NPCName npcName,
        NPCRoutine routine)
    {
        if (!TryGetActiveOverride(
                npcName.ToString(), out NpcOverride npc))
            return;

        string key = GetRoutineKey(routine);
        if (!npc.routines.TryGetValue(key, out RoutineOverride data))
            return;

        routine.targetPosition = new Vector2Int(data.targetX, data.targetY);
        routine.pathingActivity = data.pathingActivity;
        routine.activity = data.activity;
        routine.visibleActivity = data.visibleActivity;
    }

    internal static void ApplyRoutineOverride(
        NeuralNPC npc,
        NPCRoutine routine)
    {
        if (!TryGetActiveOverride(
                GetNpcStorageKey(npc), out NpcOverride npcOverride))
            return;

        string key = GetRoutineKey(routine);
        if (!npcOverride.routines.TryGetValue(
                key, out RoutineOverride data))
            return;

        routine.targetPosition = new Vector2Int(data.targetX, data.targetY);
        routine.pathingActivity = data.pathingActivity;
        routine.activity = data.activity;
        routine.visibleActivity = data.visibleActivity;
    }

    internal static string GetOriginalResolvedDescription(
        NeuralNPC npc,
        bool isPrivate)
    {
        suppressDescriptionOverride = true;
        try
        {
            string result = npc.GetFinalCharacterDescription(isPrivate);
            int personalityIndex = result.LastIndexOf(
                " Personality:",
                StringComparison.Ordinal);
            return (personalityIndex >= 0
                    ? result.Substring(0, personalityIndex)
                    : result)
                .Trim();
        }
        finally
        {
            suppressDescriptionOverride = false;
        }
    }

    internal static bool SuppressDescriptionOverride =>
        suppressDescriptionOverride;

    internal static void Commit()
    {
        Instance.RefreshSaveIdentity(createIfMissing: true);
        Instance.UpdateCurrentSaveMetadata();
        // Unlike background persistence, a user action must surface failure
        // instead of displaying a success message after an unsuccessful write.
        Instance.WriteSaveFile(Instance.currentRunId, CurrentSave);
    }

    private void UpdateCurrentSaveMetadata()
    {
        if (string.IsNullOrEmpty(currentRunId))
            return;

        SaveInfo info = CurrentSave.saveInfo;
        info.runId = currentRunId;
        info.lastSeenLocal = DateTimeOffset.Now.ToString("O");
        info.npcOverrideCount = CurrentSave.npcs.Count;

        if (Player.Instance != null)
        {
            info.playerName = Player.Instance.playerName ?? "";
            try
            {
                info.location = MapZone.GetPositionZoneName(
                    Player.Instance.transform.GetVector2IntPosition());
            }
            catch
            {
                info.location = "";
            }
        }

        if (WorldInfoManager.Instance != null)
        {
            try
            {
                info.inGameDay =
                    WorldInfoManager.Instance.GetCurrentDay() + 1;
                info.inGameTime =
                    WorldInfoManager.Instance.GetCurrentHourOfDayAMPM();
                info.season =
                    WorldInfoManager.Instance.GetSeasonString();
            }
            catch
            {
                info.inGameTime = "";
                info.season = "";
            }
        }
    }

    private string EditorConfigDirectory =>
        Path.Combine(Paths.ConfigPath, "RuntimeNPCEditor");

    private string SavesDirectory =>
        Path.Combine(EditorConfigDirectory, "saves");

    private string LegacyDatabasePath =>
        Path.Combine(EditorConfigDirectory, "overrides.json");

    private string LegacyBackupDirectory =>
        Path.Combine(EditorConfigDirectory, "legacy");

    private void LoadDatabase()
    {
        Database = new EditorDatabase();
        try
        {
            Directory.CreateDirectory(SavesDirectory);
            try
            {
                MigrateLegacyDatabase();
            }
            catch (Exception exception)
            {
                Log.LogError("Could not migrate legacy overrides; continuing "
                    + "to load per-save files: " + exception);
            }
            foreach (string path in Directory.GetFiles(
                         SavesDirectory, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    SaveOverrides save = NormalizeSave(
                        JsonConvert.DeserializeObject<SaveOverrides>(
                            File.ReadAllText(path))
                        ?? throw new InvalidDataException(
                            "The save override file is empty."));
                    string runId = string.IsNullOrWhiteSpace(save.saveInfo.runId)
                        ? Path.GetFileNameWithoutExtension(path)
                        : save.saveInfo.runId;
                    save.saveInfo.runId = runId;
                    Database.saves[runId] = save;
                }
                catch (Exception exception)
                {
                    unreadableSaveFiles.Add(Path.GetFullPath(path));
                    Log.LogError(
                        $"Could not load save override file '{path}': "
                        + exception);
                }
            }
        }
        catch (Exception exception)
        {
            Log.LogError(
                "Could not load Runtime NPC Editor overrides: "
                + exception);
        }
    }

    private void SaveDatabase()
    {
        if (string.IsNullOrWhiteSpace(currentRunId)
            || !Database.saves.TryGetValue(
                currentRunId, out SaveOverrides save))
        {
            return;
        }

        try
        {
            WriteSaveFile(currentRunId, save);
        }
        catch (Exception exception)
        {
            Log.LogError(
                $"Could not save Runtime NPC Editor overrides for "
                + $"'{currentRunId}': "
                + exception);
        }
    }

    private void MigrateLegacyDatabase()
    {
        if (!File.Exists(LegacyDatabasePath))
            return;

        EditorDatabase legacy =
            JsonConvert.DeserializeObject<EditorDatabase>(
                File.ReadAllText(LegacyDatabasePath))
            ?? throw new InvalidDataException(
                "The legacy override database is empty.");

        foreach (KeyValuePair<string, SaveOverrides> pair in legacy.saves)
        {
            SaveOverrides save = NormalizeSave(pair.Value);
            if (IsEmptyTransientSave(save))
                continue;

            save.saveInfo.runId = pair.Key;
            string path = GetSavePath(pair.Key);
            if (!File.Exists(path))
                WriteSaveFile(pair.Key, save);
        }

        Directory.CreateDirectory(LegacyBackupDirectory);
        string backupPath = Path.Combine(
            LegacyBackupDirectory,
            "overrides-pre-2.8.0.json");
        if (File.Exists(backupPath))
        {
            backupPath = Path.Combine(
                LegacyBackupDirectory,
                "overrides-pre-2.8.0-"
                + DateTime.Now.ToString("yyyyMMdd-HHmmss")
                + ".json");
        }
        File.Move(LegacyDatabasePath, backupPath);
        Log.LogInfo(
            "Migrated Runtime NPC Editor overrides to one JSON file per "
            + $"run ID. Legacy backup: {backupPath}");
    }

    private void WriteSaveFile(string runId, SaveOverrides save)
    {
        Directory.CreateDirectory(SavesDirectory);
        save = NormalizeSave(save);
        save.saveInfo.runId = runId;
        string path = GetSavePath(runId);
        if (unreadableSaveFiles.Contains(Path.GetFullPath(path)))
            throw new InvalidDataException(
                "Refusing to overwrite an unreadable override file: " + path
                + ". Repair or restore it from backup and restart the game.");
        AtomicFile.WriteAllText(
            path,
            JsonConvert.SerializeObject(save, Formatting.Indented));
    }

    private string GetSavePath(string runId)
    {
        string safeName = runId;
        foreach (char invalid in Path.GetInvalidFileNameChars())
            safeName = safeName.Replace(invalid, '_');
        if (string.IsNullOrWhiteSpace(safeName))
            throw new InvalidDataException("The save run ID is empty.");
        return Path.Combine(SavesDirectory, safeName + ".json");
    }

    private static SaveOverrides NormalizeSave(SaveOverrides save)
    {
        save.saveInfo ??= new SaveInfo();
        save.npcs ??= new Dictionary<string, NpcOverride>();
        // Disable legacy numeric locks. Old numbers remain only in JSON;
        // the UI reads live NPC values and background hooks never replay them.
        foreach (NpcOverride npc in save.npcs.Values)
        {
            if (npc == null || npc.knowledge == null || npc.relationships == null
                || npc.routines == null || npc.publicDescription == null
                || npc.privateDescription == null || npc.personalityTraits == null
                || npc.knowledge.Any(entry => entry == null || entry.entryName == null
                    || entry.lore == null || entry.nouns == null || entry.verbs == null
                    || entry.adjectives == null || entry.keywords == null)
                || npc.relationships.Values.Any(relation => relation == null)
                || npc.routines.Values.Any(routine => routine == null))
                throw new InvalidDataException("An NPC override contains null data.");
            npc.playerRelationshipEnabled = false;
        }
        return save;
    }

    private static bool IsEmptyTransientSave(SaveOverrides save) =>
        save.npcs.Count == 0
        && string.IsNullOrWhiteSpace(save.saveInfo.playerName)
        && string.IsNullOrWhiteSpace(save.saveInfo.lastSeenLocal)
        && save.saveInfo.inGameDay == 0
        && string.IsNullOrWhiteSpace(save.saveInfo.inGameTime)
        && string.IsNullOrWhiteSpace(save.saveInfo.season)
        && string.IsNullOrWhiteSpace(save.saveInfo.location);
}

public sealed class EditorDatabase
{
    public Dictionary<string, SaveOverrides> saves = new();
}

public sealed class SaveOverrides
{
    public SaveInfo saveInfo = new();
    public Dictionary<string, NpcOverride> npcs = new();
}

public sealed class SaveInfo
{
    public string runId = "";
    public string playerName = "";
    public string lastSeenLocal = "";
    public int inGameDay;
    public string inGameTime = "";
    public string season = "";
    public string location = "";
    public int npcOverrideCount;
}

public sealed class RoutineCatalogDatabase
{
    public Dictionary<string, Dictionary<string, string>> npcs = new();
}

public sealed class NpcOverride
{
    public bool descriptionEnabled;
    public string publicDescription = "";
    public string privateDescription = "";
    public bool personalityEnabled;
    public string personalityTraits = "";
    public bool playerRelationshipEnabled;
    public int playerRelationshipLevel;
    public int playerRelationshipDailyCap;
    public int playerRelationshipTotalCap;
    public string playerRelationshipTitle = "";
    public List<KnowledgeOverride> knowledge = new();
    public Dictionary<string, RelationshipOverride> relationships = new();
    public Dictionary<string, RoutineOverride> routines = new();
}

public sealed class KnowledgeOverride
{
    public string entryName = "";
    public string lore = "";
    public bool isPublic;
    public bool wordCloudDirty = true;
    public bool playerNameResolvedInWordCloud;
    public List<string> nouns = new();
    public List<string> verbs = new();
    public List<string> adjectives = new();
    public List<string> keywords = new();
}

public sealed class RelationshipOverride
{
    public int level;
    public string customName = "";
}

public sealed class RoutineOverride
{
    public int targetX;
    public int targetY;
    public string pathingActivity = "";
    public string activity = "";
    public string visibleActivity = "";
    public bool hasOriginal;
    public int originalTargetX;
    public int originalTargetY;
    public string originalPathingActivity = "";
    public string originalActivity = "";
    public string originalVisibleActivity = "";
}

[HarmonyPatch(typeof(NPCRoutineLibrary), nameof(NPCRoutineLibrary.GetNPCRoutine))]
internal static class CustomRoutineSelectorGuardPatch
{
    private const string GuardMarker =
        "-- Runtime NPC Editor non-string routine guard";

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void GuardCustomRoutineSelector(NPCName __1)
    {
        try
        {
            if (NeuralNPC.neuralNPCs == null ||
                !NeuralNPC.neuralNPCs.TryGetValue(__1, out NeuralNPC npc) ||
                npc == null ||
                !npc.TryGetComponent(out CustomNPCHandler handler) ||
                handler == null ||
                !CustomContentDefinition_NPC.loaded.TryGetValue(
                    handler.customContentName,
                    out CustomContentDefinition_NPC definition))
                return;

            string script = definition.getTargetRoutineIDLuaScript;
            if (string.IsNullOrWhiteSpace(script) ||
                script.Contains(GuardMarker))
                return;

            definition.getTargetRoutineIDLuaScript = script + "\n\n" +
                GuardMarker + "\n" +
                "local __runtimeNPCEditorOriginalGetTargetRoutineID = " +
                "GetTargetRoutineID\n" +
                "function GetTargetRoutineID(hour)\n" +
                "    local result = " +
                "__runtimeNPCEditorOriginalGetTargetRoutineID(hour)\n" +
                "    if type(result) == \"string\" then\n" +
                "        return result\n" +
                "    end\n" +
                "    return \"silverpine_leisure\"\n" +
                "end";
        }
        catch (Exception exception)
        {
            Plugin.Log.LogWarning(
                "Could not install the custom routine return guard: " +
                exception.Message);
        }
    }
}

[HarmonyPatch(typeof(NPCRoutineLibrary), nameof(NPCRoutineLibrary.GetNPCRoutine))]
internal static class RoutinePatch
{
    [HarmonyPostfix]
    private static void ApplyEditedRoutine(
        NPCName __1,
        ref NPCRoutine __result)
    {
        if (__result != null)
        {
            Plugin.RememberOriginalRoutineKey(__result);
            if (NeuralNPC.neuralNPCs != null
                && NeuralNPC.neuralNPCs.TryGetValue(
                    __1, out NeuralNPC npc)
                && npc != null)
            {
                Plugin.ObserveRoutine(
                    Plugin.GetNpcStorageKey(npc), __result);
                Plugin.ApplyRoutineOverride(npc, __result);
            }
            else if (__1 <= NPCName.Orson)
            {
                string baseNpcKey = Plugin.GetBaseNpcStorageKey(__1);
                Plugin.ObserveRoutine(baseNpcKey, __result);
                Plugin.ApplyRoutineOverride(__1, __result);
            }
        }
    }
}

[HarmonyPatch(
    typeof(NPCRoutineExecutor),
    nameof(NPCRoutineExecutor.StartNPCRoutine))]
internal static class ActiveRoutineStartPatch
{
    [HarmonyPrefix]
    private static void RememberActiveBaseRoutine(
        NPCRoutineExecutor __instance,
        NPCRoutine newRoutine)
    {
        Plugin.ObserveActiveBaseRoutine(__instance, newRoutine);
    }
}

[HarmonyPatch(
    typeof(NPCRoutineExecutor),
    nameof(NPCRoutineExecutor.Deserialize))]
internal static class ActiveRoutineDeserializePatch
{
    [HarmonyPostfix]
    private static void RememberLoadedActiveBaseRoutine(
        NPCRoutineExecutor __instance)
    {
        Plugin.RecoverSavedRoutineIdentity(
            __instance.neuralNPC, __instance.currentRoutine);
        Plugin.ObserveActiveBaseRoutine(
            __instance, __instance.currentRoutine);
        if (__instance.neuralNPC != null
            && __instance.currentRoutine != null)
        {
            Plugin.ApplyRoutineOverride(
                __instance.neuralNPC,
                __instance.currentRoutine);
        }
    }
}

[HarmonyPatch(
    typeof(NeuralNPC),
    nameof(NeuralNPC.GetFinalCharacterDescription))]
internal static class DescriptionPatch
{
    [HarmonyPostfix]
    private static void ApplyEditedDescription(
        NeuralNPC __instance,
        bool isPrivate,
        ref string __result)
    {
        if (!Plugin.TryGetActiveOverride(
                Plugin.GetNpcStorageKey(__instance), out NpcOverride data)
            || Plugin.SuppressDescriptionOverride
            || !data.descriptionEnabled)
        {
            return;
        }

        string description = isPrivate
            ? data.privateDescription
            : data.publicDescription;
        if (string.IsNullOrWhiteSpace(description))
            description = data.publicDescription;

        __result = description
            .Replace("%PLAYER_NAME%", Player.Instance.playerName)
            .Replace("%NAME%", __instance.GetFinalName())
            + " Personality: "
            + __instance.personalityTraits
            + ".";
    }
}

[HarmonyPatch(typeof(NeuralNPC), "Start")]
internal static class NpcStartPatch
{
    [HarmonyPostfix]
    private static void ApplyWhenNpcStarts(NeuralNPC __instance)
    {
        Plugin.ApplyNpcOverride(__instance);
    }
}

[HarmonyPatch(typeof(CustomNPCManager), "SpawnInNPCs")]
internal static class CustomNpcRegistrationPatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void ReapplyAfterCustomNpcRegistration()
    {
        Plugin.Instance.ApplyOverridesAfterSaveLoad(
            "CustomNPCManager.SpawnInNPCs");
    }
}

[HarmonyPatch(
    typeof(Lorebook),
    nameof(Lorebook.GetConditionalLore))]
internal static class KnowledgeSynchronizationPatch
{
    [HarmonyPrefix]
    private static void SynchronizeLiveKnowledge(NPCName npcName)
    {
        Plugin.Instance.RefreshSaveIdentity();
        if (NeuralNPC.neuralNPCs != null
            && NeuralNPC.neuralNPCs.TryGetValue(
                npcName, out NeuralNPC npc)
            && npc != null)
        {
            Plugin.ApplyNpcOverride(npc);
        }
    }
}

[HarmonyPatch(typeof(NeuralNPC), "GetRelationshipString")]
internal static class PlayerRelationshipTitlePromptPatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void ApplyCustomPlayerTitle(
        NeuralNPC __instance,
        ref string __result)
    {
        string title =
            Plugin.GetPlayerRelationshipTitleStatement(__instance);
        if (!string.IsNullOrWhiteSpace(title))
            __result = title;
    }
}

[HarmonyPatch(
    typeof(NeuralNPC),
    nameof(NeuralNPC.GetCombinedPrompt))]
internal static class PlayerRelationshipTitleCombinedPromptPatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void ReplaceRelationshipInCompletedPrompt(
        NeuralNPC __instance,
        ref Task<string> __result)
    {
        __result = ReplaceRelationshipAsync(__instance, __result);
    }

    private static async Task<string> ReplaceRelationshipAsync(
        NeuralNPC npc,
        Task<string> originalPromptTask)
    {
        string prompt = await originalPromptTask;
        string title = Plugin.GetPlayerRelationshipTitleStatement(npc);
        if (string.IsNullOrWhiteSpace(title)
            || Player.Instance == null)
        {
            return prompt;
        }

        string playerSection =
            "This is the character description of "
            + Player.Instance.playerName
            + ", the player's character:\n"
            + Player.Instance.characterDescription;
        int relationshipStart = prompt.IndexOf(
            playerSection, StringComparison.Ordinal);
        if (relationshipStart >= 0)
        {
            relationshipStart += playerSection.Length;
            string npcSection =
                "\n\nThis is the character description of "
                + npc.GetFinalName()
                + ":\n";
            int relationshipEnd = prompt.IndexOf(
                npcSection,
                relationshipStart,
                StringComparison.Ordinal);
            if (relationshipEnd >= relationshipStart)
            {
                prompt =
                    prompt.Substring(0, relationshipStart)
                    + "\n"
                    + title
                    + prompt.Substring(relationshipEnd);
            }
        }
        return prompt;
    }
}

[HarmonyPatch(typeof(Player), "Update")]
internal static class PlayerInputPatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void PollNpcEditorShortcut()
    {
        if (ReferenceEquals(Plugin.Instance, null))
        {
            return;
        }

        Plugin.Instance.RunManagedUpdate();
        if (Plugin.Instance.IsConfiguredTogglePressed())
            Plugin.Instance.ToggleEditor();
    }
}
