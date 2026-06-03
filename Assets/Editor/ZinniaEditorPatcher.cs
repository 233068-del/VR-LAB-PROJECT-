using System.IO;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

/// <summary>
/// One-time startup patcher that fixes the NullReferenceException in Zinnia 2.0.0's
/// CollapsibleUnityEventDrawer when running under Unity 6+.
///
/// Root cause: Unity 6 removed the internal <c>DrawerKeySet</c> nested type from
/// <c>ScriptAttributeUtility</c>. The old code chained GetNestedType().GetField()
/// without a null check, so GetNestedType returning null caused a NRE.
///
/// Fix (mirrors commit 2ce9cef in Zinnia.Unity):
///   - Guard with a null check on <c>utilityType</c>.
///   - Split the chained call into two steps and guard <c>drawerKeySet</c> for null.
///
/// After patching the cached file the script requests a recompile. On the next domain
/// reload the fixed code runs cleanly and this patcher becomes a no-op.
/// </summary>
[InitializeOnLoad]
public static class ZinniaEditorPatcher
{
    private const string DrawerFileName = "CollapsibleUnityEventDrawer.cs";
    private const string DrawerSubPath = "Editor/Data/Type/" + DrawerFileName;
    private const string PatchedMarker = "if (drawerKeySet == null)";
    private const string BrokenChainMarker = "FieldInfo drawerField = utilityType";
    private const string GetNestedTypeMarker = ".GetNestedType(\"DrawerKeySet\"";
    private const string GetFieldMarker = ".GetField(\"drawer\"";

    static ZinniaEditorPatcher()
    {
        TryPatchZinniaDrawer();
    }

    /// <summary>
    /// Locates the Zinnia package cache entry, reads the drawer file, and applies
    /// the null-guard fix if not already present.
    /// </summary>
    private static void TryPatchZinniaDrawer()
    {
        string cacheDir = Path.Combine(
            Path.GetDirectoryName(Application.dataPath),
            "Library", "PackageCache");

        if (!Directory.Exists(cacheDir))
            return;

        string[] zinniaDirectories = Directory.GetDirectories(cacheDir, "io.extendreality.zinnia.unity*");
        if (zinniaDirectories.Length == 0)
            return;

        string filePath = Path.Combine(zinniaDirectories[0], DrawerSubPath);
        if (!File.Exists(filePath))
            return;

        string content = File.ReadAllText(filePath);

        // Already patched — nothing to do.
        if (content.Contains(PatchedMarker))
            return;

        string patched = ApplyNullGuards(content);
        if (patched == null)
        {
            Debug.LogWarning("[ZinniaEditorPatcher] Could not locate the broken pattern. " +
                             "The Zinnia package may have already been updated or the file structure changed.");
            return;
        }

        try
        {
            File.WriteAllText(filePath, patched, System.Text.Encoding.UTF8);
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[ZinniaEditorPatcher] Failed to write patched file: {ex.Message}");
            return;
        }

        Debug.Log("[ZinniaEditorPatcher] Zinnia CollapsibleUnityEventDrawer.cs patched for Unity 6 " +
                  "compatibility. Triggering recompile...");
        CompilationPipeline.RequestScriptCompilation();
    }

    /// <summary>
    /// Applies two null-guard patches to <paramref name="source"/>.
    /// Returns the patched string, or <c>null</c> if the expected pattern was not found.
    /// </summary>
    private static string ApplyNullGuards(string source)
    {
        // Normalise line endings so all IndexOf calls use '\n'.
        string text = source.Replace("\r\n", "\n").Replace("\r", "\n");

        // ── Patch 1: null-guard for utilityType ───────────────────────────────
        const string utilityTypeLine = "System.Type utilityType = System.Type.GetType(\"UnityEditor.ScriptAttributeUtility, UnityEditor\");";
        int utilityIdx = text.IndexOf(utilityTypeLine);
        if (utilityIdx >= 0)
        {
            int lineEnd = text.IndexOf('\n', utilityIdx);
            if (lineEnd < 0)
                lineEnd = text.Length - 1;

            // Determine indentation of the utilityType line.
            int lineStart = text.LastIndexOf('\n', utilityIdx) + 1;
            string indent = GetIndent(text, lineStart);

            string nullGuard =
                $"\n{indent}if (utilityType == null)\n" +
                $"{indent}{{\n" +
                $"{indent}    return;\n" +
                $"{indent}}}";

            text = text.Substring(0, lineEnd) + nullGuard + text.Substring(lineEnd);
        }

        // ── Patch 2: break the chained GetNestedType().GetField() call ────────
        int chainStart = text.IndexOf(BrokenChainMarker);
        if (chainStart < 0)
            return null;

        int getNestedIdx = text.IndexOf(GetNestedTypeMarker, chainStart);
        if (getNestedIdx < 0)
            return null;

        int getFieldIdx = text.IndexOf(GetFieldMarker, getNestedIdx);
        if (getFieldIdx < 0)
            return null;

        int stmtEnd = text.IndexOf(';', getFieldIdx);
        if (stmtEnd < 0)
            return null;

        stmtEnd += 1; // include the semicolon

        // Determine indentation of the FieldInfo drawerField line.
        int chainLineStart = text.LastIndexOf('\n', chainStart) + 1;
        string chainIndent = GetIndent(text, chainLineStart);

        string fixedChain =
            $"System.Type drawerKeySet = utilityType.GetNestedType(\"DrawerKeySet\", " +
            $"BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);\n" +
            $"{chainIndent}if (drawerKeySet == null)\n" +
            $"{chainIndent}{{\n" +
            $"{chainIndent}    return;\n" +
            $"{chainIndent}}}\n" +
            $"{chainIndent}FieldInfo drawerField = drawerKeySet.GetField(\"drawer\", " +
            $"BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);";

        text = text.Substring(0, chainStart) + fixedChain + text.Substring(stmtEnd);

        // Restore original line endings.
        if (source.Contains("\r\n"))
            text = text.Replace("\n", "\r\n");

        return text;
    }

    /// <summary>Returns the leading whitespace of the line starting at <paramref name="lineStart"/>.</summary>
    private static string GetIndent(string text, int lineStart)
    {
        int i = lineStart;
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t'))
            i++;
        return text.Substring(lineStart, i - lineStart);
    }
}
