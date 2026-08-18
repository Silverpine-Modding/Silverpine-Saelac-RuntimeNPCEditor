#nullable enable

using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace RuntimeNPCEditor;

internal sealed class NativeNPCEditorWindow : MonoBehaviour
{
    private enum EditorTab
    {
        Description,
        Personality,
        Knowledge,
        Relationships,
        DynamicRelationships,
        Routines
    }

    private sealed class RoutineChoice
    {
        internal int Hour;
        internal NPCRoutine Routine = null!;
        internal bool Active;
    }

    private static readonly string[] DynamicStagesWithEnemies =
    {
        "Enemies",
        "Stranger",
        "Acquaintance",
        "Friend",
        "Lover",
        "Married"
    };

    private static readonly string[] LegacyDynamicStages =
    {
        "Stranger",
        "Acquaintance",
        "Friend",
        "Lover",
        "Married"
    };

    private readonly List<GameObject> pageObjects = new();
    private readonly List<Button> npcButtons = new();
    private readonly List<Button> routineButtons = new();
    private readonly List<RoutineChoice> routines = new();
    private readonly List<Button> knowledgeButtons = new();
    private readonly List<Button> relationshipButtons = new();
    private Action close = null!;
    private InventoryUI inventory = null!;
    private Button buttonTemplate = null!;
    private TMP_InputField inputTemplate = null!;
    private TMP_FontAsset font = null!;
    private RectTransform panel = null!;
    private RectTransform content = null!;
    private TextMeshProUGUI title = null!;
    private TextMeshProUGUI status = null!;
    private NeuralNPC? npc;
    private EditorTab tab;
    private int npcPage;
    private int routinePage;
    private int selectedRoutine;
    private int selectedKnowledge;
    private int selectedRelationship;
    private int knowledgePage;
    private int relationshipPage;
    private bool knowledgeIsPublic;
    private string selectedKnowledgeOriginalName = "";
    private int relationshipLevel;
    private TMP_InputField? publicDescription;
    private TMP_InputField? privateDescription;
    private TMP_InputField? personality;
    private TMP_InputField? targetX;
    private TMP_InputField? targetY;
    private TMP_InputField? pathingActivity;
    private TMP_InputField? activity;
    private TMP_InputField? visibleActivity;
    private TMP_InputField? knowledgeName;
    private TMP_InputField? knowledgeLore;
    private TMP_InputField? playerRelationshipLevel;
    private TMP_InputField? playerRelationshipDailyCap;
    private TMP_InputField? playerRelationshipTotalCap;
    private TMP_InputField? playerRelationshipTitle;
    private TMP_InputField? customRelationshipName;
    private TMP_InputField? dynamicRelationshipScore;
    private Button? knowledgeVisibilityButton;
    private Button? dynamicRelationshipStageButton;
    private Button? dynamicRelationshipLockButton;
    private string dynamicRelationshipStage = "";
    private bool dynamicRelationshipTierLocked;
    private bool dynamicRelationshipHasEnemies;

    internal static NativeNPCEditorWindow Create(
        InventoryUI inventory,
        Action close)
    {
        Button template = Traverse.Create(inventory)
            .Field("useButton").GetValue<Button>();
        TMP_InputField input = Traverse.Create(DialogBox.Instance)
            .Field("inputField").GetValue<TMP_InputField>();
        if (template == null || input == null)
            throw new InvalidOperationException(
                "Silverpine UI templates were not available.");

        GameObject root =
            Silverpine.ModdingTools.ModUi.CreateOverlayCanvas(
                "RuntimeNPCEditorNative");
        root.AddComponent<CanvasGroup>();

        NativeNPCEditorWindow window =
            root.AddComponent<NativeNPCEditorWindow>();
        window.inventory = inventory;
        window.close = close;
        window.buttonTemplate = template;
        window.inputTemplate = input;
        window.font = template
            .GetComponentInChildren<TextMeshProUGUI>(true).font;
        window.Build();
        return window;
    }

    internal void BringToFront() => transform.SetAsLastSibling();

    internal void Dispose()
    {
        if (this != null && gameObject != null)
            Destroy(gameObject);
    }

    private void Build()
    {
        Image dim = gameObject.AddComponent<Image>();
        dim.color = new Color(0f, 0f, 0f, 0.72f);

        panel = CreatePanel(
            transform,
            "EditorPanel",
            new Vector2(0.5f, 0.5f),
            new Vector2(1560f, 900f));
        Image panelImage = panel.gameObject.AddComponent<Image>();
        Image templateImage = buttonTemplate.GetComponent<Image>();
        panelImage.sprite = templateImage.sprite;
        panelImage.type = templateImage.type;
        panelImage.color = new Color(0.16f, 0.18f, 0.2f, 0.99f);

        title = CreateText(
            panel,
            "NPC Editor",
            new Vector2(0f, 410f),
            new Vector2(900f, 55f),
            34,
            TextAlignmentOptions.Center);
        CreateButton(
            panel,
            "Close",
            new Vector2(690f, 410f),
            new Vector2(150f, 50f),
            () => close());

        CreateText(
            panel,
            "NPCs",
            new Vector2(-655f, 350f),
            new Vector2(190f, 42f),
            25,
            TextAlignmentOptions.Center);
        for (int index = 0; index < 9; index++)
        {
            Button button = CreateButton(
                panel,
                "",
                new Vector2(-655f, 285f - index * 65f),
                new Vector2(245f, 52f),
                delegate { });
            npcButtons.Add(button);
        }
        CreateButton(
            panel,
            "<",
            new Vector2(-725f, -350f),
            new Vector2(90f, 48f),
            () =>
            {
                npcPage = Mathf.Max(0, npcPage - 1);
                RefreshNpcButtons();
            });
        CreateButton(
            panel,
            ">",
            new Vector2(-605f, -350f),
            new Vector2(90f, 48f),
            () =>
            {
                npcPage++;
                RefreshNpcButtons();
            });

        content = CreatePanel(
            panel,
            "Content",
            new Vector2(150f, -20f),
            new Vector2(1110f, 760f));
        bool hasDynamicRelationships = DynamicRelationshipBridge.IsAvailable;
        float[] tabPositions = hasDynamicRelationships
            ? new[] { -400f, -225f, -50f, 125f, 300f, 475f }
            : new[] { -340f, -140f, 60f, 260f, 460f };
        Vector2 tabSize = hasDynamicRelationships
            ? new Vector2(165f, 52f)
            : new Vector2(190f, 52f);
        int tabIndex = 0;
        CreateButton(panel, "Description",
            new Vector2(tabPositions[tabIndex++], 345f), tabSize,
            () => ShowTab(EditorTab.Description));
        CreateButton(panel, "Personality",
            new Vector2(tabPositions[tabIndex++], 345f), tabSize,
            () => ShowTab(EditorTab.Personality));
        CreateButton(panel, "Knowledge",
            new Vector2(tabPositions[tabIndex++], 345f), tabSize,
            () => ShowTab(EditorTab.Knowledge));
        CreateButton(panel, "Relationships",
            new Vector2(tabPositions[tabIndex++], 345f), tabSize,
            () => ShowTab(EditorTab.Relationships));
        if (hasDynamicRelationships)
        {
            CreateButton(panel, "Dynamic",
                new Vector2(tabPositions[tabIndex++], 345f), tabSize,
                () => ShowTab(EditorTab.DynamicRelationships));
        }
        CreateButton(panel, "Routines",
            new Vector2(tabPositions[tabIndex], 345f), tabSize,
            () => ShowTab(EditorTab.Routines));

        status = CreateText(
            panel,
            "",
            new Vector2(140f, -410f),
            new Vector2(1080f, 45f),
            21,
            TextAlignmentOptions.Center);

        RefreshNpcButtons();
        npc = GetNpcs().FirstOrDefault();
        ShowTab(EditorTab.Description);
    }

    private void RefreshNpcButtons()
    {
        List<NeuralNPC> npcs = GetNpcs();
        int pages = Mathf.Max(1, Mathf.CeilToInt(npcs.Count / 9f));
        npcPage = Mathf.Clamp(npcPage, 0, pages - 1);
        for (int index = 0; index < npcButtons.Count; index++)
        {
            int npcIndex = npcPage * 9 + index;
            Button button = npcButtons[index];
            button.onClick = new Button.ButtonClickedEvent();
            if (npcIndex >= npcs.Count)
            {
                button.gameObject.SetActive(false);
                continue;
            }
            NeuralNPC captured = npcs[npcIndex];
            button.gameObject.SetActive(true);
            SetButtonLabel(button, captured.GetFinalName());
            button.onClick.AddListener(() =>
            {
                npc = captured;
                title.text = "NPC Editor - " + captured.GetFinalName();
                status.text = "";
                ShowTab(tab);
            });
        }
    }

    private void ShowTab(EditorTab target)
    {
        tab = target;
        ClearPage();
        if (npc == null)
        {
            CreatePageText(
                "No enabled NPCs are loaded.",
                new Vector2(0f, 250f),
                new Vector2(800f, 50f),
                26);
            return;
        }
        title.text = "NPC Editor - " + npc.GetFinalName();
        switch (target)
        {
            case EditorTab.Description:
                BuildDescriptionPage();
                break;
            case EditorTab.Personality:
                BuildPersonalityPage();
                break;
            case EditorTab.Knowledge:
                BuildKnowledgePage();
                break;
            case EditorTab.Relationships:
                BuildRelationshipsPage();
                break;
            case EditorTab.DynamicRelationships:
                if (DynamicRelationshipBridge.IsAvailable)
                    BuildDynamicRelationshipsPage();
                else
                    ShowTab(EditorTab.Relationships);
                break;
            case EditorTab.Routines:
                BuildRoutinePage();
                break;
        }
    }

    private void BuildDescriptionPage()
    {
        NpcOverride data = GetNpcData();
        CreatePageText(
            "Public / Physical Description",
            new Vector2(0f, 305f),
            new Vector2(900f, 38f),
            24);
        publicDescription = CreatePageInput(
            new Vector2(0f, 180f),
            new Vector2(960f, 190f),
            multiline: true);
        publicDescription.text = data.descriptionEnabled
            ? data.publicDescription
            : Plugin.GetOriginalResolvedDescription(npc!, false);

        CreatePageText(
            "Private Description",
            new Vector2(0f, 55f),
            new Vector2(900f, 38f),
            24);
        privateDescription = CreatePageInput(
            new Vector2(0f, -70f),
            new Vector2(960f, 190f),
            multiline: true);
        privateDescription.text = data.descriptionEnabled
            ? data.privateDescription
            : Plugin.GetOriginalResolvedDescription(npc!, true);

        CreatePageButton(
            "Save Description Override",
            new Vector2(-190f, -285f),
            new Vector2(340f, 55f),
            () =>
            {
                data.descriptionEnabled = true;
                data.publicDescription = publicDescription.text.Trim();
                data.privateDescription = privateDescription.text.Trim();
                Plugin.Commit();
                status.text = "Description override saved.";
            });
        CreatePageButton(
            "Use Game Defaults",
            new Vector2(190f, -285f),
            new Vector2(300f, 55f),
            () =>
            {
                data.descriptionEnabled = false;
                Plugin.Commit();
                ShowTab(EditorTab.Description);
                status.text = "Game descriptions restored.";
            });
    }

    private void BuildPersonalityPage()
    {
        NpcOverride data = GetNpcData();
        CreatePageText(
            "Personality Traits (comma-separated)",
            new Vector2(0f, 275f),
            new Vector2(900f, 45f),
            26);
        personality = CreatePageInput(
            new Vector2(0f, 100f),
            new Vector2(960f, 260f),
            multiline: true);
        personality.text = data.personalityEnabled
            ? data.personalityTraits
            : npc!.personalityTraits;
        CreatePageButton(
            "Save Personality Override",
            new Vector2(-190f, -100f),
            new Vector2(350f, 55f),
            () =>
            {
                data.personalityEnabled = true;
                data.personalityTraits = personality.text.Trim();
                Plugin.ApplyNpcOverride(npc!);
                Plugin.Commit();
                status.text = "Personality override saved and applied.";
            });
        CreatePageButton(
            "Use Game Defaults",
            new Vector2(190f, -100f),
            new Vector2(300f, 55f),
            () =>
            {
                data.personalityEnabled = false;
                Plugin.Commit();
                status.text =
                    "Override disabled; the original returns after reload.";
            });
    }

    private List<KnowledgeOverride> GetKnowledgeEntries()
    {
        var result = new List<KnowledgeOverride>();
        if (CustomContentDefinition_NPC.loaded.TryGetValue(
                npc!.GetFinalName(),
                out CustomContentDefinition_NPC definition)
            && Plugin.IsNativeKnowledgeDefinitionActive(npc, definition))
        {
            result.AddRange(definition.knowledgeEntries.Select(entry =>
                new KnowledgeOverride
                {
                    entryName = entry.entryName,
                    lore = entry.lore,
                    isPublic = entry.isPublic,
                    wordCloudDirty = entry.wordCloud.IsEmpty(),
                    playerNameResolvedInWordCloud = true,
                    nouns = entry.wordCloud.nouns.ToList(),
                    verbs = entry.wordCloud.verbs.ToList(),
                    adjectives = entry.wordCloud.adjectives.ToList(),
                    keywords = entry.wordCloud.keywords.ToList()
                }));
        }
        foreach (KnowledgeOverride edited in GetNpcData().knowledge)
        {
            int existingIndex = result.FindIndex(entry => string.Equals(
                entry.entryName, edited.entryName,
                StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
                result[existingIndex] = edited;
            else
                result.Add(edited);
        }
        return result;
    }

    private void BuildKnowledgePage()
    {
        List<KnowledgeOverride> entries = GetKnowledgeEntries();
        selectedKnowledge = Mathf.Clamp(
            selectedKnowledge, 0, Mathf.Max(0, entries.Count - 1));
        int knowledgePages = Mathf.Max(1, Mathf.CeilToInt(entries.Count / 6f));
        knowledgePage = Mathf.Clamp(knowledgePage, 0, knowledgePages - 1);
        for (int index = 0; index < 6; index++)
        {
            int entryIndex = knowledgePage * 6 + index;
            if (entryIndex >= entries.Count)
                break;
            int captured = entryIndex;
            bool isModEntry = GetNpcData().knowledge.Any(
                edited => string.Equals(
                    edited.entryName,
                    entries[entryIndex].entryName,
                    StringComparison.OrdinalIgnoreCase));
            Button button = CreatePageButton(
                (isModEntry ? "[MOD] " : "")
                    + entries[entryIndex].entryName,
                new Vector2(-330f, 285f - index * 58f),
                new Vector2(390f, 46f),
                () =>
                {
                    selectedKnowledge = captured;
                    ShowTab(EditorTab.Knowledge);
                });
            knowledgeButtons.Add(button);
        }
        CreatePageButton("<", new Vector2(-405f, -85f),
            new Vector2(80f, 42f), () =>
            {
                knowledgePage = Mathf.Max(0, knowledgePage - 1);
                ShowTab(EditorTab.Knowledge);
            });
        CreatePageButton(">", new Vector2(-255f, -85f),
            new Vector2(80f, 42f), () =>
            {
                knowledgePage++;
                ShowTab(EditorTab.Knowledge);
            });

        KnowledgeOverride selected = entries.Count > 0
            ? entries[selectedKnowledge]
            : new KnowledgeOverride();
        CreatePageText("Entry Name", new Vector2(230f, 305f),
            new Vector2(450f, 35f), 22);
        knowledgeName = CreatePageInput(
            new Vector2(230f, 260f), new Vector2(500f, 48f), false);
        knowledgeName.text = selected.entryName;
        selectedKnowledgeOriginalName = selected.entryName;
        CreatePageText("Knowledge", new Vector2(230f, 205f),
            new Vector2(450f, 35f), 22);
        knowledgeLore = CreatePageInput(
            new Vector2(230f, 65f), new Vector2(500f, 230f), true);
        knowledgeLore.text = selected.lore;
        knowledgeIsPublic = selected.isPublic;
        knowledgeVisibilityButton = CreatePageButton(
            "",
            new Vector2(230f, -85f), new Vector2(430f, 58f),
            () =>
            {
                knowledgeIsPublic = !knowledgeIsPublic;
                RefreshKnowledgeVisibilityButton();
                status.text = "Visibility changed locally. Click Save Entry "
                    + "to apply it.";
            });
        RefreshKnowledgeVisibilityButton();
        CreatePageButton("Save Entry", new Vector2(85f, -155f),
            new Vector2(220f, 48f), () => SaveKnowledgeEntry(false));
        CreatePageButton("New Entry", new Vector2(325f, -155f),
            new Vector2(220f, 48f), NewKnowledgeEntry);
        CreatePageButton("Delete Mod Entry", new Vector2(85f, -220f),
            new Vector2(220f, 48f), RemoveKnowledgeEntry);
        CreatePageButton("Use Game Defaults", new Vector2(325f, -220f),
            new Vector2(220f, 48f), RestoreKnowledgeDefaults);
    }

    private void SaveKnowledgeEntry(bool refreshOnly)
    {
        if (!refreshOnly
            && (knowledgeName == null
                || string.IsNullOrWhiteSpace(knowledgeName.text)))
        {
            status.text = "Knowledge entries require a name.";
            return;
        }
        if (!refreshOnly)
        {
            NpcOverride data = GetNpcData();
            string name = knowledgeName!.text.Trim();
            KnowledgeOverride? previous = data.knowledge.FirstOrDefault(
                entry => string.Equals(
                    entry.entryName, selectedKnowledgeOriginalName,
                    StringComparison.OrdinalIgnoreCase));
            previous ??= GetKnowledgeEntries().FirstOrDefault(
                entry => string.Equals(
                    entry.entryName, selectedKnowledgeOriginalName,
                    StringComparison.OrdinalIgnoreCase));
            string lore = knowledgeLore!.text.Trim();
            bool textChanged =
                previous == null || previous.lore != lore;
            data.knowledge.RemoveAll(entry => string.Equals(
                entry.entryName, selectedKnowledgeOriginalName,
                StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    entry.entryName, name,
                    StringComparison.OrdinalIgnoreCase));
            data.knowledge.Add(new KnowledgeOverride
            {
                entryName = name,
                lore = lore,
                isPublic = knowledgeIsPublic,
                wordCloudDirty =
                    textChanged || previous!.wordCloudDirty,
                playerNameResolvedInWordCloud =
                    !textChanged
                    && previous!.playerNameResolvedInWordCloud,
                nouns = textChanged
                    ? new List<string>()
                    : previous!.nouns.ToList(),
                verbs = textChanged
                    ? new List<string>()
                    : previous!.verbs.ToList(),
                adjectives = textChanged
                    ? new List<string>()
                    : previous!.adjectives.ToList(),
                keywords = textChanged
                    ? new List<string>()
                    : previous!.keywords.ToList()
            });
            Plugin.ApplyNpcOverride(npc!);
            Plugin.Commit();
            List<KnowledgeOverride> refreshed = GetKnowledgeEntries();
            selectedKnowledge = Mathf.Max(
                0,
                refreshed.FindIndex(entry => string.Equals(
                    entry.entryName, name,
                    StringComparison.OrdinalIgnoreCase)));
            knowledgePage = selectedKnowledge / 6;
            status.text = "Knowledge override saved.";
        }
        ShowTab(EditorTab.Knowledge);
    }

    private void RefreshKnowledgeVisibilityButton()
    {
        if (knowledgeVisibilityButton == null)
            return;
        SetButtonLabel(
            knowledgeVisibilityButton,
            knowledgeIsPublic
                ? "CURRENT: PUBLIC (shared)"
                : "CURRENT: PRIVATE (owner only)");
        Image image = knowledgeVisibilityButton.GetComponent<Image>();
        if (image != null)
        {
            image.color = knowledgeIsPublic
                ? new Color(0.22f, 0.55f, 0.30f, 1f)
                : new Color(0.48f, 0.28f, 0.24f, 1f);
        }
    }

    private void NewKnowledgeEntry()
    {
        selectedKnowledge = GetKnowledgeEntries().Count;
        knowledgeName!.text = "";
        knowledgeLore!.text = "";
        selectedKnowledgeOriginalName = "";
        knowledgeIsPublic = false;
        status.text = "Enter a name and knowledge text, then save.";
    }

    private void RemoveKnowledgeEntry()
    {
        List<KnowledgeOverride> entries = GetKnowledgeEntries();
        if (entries.Count == 0)
            return;
        string name = entries[selectedKnowledge].entryName;
        int removed = GetNpcData().knowledge.RemoveAll(
            entry => string.Equals(
                entry.entryName, name,
                StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
        {
            status.text = "This is a game-provided knowledge entry. "
                + "Only entries marked [MOD] can be deleted.";
            return;
        }
        Plugin.ApplyNpcOverride(npc!);
        Plugin.Commit();
        selectedKnowledge = 0;
        ShowTab(EditorTab.Knowledge);
        status.text = "Mod knowledge entry deleted.";
    }

    private void RestoreKnowledgeDefaults()
    {
        GetNpcData().knowledge.Clear();
        Plugin.ApplyNpcOverride(npc!);
        Plugin.Commit();
        selectedKnowledge = 0;
        ShowTab(EditorTab.Knowledge);
        status.text = "Game knowledge entries restored.";
    }

    private void BuildRelationshipsPage()
    {
        NpcOverride data = GetNpcData();
        CreatePageText("Player Relationship: level / daily cap / total cap",
            new Vector2(-180f, 305f), new Vector2(500f, 35f), 22);
        playerRelationshipLevel = CreatePageInput(
            new Vector2(-300f, 255f), new Vector2(140f, 48f), false);
        playerRelationshipDailyCap = CreatePageInput(
            new Vector2(-145f, 255f), new Vector2(140f, 48f), false);
        playerRelationshipTotalCap = CreatePageInput(
            new Vector2(10f, 255f), new Vector2(140f, 48f), false);
        playerRelationshipLevel.text = (data.playerRelationshipEnabled
            ? data.playerRelationshipLevel : npc!.relationshipLevel).ToString();
        playerRelationshipDailyCap.text = (data.playerRelationshipEnabled
            ? data.playerRelationshipDailyCap
            : npc!.relationshipLevelDailyCap).ToString();
        playerRelationshipTotalCap.text = (data.playerRelationshipEnabled
            ? data.playerRelationshipTotalCap
            : npc!.relationshipLevelTotalCap).ToString();
        CreatePageButton("Save Player Values",
            new Vector2(-145f, 195f), new Vector2(240f, 48f),
            SavePlayerRelationship);
        CreatePageText("Custom Relationship Title",
            new Vector2(300f, 245f), new Vector2(450f, 32f), 19);
        playerRelationshipTitle = CreatePageInput(
            new Vector2(300f, 200f), new Vector2(400f, 46f), false);
        playerRelationshipTitle.text = data.playerRelationshipTitle;
        CreatePageButton("Save Player Title",
            new Vector2(300f, 145f), new Vector2(240f, 44f),
            SavePlayerRelationshipTitle);

        List<NeuralNPC> targets = GetNpcs().Where(other => other != npc).ToList();
        selectedRelationship = Mathf.Clamp(
            selectedRelationship, 0, Mathf.Max(0, targets.Count - 1));
        int relationshipPages =
            Mathf.Max(1, Mathf.CeilToInt(targets.Count / 6f));
        relationshipPage = Mathf.Clamp(
            relationshipPage, 0, relationshipPages - 1);
        for (int index = 0; index < 6; index++)
        {
            int targetIndex = relationshipPage * 6 + index;
            if (targetIndex >= targets.Count)
                break;
            int captured = targetIndex;
            NeuralNPC target = targets[targetIndex];
            Button button = CreatePageButton(
                target.GetFinalName(),
                new Vector2(-340f, 80f - index * 55f),
                new Vector2(350f, 44f),
                () =>
                {
                    selectedRelationship = captured;
                    ShowTab(EditorTab.Relationships);
                });
            relationshipButtons.Add(button);
        }
        CreatePageButton("<", new Vector2(-405f, -245f),
            new Vector2(80f, 42f), () =>
            {
                relationshipPage = Mathf.Max(0, relationshipPage - 1);
                ShowTab(EditorTab.Relationships);
            });
        CreatePageButton(">", new Vector2(-255f, -245f),
            new Vector2(80f, 42f), () =>
            {
                relationshipPage++;
                ShowTab(EditorTab.Relationships);
            });
        if (targets.Count == 0)
            return;
        NeuralNPC selectedTarget = targets[selectedRelationship];
        string targetKey = Plugin.GetNpcStorageKey(selectedTarget);
        RelationshipOverride relation;
        if (!data.relationships.TryGetValue(targetKey, out relation))
        {
            relation = new RelationshipOverride
            {
                level = (int)npc!.relationships[selectedTarget.npcName],
                customName = npc.customRelationshipNames.TryGetValue(
                    selectedTarget.npcName, out string label) ? label : ""
            };
        }
        relationshipLevel = relation.level;
        CreatePageText("NPC Relationship", new Vector2(245f, 75f),
            new Vector2(500f, 35f), 22);
        CreatePageButton(
            ((NPCRelationship)relationshipLevel).ToString()
                .Replace("_", " "),
            new Vector2(245f, 25f), new Vector2(300f, 48f),
            () =>
            {
                relationshipLevel = (relationshipLevel + 1) % 4;
                SaveNpcRelationship(refreshOnly: false);
            });
        CreatePageText("Custom prompt label (optional)",
            new Vector2(245f, -35f), new Vector2(500f, 35f), 20);
        customRelationshipName = CreatePageInput(
            new Vector2(245f, -80f), new Vector2(500f, 48f), false);
        customRelationshipName.text = relation.customName;
        CreatePageButton("Save NPC Relationship",
            new Vector2(245f, -145f), new Vector2(300f, 48f),
            () => SaveNpcRelationship(false));
        CreatePageButton("Remove Relationship Override",
            new Vector2(245f, -210f), new Vector2(330f, 48f),
            RemoveNpcRelationship);
    }

    private void BuildDynamicRelationshipsPage()
    {
        bool canToggleSave =
            DynamicRelationshipBridge.CanToggleCurrentSave;
        bool saveEnabled =
            DynamicRelationshipBridge.IsEnabledForCurrentSave;
        bool globallyEnabled =
            DynamicRelationshipBridge.IsGloballyEnabled;
        CreatePageText("Dynamic NPC Relationships",
            canToggleSave
                ? new Vector2(15f, 305f)
                : new Vector2(150f, 305f),
            canToggleSave
                ? new Vector2(580f, 42f)
                : new Vector2(850f, 42f),
            27);
        if (canToggleSave)
        {
            CreatePageButton(
                saveEnabled
                    ? "Save-wide System: On"
                    : "Save-wide System: Off",
                new Vector2(465f, 305f), new Vector2(285f, 42f),
                ToggleDynamicSystemForCurrentSave);
        }

        if (!saveEnabled)
        {
            bool hasSaveData =
                DynamicRelationshipBridge.HasCurrentSaveData;
            CreatePageText(
                hasSaveData
                    ? "Dynamic Relationships is disabled for this save. "
                        + "Its existing relationship progress is retained "
                        + "and no scoring, decay, prompts, proposals, or "
                        + "dynamic title changes will run until it is "
                        + "enabled again."
                    : "Dynamic Relationships has not been enabled for this "
                        + "save or committed by a game save. New saves begin "
                        + "disabled, and no per-run relationship JSON is "
                        + "created until the save-wide system is enabled and "
                        + "the player saves the game.",
                new Vector2(150f, 80f), new Vector2(850f, 250f), 23);
            return;
        }

        if (!globallyEnabled)
        {
            CreatePageText(
                "This save's Dynamic Relationships toggle is on, but the "
                    + "BepInEx General.Enabled master switch is off. Saved "
                    + "progress is retained, but the system will remain "
                    + "inactive until the master switch is enabled.",
                new Vector2(150f, 80f), new Vector2(850f, 250f), 23);
            return;
        }

        List<NeuralNPC> targets = GetNpcs()
            .Where(other => other != npc)
            .ToList();
        CreatePageText("Select another NPC",
            new Vector2(-325f, 255f), new Vector2(380f, 34f), 20);

        selectedRelationship = Mathf.Clamp(
            selectedRelationship, 0, Mathf.Max(0, targets.Count - 1));
        int pages = Mathf.Max(1, Mathf.CeilToInt(targets.Count / 8f));
        relationshipPage = Mathf.Clamp(relationshipPage, 0, pages - 1);
        for (int index = 0; index < 8; index++)
        {
            int targetIndex = relationshipPage * 8 + index;
            if (targetIndex >= targets.Count)
                break;
            int captured = targetIndex;
            NeuralNPC target = targets[targetIndex];
            Button button = CreatePageButton(
                target.GetFinalName(),
                new Vector2(-330f, 205f - index * 57f),
                new Vector2(370f, 45f),
                () =>
                {
                    selectedRelationship = captured;
                    ShowTab(EditorTab.DynamicRelationships);
                });
            relationshipButtons.Add(button);
        }
        CreatePageButton("<", new Vector2(-390f, -305f),
            new Vector2(85f, 42f), () =>
            {
                relationshipPage = Mathf.Max(0, relationshipPage - 1);
                ShowTab(EditorTab.DynamicRelationships);
            });
        CreatePageButton(">", new Vector2(-265f, -305f),
            new Vector2(85f, 42f), () =>
            {
                relationshipPage++;
                ShowTab(EditorTab.DynamicRelationships);
            });

        if (targets.Count == 0)
            return;

        NeuralNPC selectedTarget = targets[selectedRelationship];
        CreatePageText(
            npc!.GetFinalName() + " + " + selectedTarget.GetFinalName(),
            new Vector2(245f, 235f), new Vector2(550f, 45f), 25);
        DynamicRelationshipSnapshot? dynamicRelationship =
            DynamicRelationshipBridge.TryGetSnapshot(npc, selectedTarget);
        if (dynamicRelationship == null)
        {
            bool enabledByDefaults =
                DynamicRelationshipBridge.IsPairEnabledBySaveDefaults(
                    npc, selectedTarget);
            CreatePageText(
                enabledByDefaults
                    ? "This pair is not dynamically managed. Loaded symmetric "
                        + "Stranger, Acquaintance, and Friend relationships are "
                        + "normally initialized automatically. This pair may "
                        + "use an asymmetric or special authored tier, or may "
                        + "have appeared after this run's initialization."
                    : "Dynamic relationships between this pair are disabled "
                        + "for this save by the defaultRelationships.json "
                        + "policy captured when the save was first opened.",
                new Vector2(245f, 95f), new Vector2(540f, 190f), 21);
            if (enabledByDefaults
                && DynamicRelationshipBridge.CanBegin(npc, selectedTarget))
            {
                CreatePageButton("Begin Dynamic Tracking",
                    new Vector2(245f, -70f), new Vector2(340f, 48f),
                    BeginDynamicRelationship);
            }
            return;
        }

        bool canEditState = DynamicRelationshipBridge.CanEditState
            && !dynamicRelationship.Suspended;
        CreatePageText(
            dynamicRelationship.GetDisplayText(),
            canEditState
                ? new Vector2(245f, 145f)
                : new Vector2(245f, 115f),
            canEditState
                ? new Vector2(590f, 180f)
                : new Vector2(590f, 260f),
            canEditState ? 16 : 18);
        if (canEditState)
        {
            dynamicRelationshipStage = dynamicRelationship.Stage;
            dynamicRelationshipTierLocked = dynamicRelationship.TierLocked;
            dynamicRelationshipHasEnemies =
                dynamicRelationship.HasEnemiesTier;

            CreatePageText(
                $"Score ({dynamicRelationship.MinimumScore} to "
                    + $"{dynamicRelationship.MaximumScore})",
                new Vector2(45f, 30f), new Vector2(175f, 30f), 17);
            dynamicRelationshipScore = CreatePageInput(
                new Vector2(45f, -10f),
                new Vector2(160f, 42f), false);
            dynamicRelationshipScore.text =
                dynamicRelationship.Score.ToString();
            CreatePageText("Current Tier",
                new Vector2(235f, 30f), new Vector2(185f, 30f), 17);
            dynamicRelationshipStageButton = CreatePageButton(
                dynamicRelationshipStage,
                new Vector2(235f, -10f), new Vector2(185f, 42f),
                CycleDynamicRelationshipStage);
            dynamicRelationshipLockButton = CreatePageButton(
                "",
                new Vector2(465f, -10f), new Vector2(245f, 42f),
                ToggleDynamicRelationshipLock);
            RefreshDynamicRelationshipLockLabel();
            CreatePageButton("Save Dynamic State",
                new Vector2(245f, -75f), new Vector2(300f, 46f),
                SaveDynamicRelationshipState);
        }

        float actionY = canEditState ? -145f : -80f;
        if (dynamicRelationship.Suspended)
        {
            CreatePageButton(
                "Resume Dynamic Control",
                canEditState
                    ? new Vector2(100f, actionY)
                    : new Vector2(245f, actionY),
                canEditState
                    ? new Vector2(260f, 46f)
                    : new Vector2(340f, 48f),
                ResumeDynamicRelationship);
        }
        CreatePageButton(
            "Reset Dynamic Progress",
            dynamicRelationship.Suspended && canEditState
                ? new Vector2(390f, actionY)
                : new Vector2(
                    245f,
                    canEditState ? actionY : -155f),
            canEditState
                ? new Vector2(260f, 46f)
                : new Vector2(340f, 48f),
            ResetDynamicRelationship);
    }

    private void ToggleDynamicSystemForCurrentSave()
    {
        bool enable =
            !DynamicRelationshipBridge.IsEnabledForCurrentSave;
        bool changed =
            DynamicRelationshipBridge.SetEnabledForCurrentSave(enable);
        bool globallyEnabled =
            DynamicRelationshipBridge.IsGloballyEnabled;
        ShowTab(EditorTab.DynamicRelationships);
        if (!changed)
        {
            status.text = "The save-wide Dynamic Relationships setting "
                + "could not be changed because no active save is ready.";
            return;
        }
        if (!enable)
        {
            status.text = "Dynamic Relationships disabled for this save; "
                + "existing progress was retained. Save the game to persist "
                + "this setting.";
            return;
        }
        status.text = globallyEnabled
            ? "Dynamic Relationships enabled for this save. Save the game "
                + "to persist this setting and create its JSON."
            : "Dynamic Relationships enabled for this save, but the global "
                + "BepInEx master switch remains off. Save the game to "
                + "persist the save-wide setting.";
    }

    private void SavePlayerRelationship()
    {
        if (!int.TryParse(playerRelationshipLevel!.text, out int level)
            || !int.TryParse(playerRelationshipDailyCap!.text, out int daily)
            || !int.TryParse(playerRelationshipTotalCap!.text, out int total))
        {
            status.text = "Relationship values must be whole numbers.";
            return;
        }
        NpcOverride data = GetNpcData();
        data.playerRelationshipEnabled = true;
        data.playerRelationshipLevel = Mathf.Clamp(level, 0, 100);
        data.playerRelationshipDailyCap = Mathf.Clamp(daily, 0, 100);
        data.playerRelationshipTotalCap = Mathf.Clamp(total, 0, 100);
        Plugin.ApplyNpcOverride(npc!);
        Plugin.Commit();
        status.text = "Player relationship values saved and applied.";
    }

    private void SavePlayerRelationshipTitle()
    {
        GetNpcData().playerRelationshipTitle =
            playerRelationshipTitle!.text.Trim();
        Plugin.Commit();
        status.text = string.IsNullOrWhiteSpace(
            playerRelationshipTitle.text)
            ? "Custom player relationship title removed."
            : "Custom player relationship title saved.";
    }

    private void SaveNpcRelationship(bool refreshOnly)
    {
        List<NeuralNPC> targets = GetNpcs().Where(other => other != npc).ToList();
        if (targets.Count == 0)
            return;
        if (!refreshOnly)
        {
            NeuralNPC target = targets[selectedRelationship];
            DynamicRelationshipBridge.NotifyExternalOverride(npc!, target);
            GetNpcData().relationships[Plugin.GetNpcStorageKey(target)] =
                new RelationshipOverride
                {
                    level = relationshipLevel,
                    customName = customRelationshipName!.text.Trim()
                };
            Plugin.ApplyNpcOverride(npc!);
            Plugin.Commit();
            status.text = "NPC relationship saved symmetrically.";
        }
        ShowTab(EditorTab.Relationships);
    }

    private void RemoveNpcRelationship()
    {
        List<NeuralNPC> targets = GetNpcs().Where(other => other != npc).ToList();
        if (targets.Count == 0)
            return;
        NeuralNPC target = targets[selectedRelationship];
        GetNpcData().relationships.Remove(Plugin.GetNpcStorageKey(target));
        Plugin.Commit();
        bool resumed = DynamicRelationshipBridge.Resume(npc!, target);
        ShowTab(EditorTab.Relationships);
        status.text = resumed
            ? "Relationship override removed; dynamic progression resumed."
            : "Relationship override removed; reload restores save data.";
    }

    private void ResetDynamicRelationship()
    {
        List<NeuralNPC> targets = GetNpcs().Where(other => other != npc).ToList();
        if (targets.Count == 0)
            return;
        NeuralNPC target = targets[selectedRelationship];
        bool reset = DynamicRelationshipBridge.Reset(npc!, target);
        ShowTab(EditorTab.DynamicRelationships);
        status.text = reset
            ? "Dynamic relationship progress reset."
            : "No dynamic relationship progress exists for this pair.";
    }

    private void CycleDynamicRelationshipStage()
    {
        string[] stages = dynamicRelationshipHasEnemies
            ? DynamicStagesWithEnemies
            : LegacyDynamicStages;
        int index = Array.IndexOf(stages, dynamicRelationshipStage);
        dynamicRelationshipStage = stages[(index + 1) % stages.Length];
        if (dynamicRelationshipStageButton != null)
        {
            SetButtonLabel(
                dynamicRelationshipStageButton,
                dynamicRelationshipStage);
        }
        RefreshDynamicRelationshipLockLabel();
    }

    private void ToggleDynamicRelationshipLock()
    {
        dynamicRelationshipTierLocked = !dynamicRelationshipTierLocked;
        RefreshDynamicRelationshipLockLabel();
    }

    private void RefreshDynamicRelationshipLockLabel()
    {
        if (dynamicRelationshipLockButton == null)
            return;
        SetButtonLabel(
            dynamicRelationshipLockButton,
            dynamicRelationshipTierLocked
                ? "Tier Lock: On"
                : "Tier Lock: Off");
    }

    private void SaveDynamicRelationshipState()
    {
        if (dynamicRelationshipScore == null
            || !int.TryParse(
                dynamicRelationshipScore.text, out int requestedScore))
        {
            status.text = "Dynamic score must be a whole number.";
            return;
        }

        List<NeuralNPC> targets = GetNpcs()
            .Where(other => other != npc)
            .ToList();
        if (targets.Count == 0)
            return;
        NeuralNPC target = targets[selectedRelationship];
        DynamicRelationshipSnapshot? snapshot =
            DynamicRelationshipBridge.TryGetSnapshot(npc!, target);
        if (snapshot == null)
        {
            status.text =
                "Dynamic relationship state is no longer available.";
            ShowTab(EditorTab.DynamicRelationships);
            return;
        }

        int appliedScore = Mathf.Clamp(
            requestedScore,
            snapshot.MinimumScore,
            snapshot.MaximumScore);
        bool saved = DynamicRelationshipBridge.SetState(
            npc!, target,
            appliedScore,
            dynamicRelationshipStage,
            dynamicRelationshipTierLocked);
        ShowTab(EditorTab.DynamicRelationships);
        status.text = saved
            ? requestedScore == appliedScore
                ? "Dynamic score, tier, and tier lock saved."
                : "Dynamic state saved; score was clamped to "
                    + appliedScore + "."
            : "Dynamic state could not be saved; dynamic control may have "
                + "been suspended by another relationship source.";
    }

    private void ResumeDynamicRelationship()
    {
        List<NeuralNPC> targets = GetNpcs().Where(other => other != npc).ToList();
        if (targets.Count == 0)
            return;
        NeuralNPC target = targets[selectedRelationship];
        bool resumed = DynamicRelationshipBridge.Resume(npc!, target);
        ShowTab(EditorTab.DynamicRelationships);
        status.text = resumed
            ? "Dynamic relationship control resumed."
            : "Dynamic relationship control could not be resumed.";
    }

    private void BeginDynamicRelationship()
    {
        List<NeuralNPC> targets = GetNpcs().Where(other => other != npc).ToList();
        if (targets.Count == 0)
            return;
        NeuralNPC target = targets[selectedRelationship];
        bool began = DynamicRelationshipBridge.Begin(npc!, target);
        ShowTab(EditorTab.DynamicRelationships);
        status.text = began
            ? "Dynamic relationship tracking started."
            : "This pair is not eligible for dynamic tracking.";
    }

    private void BuildRoutinePage()
    {
        ResolveRoutines();
        for (int index = 0; index < 7; index++)
        {
            Button button = CreatePageButton(
                "",
                new Vector2(-280f, 295f - index * 62f),
                new Vector2(480f, 48f),
                delegate { });
            routineButtons.Add(button);
        }
        CreatePageButton(
            "<",
            new Vector2(-380f, -165f),
            new Vector2(90f, 45f),
            () =>
            {
                routinePage = Mathf.Max(0, routinePage - 1);
                RefreshRoutineButtons();
            });
        CreatePageButton(
            ">",
            new Vector2(-180f, -165f),
            new Vector2(90f, 45f),
            () =>
            {
                routinePage++;
                RefreshRoutineButtons();
            });

        CreatePageText(
            "Target X / Y",
            new Vector2(255f, 295f),
            new Vector2(400f, 35f),
            22);
        targetX = CreatePageInput(
            new Vector2(120f, 245f),
            new Vector2(150f, 48f),
            false);
        targetY = CreatePageInput(
            new Vector2(280f, 245f),
            new Vector2(150f, 48f),
            false);
        CreatePageButton(
            "Pick World Tile",
            new Vector2(455f, 245f),
            new Vector2(200f, 48f),
            BeginWorldTileSelection);
        CreatePageText(
            "Travel Activity",
            new Vector2(255f, 185f),
            new Vector2(430f, 35f),
            21);
        pathingActivity = CreatePageInput(
            new Vector2(255f, 140f),
            new Vector2(480f, 48f),
            false);
        CreatePageText(
            "Destination Activity",
            new Vector2(255f, 85f),
            new Vector2(430f, 35f),
            21);
        activity = CreatePageInput(
            new Vector2(255f, 40f),
            new Vector2(480f, 48f),
            false);
        CreatePageText(
            "Visible Activity",
            new Vector2(255f, -15f),
            new Vector2(430f, 35f),
            21);
        visibleActivity = CreatePageInput(
            new Vector2(255f, -60f),
            new Vector2(480f, 48f),
            false);
        CreatePageButton(
            "Save Override",
            new Vector2(65f, -145f),
            new Vector2(170f, 50f),
            SaveRoutine);
        CreatePageButton(
            "Remove Override",
            new Vector2(245f, -145f),
            new Vector2(170f, 50f),
            RemoveRoutine);
        CreatePageButton(
            "Analyze / Catalog",
            new Vector2(425f, -145f),
            new Vector2(170f, 50f),
            AnalyzeSelectedRoutine);
        RefreshRoutineButtons();
    }

    private void ResolveRoutines()
    {
        routines.Clear();
        var seen = new HashSet<string>();
        selectedRoutine = 0;
        routinePage = 0;
        NPCRoutineExecutor? executor =
            npc!.GetComponent<NPCRoutineExecutor>();
        if (executor?.currentRoutine != null)
        {
            seen.Add(Plugin.GetRoutineKey(executor.currentRoutine));
            routines.Add(new RoutineChoice
            {
                Hour = -1,
                Routine = executor.currentRoutine,
                Active = true
            });
        }

        if (CustomContentDefinition_NPC.loaded.TryGetValue(
                npc.GetFinalName(), out CustomContentDefinition_NPC definition)
            && definition.possibleRoutines.Count > 0)
        {
            foreach (NPCRoutine source in definition.possibleRoutines)
            {
                NPCRoutine routine = source.DeepClone();
                Plugin.RememberOriginalRoutineKey(routine);
                if (!seen.Add(Plugin.GetRoutineKey(routine)))
                    continue;
                routines.Add(new RoutineChoice
                {
                    Hour = -2,
                    Routine = routine
                });
            }
        }

        foreach (NPCRoutine routine in
            Plugin.GetObservedRoutines(npc))
        {
            if (!seen.Add(Plugin.GetRoutineKey(routine)))
                continue;
            routines.Add(new RoutineChoice
            {
                Hour = -3,
                Routine = routine
            });
        }
    }

    private void RefreshRoutineButtons()
    {
        int pages = Mathf.Max(1, Mathf.CeilToInt(routines.Count / 7f));
        routinePage = Mathf.Clamp(routinePage, 0, pages - 1);
        for (int index = 0; index < routineButtons.Count; index++)
        {
            int routineIndex = routinePage * 7 + index;
            Button button = routineButtons[index];
            button.onClick = new Button.ButtonClickedEvent();
            if (routineIndex >= routines.Count)
            {
                button.gameObject.SetActive(false);
                continue;
            }
            RoutineChoice choice = routines[routineIndex];
            string time = choice.Active
                ? "ACTIVE"
                : choice.Hour == -2
                    ? "DEFINED"
                : choice.Hour < 0
                    ? "OBSERVED"
                : choice.Hour.ToString("00") + ":00";
            string edited = GetNpcData().routines.ContainsKey(
                Plugin.GetRoutineKey(choice.Routine))
                ? " [edited]"
                : "";
            SetButtonLabel(
                button,
                time + " - " + choice.Routine.activity + edited);
            button.gameObject.SetActive(true);
            int captured = routineIndex;
            button.onClick.AddListener(() =>
            {
                selectedRoutine = captured;
                LoadRoutineFields();
            });
        }
        LoadRoutineFields();
    }

    private void LoadRoutineFields()
    {
        if (routines.Count == 0 || targetX == null)
            return;
        selectedRoutine = Mathf.Clamp(
            selectedRoutine, 0, routines.Count - 1);
        NPCRoutine routine = routines[selectedRoutine].Routine;
        string key = Plugin.GetRoutineKey(routine);
        if (GetNpcData().routines.TryGetValue(
                key, out RoutineOverride stored))
        {
            targetX.text = stored.targetX.ToString();
            targetY!.text = stored.targetY.ToString();
            pathingActivity!.text = stored.pathingActivity;
            activity!.text = stored.activity;
            visibleActivity!.text = stored.visibleActivity;
        }
        else
        {
            targetX.text = routine.targetPosition.x.ToString();
            targetY!.text = routine.targetPosition.y.ToString();
            pathingActivity!.text = routine.pathingActivity;
            activity!.text = routine.activity;
            visibleActivity!.text = routine.visibleActivity;
        }
        status.text = string.Empty;
    }

    private void SaveRoutine()
    {
        if (routines.Count == 0
            || !int.TryParse(targetX!.text, out int x)
            || !int.TryParse(targetY!.text, out int y))
        {
            status.text = "Target X and Y must be whole numbers.";
            return;
        }
        RoutineChoice choice = routines[selectedRoutine];
        string key = Plugin.GetRoutineKey(choice.Routine);
        NpcOverride npcData = GetNpcData();
        if (!npcData.routines.TryGetValue(
                key, out RoutineOverride data))
        {
            data = new RoutineOverride
            {
                hasOriginal = true,
                originalTargetX = choice.Routine.targetPosition.x,
                originalTargetY = choice.Routine.targetPosition.y,
                originalPathingActivity = choice.Routine.pathingActivity,
                originalActivity = choice.Routine.activity,
                originalVisibleActivity = choice.Routine.visibleActivity
            };
            npcData.routines[key] = data;
        }
        data.targetX = x;
        data.targetY = y;
        data.pathingActivity = pathingActivity!.text.Trim();
        data.activity = activity!.text.Trim();
        data.visibleActivity = visibleActivity!.text.Trim();
        Plugin.Commit();
        if (choice.Active)
        {
            NPCRoutineExecutor executor =
                npc!.GetComponent<NPCRoutineExecutor>();
            if (!executor.IsCurrentRoutineOverrideRoutine())
            {
                Plugin.ApplyRoutineOverride(npc, choice.Routine);
                executor.StartNPCRoutine(choice.Routine);
                status.text = "Override saved and active routine restarted.";
                return;
            }
        }
        status.text = "Routine override saved.";
        RefreshRoutineButtons();
    }

    private void RemoveRoutine()
    {
        if (routines.Count == 0)
            return;
        RoutineChoice choice = routines[selectedRoutine];
        string key = Plugin.GetRoutineKey(choice.Routine);
        NpcOverride npcData = GetNpcData();
        if (npcData.routines.TryGetValue(
                key, out RoutineOverride removed)
            && npcData.routines.Remove(key))
        {
            Plugin.Commit();
            if (choice.Active)
                RestoreActiveRoutine(choice, key, removed);
            else
                status.text = "Routine override removed.";
            string result = status.text;
            RefreshRoutineButtons();
            status.text = result;
        }
    }

    private void AnalyzeSelectedRoutine()
    {
        if (npc == null || routines.Count == 0)
        {
            status.text = "No routine is selected for analysis.";
            return;
        }

        RoutineChoice choice = routines[selectedRoutine];
        status.text = Plugin.AnalyzeAndRecordRoutine(
            npc, choice.Routine, choice.Active);
    }

    private void RestoreActiveRoutine(
        RoutineChoice choice,
        string key,
        RoutineOverride removed)
    {
        NPCRoutine original;
        if (removed.hasOriginal)
        {
            original = choice.Routine.DeepClone();
            original.targetPosition = new Vector2Int(
                removed.originalTargetX,
                removed.originalTargetY);
            original.pathingActivity = removed.originalPathingActivity;
            original.activity = removed.originalActivity;
            original.visibleActivity = removed.originalVisibleActivity;
            Plugin.RememberOriginalRoutineKey(original);
        }
        else if (!Plugin.TryGetOriginalRoutine(
                     npc!, key, out original))
        {
            status.text = "Override removed. The original coordinate will "
                + "return when the game next selects this routine.";
            return;
        }

        NPCRoutineExecutor executor =
            npc!.GetComponent<NPCRoutineExecutor>();
        if (executor.IsCurrentRoutineOverrideRoutine())
        {
            status.text = "Override removed. Immediate restoration was "
                + "deferred because a scripted routine is active.";
            return;
        }
        choice.Routine = original;
        executor.StartNPCRoutine(original);
        status.text =
            "Override removed and the original routine restored.";
    }

    private void BeginWorldTileSelection()
    {
        if (targetX == null || targetY == null)
            return;
        StartCoroutine(SelectWorldTileCoroutine());
    }

    private IEnumerator SelectWorldTileCoroutine()
    {
        CanvasGroup group = GetComponent<CanvasGroup>();
        group.alpha = 0f;
        group.interactable = false;
        group.blocksRaycasts = false;
        bool modToolsWorldInput =
            Silverpine.ModdingTools.InventoryModTools.BeginWorldInput();
        if (!modToolsWorldInput && PauseMenuManager.Instance != null
            && PauseMenuManager.Instance.open)
        {
            PauseMenuManager.Instance.Close();
        }
        UpperNotificationUI.Instance.OneOff(
            "Click a loaded world tile for the routine destination. "
            + "Press Escape to cancel.");

        // Let the button release and pause-menu transition finish first.
        yield return null;
        yield return null;
        yield return null;

        while (true)
        {
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                UpperNotificationUI.Instance.OneOff(
                    "Routine destination selection cancelled.");
                RestoreAfterWorldSelection(group, modToolsWorldInput);
                yield break;
            }
            if (!Input.GetKeyUp(KeyCode.Mouse0))
            {
                yield return null;
                continue;
            }

            Camera camera = Camera.main;
            if (camera == null && Player.Instance != null)
            {
                Transform cameraTransform =
                    Player.Instance.transform.Find("Main Camera");
                if (cameraTransform != null)
                    camera = cameraTransform.GetComponent<Camera>();
            }
            if (camera == null)
            {
                camera = Camera.allCameras.FirstOrDefault(
                    candidate =>
                        candidate != null
                        && candidate.isActiveAndEnabled);
            }
            if (camera == null)
            {
                UpperNotificationUI.Instance.OneOff(
                    "No active world camera was found.");
                RestoreAfterWorldSelection(group, modToolsWorldInput);
                yield break;
            }

            Vector3 world =
                camera.ScreenToWorldPoint(Input.mousePosition);
            Vector2Int tile = Vector2Int.RoundToInt(
                new Vector2(world.x, world.y));
            if (!Turfs.IsValidTurf(tile))
            {
                UpperNotificationUI.Instance.OneOff(
                    $"No loaded world tile exists at {tile.x}, {tile.y}. "
                    + "Try again or press Escape.");
                yield return null;
                continue;
            }

            targetX!.text = tile.x.ToString();
            targetY!.text = tile.y.ToString();
            status.text =
                $"Selected routine destination {tile.x}, {tile.y}.";
            RestoreAfterWorldSelection(group, modToolsWorldInput);
            yield break;
        }
    }

    private static void RestoreAfterWorldSelection(
        CanvasGroup group,
        bool modToolsWorldInput)
    {
        if (modToolsWorldInput)
        {
            Silverpine.ModdingTools.InventoryModTools.EndWorldInput();
        }
        else if (PauseMenuManager.Instance != null
            && !PauseMenuManager.Instance.open)
        {
            PauseMenuManager.Instance.Toggle();
        }
        group.alpha = 1f;
        group.interactable = true;
        group.blocksRaycasts = true;
    }

    private void ClearPage()
    {
        foreach (GameObject item in pageObjects)
            if (item != null)
                Destroy(item);
        pageObjects.Clear();
        routineButtons.Clear();
        knowledgeButtons.Clear();
        relationshipButtons.Clear();
        knowledgeVisibilityButton = null;
        publicDescription = null;
        privateDescription = null;
        personality = null;
        targetX = null;
        dynamicRelationshipScore = null;
        dynamicRelationshipStageButton = null;
        dynamicRelationshipLockButton = null;
    }

    private Button CreatePageButton(
        string label,
        Vector2 position,
        Vector2 size,
        Action callback)
    {
        Button result = CreateButton(
            content, label, position, size, callback);
        pageObjects.Add(result.gameObject);
        return result;
    }

    private TextMeshProUGUI CreatePageText(
        string value,
        Vector2 position,
        Vector2 size,
        int fontSize)
    {
        TextMeshProUGUI result = CreateText(
            content,
            value,
            position,
            size,
            fontSize,
            TextAlignmentOptions.Center);
        pageObjects.Add(result.gameObject);
        return result;
    }

    private TMP_InputField CreatePageInput(
        Vector2 position,
        Vector2 size,
        bool multiline)
    {
        TMP_InputField field = Instantiate(
            inputTemplate,
            content,
            worldPositionStays: false);
        field.name = "NPCEditorInput";
        field.onValueChanged = new TMP_InputField.OnChangeEvent();
        field.onSubmit = new TMP_InputField.SubmitEvent();
        field.text = "";
        field.interactable = true;
        field.gameObject.SetActive(true);
        field.lineType = multiline
            ? TMP_InputField.LineType.MultiLineNewline
            : TMP_InputField.LineType.SingleLine;
        SetRect(field.GetComponent<RectTransform>(), position, size);
        pageObjects.Add(field.gameObject);
        return field;
    }

    private Button CreateButton(
        Transform parent,
        string label,
        Vector2 position,
        Vector2 size,
        Action callback)
    {
        Button button = Instantiate(
            buttonTemplate,
            parent,
            worldPositionStays: false);
        button.name = "NPCEditor_" + label.Replace(" ", "");
        button.onClick = new Button.ButtonClickedEvent();
        button.onClick.AddListener(() => callback());
        // InventoryUI.useButton is a visual template, but its interactable
        // state reflects the currently selected inventory item. Editor-owned
        // clones must not inherit that gameplay state.
        Silverpine.ModdingTools.ModUi.NormalizeButton(button);
        SetButtonLabel(button, label);
        SetRect(button.GetComponent<RectTransform>(), position, size);
        return button;
    }

    private void SetButtonLabel(Button button, string value)
    {
        TextMeshProUGUI label =
            button.GetComponentInChildren<TextMeshProUGUI>(true);
        label.text = value;
        label.fontSize = Mathf.Min(label.fontSize, 23f);
        label.enableWordWrapping = false;
        label.overflowMode = TextOverflowModes.Ellipsis;
    }

    private TextMeshProUGUI CreateText(
        Transform parent,
        string value,
        Vector2 position,
        Vector2 size,
        int fontSize,
        TextAlignmentOptions alignment)
    {
        GameObject go = new(
            "NPCEditorText",
            typeof(RectTransform),
            typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        TextMeshProUGUI text = go.GetComponent<TextMeshProUGUI>();
        text.font = font;
        text.text = value;
        text.fontSize = fontSize;
        text.color = Color.white;
        text.alignment = alignment;
        text.enableWordWrapping = true;
        SetRect((RectTransform)go.transform, position, size);
        return text;
    }

    private static RectTransform CreatePanel(
        Transform parent,
        string name,
        Vector2 position,
        Vector2 size)
    {
        GameObject go = new(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        RectTransform rect = (RectTransform)go.transform;
        SetRect(rect, position, size);
        return rect;
    }

    private NpcOverride GetNpcData()
    {
        string key = Plugin.GetNpcStorageKey(npc!);
        if (!Plugin.CurrentSave.npcs.TryGetValue(
                key, out NpcOverride data))
        {
            data = new NpcOverride();
            Plugin.CurrentSave.npcs[key] = data;
        }
        return data;
    }

    private static List<NeuralNPC> GetNpcs() =>
        NeuralNPC.neuralNPCs?.Values
            .Where(n => n != null && n.isActiveAndEnabled)
            .OrderBy(n => n.GetFinalName())
            .ToList()
        ?? new List<NeuralNPC>();

    private static void SetRect(
        RectTransform rect,
        Vector2 position,
        Vector2 size)
    {
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
        rect.localScale = Vector3.one;
    }

    private static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        rect.localScale = Vector3.one;
    }
}
