using Mono.Cecil;
using Mono.Cecil.Cil;
using RuntimeNPCEditor;

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    checks++;
}

Check(EditorDataRules.TotalCaps.SequenceEqual(new[] { 14, 49, 79, 100 }),
    "Dropdown must contain exactly the native total-cap stages.");
foreach (int level in Enumerable.Range(0, 101))
{
    foreach (int cap in EditorDataRules.TotalCaps)
        Check(EditorDataRules.ValidatePlayerValues(level, level + 10, cap, out _),
            "Valid native values rejected.");
}
foreach (int cap in Enumerable.Range(0, 111).Except(EditorDataRules.TotalCaps))
    Check(!EditorDataRules.ValidatePlayerValues(0, 10, cap, out _),
        "Unsupported total cap accepted.");
foreach (var invalid in new[] { (-1, 10, 14), (101, 10, 14), (10, -1, 14), (10, 111, 14) })
    Check(!EditorDataRules.ValidatePlayerValues(invalid.Item1, invalid.Item2,
        invalid.Item3, out _), "Out-of-range player edit accepted.");

Check(!EditorDataRules.IsSavedSelection(3, 3), "New unsaved entry is not a saved selection.");
Check(!EditorDataRules.IsSavedSelection(-1, 3), "Negative index accepted.");
Check(!EditorDataRules.IsSavedSelection(0, 0), "Empty list index accepted.");
Check(EditorDataRules.IsSavedSelection(2, 3), "Last valid selection rejected.");
Check(EditorDataRules.KnowledgeTextChanged("Old", "Lore", "New", "Lore"),
    "Renaming must regenerate the word cloud.");
Check(!EditorDataRules.KnowledgeTextChanged("Name", "Lore", "Name", "Lore"),
    "Visibility-only change must preserve the word cloud.");
Check(EditorDataRules.KnowledgeNamesMatch("%PLAYER_NAME%'s spouse", "Miyo's spouse",
    "Miyo", "Tini"), "Player-name expansion duplicated an entry.");
Check(EditorDataRules.KnowledgeNamesMatch("%NAME%'s friend", "tini's friend",
    "Miyo", "Tini"), "NPC-name expansion duplicated an entry.");
Check(!EditorDataRules.KnowledgeNamesMatch("Miyo's spouse", "Other's spouse",
    "Miyo", "Tini"), "Distinct knowledge entries merged.");

string fixture = Path.Combine(Path.GetTempPath(), "NPCEditor-regression-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
try
{
    string file = Path.Combine(fixture, "save.json");
    AtomicFile.WriteAllText(file, "first");
    AtomicFile.WriteAllText(file, "second");
    Check(File.ReadAllText(file) == "second", "Atomic replacement failed.");
    Check(File.ReadAllText(file + ".bak") == "first", "Previous save not backed up.");
    AtomicFile.WriteAllText(file, "third");
    Check(File.ReadAllText(file + ".bak") == "second", "Backup did not advance.");
    using (var locked = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.None))
    {
        bool failed = false;
        try { AtomicFile.WriteAllText(file, "must not replace"); }
        catch (IOException) { failed = true; }
        Check(failed, "Expected locked destination write to fail on Windows.");
    }
    Check(File.ReadAllText(file) == "third", "Failed write damaged previous save.");
    Check(!Directory.GetFiles(fixture, "*.tmp-*").Any(), "Temporary file leaked.");
}
finally { Directory.Delete(fixture, recursive: true); }

// Inspect the real compiled plugin without requiring Unity's native runtime.
// This guards the original regression across ALL hooks and compiler-generated
// async/lambda bodies, rather than only testing a duplicate of the logic.
if (args.Length != 1) throw new ArgumentException("Pass the built RuntimeNPCEditor.dll path.");
using var assembly = AssemblyDefinition.ReadAssembly(args[0]);
IEnumerable<TypeDefinition> Flatten(TypeDefinition type) =>
    new[] { type }.Concat(type.NestedTypes.SelectMany(Flatten));
var types = assembly.MainModule.Types.SelectMany(Flatten).ToArray();
var methods = types.SelectMany(type => type.Methods).Where(method => method.HasBody).ToArray();
var writes = methods.SelectMany(method => method.Body.Instructions
    .Where(i => i.OpCode == OpCodes.Stfld && i.Operand is FieldReference field
        && field.DeclaringType.FullName == "NeuralNPC"
        && new[] { "relationshipLevel", "relationshipLevelDailyCap", "relationshipLevelTotalCap" }
            .Contains(field.Name)).Select(i => (method, field: (FieldReference)i.Operand))).ToArray();
Check(writes.Length == 3, "Expected only the three explicit one-time player-value writes.");
Check(writes.All(write => write.method.DeclaringType.Name == "NativeNPCEditorWindow"
    && write.method.Name == "SavePlayerRelationship"),
    "A code path other than explicit Apply can overwrite native relationship progress.");
Check(writes.Count(write => write.field.Name == "relationshipLevel") == 1,
    "A reset or background hook can overwrite the player's earned level.");
foreach (var method in methods)
{
    foreach (var instruction in method.Body.Instructions.Where(i => i.OpCode == OpCodes.Stfld
        && i.Operand is FieldReference f && f.Name == "playerRelationshipEnabled"))
        Check(instruction.Previous.OpCode == OpCodes.Ldc_I4_0,
            "A code path re-enables legacy numeric locks.");
}
var normalize = methods.Single(m => m.Name == "NormalizeSave");
Check(normalize.Body.Instructions.Any(i => i.OpCode == OpCodes.Stfld
    && i.Operand is FieldReference f && f.Name == "playerRelationshipEnabled"),
    "Legacy numeric locks are not disabled on load.");
var window = types.Single(t => t.Name == "NativeNPCEditorWindow");
Check(!window.Methods.Any(method => method.Name == "RestorePlayerRelationshipProgression"),
    "The retired repair action must not be present.");
Check(window.Fields.Single(f => f.Name == "playerRelationshipTotalCap").FieldType.Name == "Button",
    "Total cap regressed to a free-text input.");
Console.WriteLine($"PASS: {checks} checks (native numeric rules, knowledge identity, atomic files, compiled relationship write paths).");
