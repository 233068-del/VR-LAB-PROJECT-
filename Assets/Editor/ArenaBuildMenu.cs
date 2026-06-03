using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEditor;
using UnityEditor.SceneManagement;
using TMPro;

public static class ArenaBuildMenu
{
    [MenuItem("Tools/Build Bowling Arena")]
    public static void BuildBowlingArena()
    {
        // BowlingAlley is the scene name, not a wrapper GameObject — parent to scene root.
        Transform parent = GameObject.Find("BowlingAlley")?.transform ?? null;

        // ── 1. WALLS + CEILING ────────────────────────────────────────────────
        var wallColor    = new Color(0.11f, 0.11f, 0.16f);
        var ceilingColor = new Color(0.08f, 0.08f, 0.12f);

        CreateCube("Arena_Wall_Left",  new Vector3(-9f, 3f,   0f), new Vector3( 0.3f, 6f,  40f),  CreateMat(wallColor,    0.1f), parent);
        CreateCube("Arena_Wall_Right", new Vector3( 9f, 3f,   0f), new Vector3( 0.3f, 6f,  40f),  CreateMat(wallColor,    0.1f), parent);
        CreateCube("Arena_Wall_Back",  new Vector3( 0f, 3f,  20f), new Vector3(18f,  6f,   0.3f), CreateMat(wallColor,    0.1f), parent);
        CreateCube("Arena_Wall_Front", new Vector3( 0f, 3f, -20f), new Vector3(18f,  6f,   0.3f), CreateMat(wallColor,    0.1f), parent);
        CreateCube("Arena_Ceiling",    new Vector3( 0f, 6f,   0f), new Vector3(18f,  0.3f, 40f),  CreateMat(ceilingColor, 0.05f), parent);

        // ── 2. ARENA FLOOR ────────────────────────────────────────────────────
        CreateCube("Arena_Floor", new Vector3(0f, -0.1f, 0f), new Vector3(18f, 0.15f, 40f),
            CreateMat(new Color(0.14f, 0.14f, 0.18f), 0.2f), parent);

        // ── 3. CHAIRS (8 chairs) ──────────────────────────────────────────────
        var chairsGo = new GameObject("Arena_Chairs");
        Undo.RegisterCreatedObjectUndo(chairsGo, "Build Arena");
        chairsGo.transform.SetParent(parent, false);
        chairsGo.transform.localPosition = new Vector3(0f, 0f, -17f);

        var seatMat = CreateMat(new Color(0.3f, 0.05f, 0.05f), 0.25f);
        for (int i = 0; i < 8; i++)
        {
            float xOffset = (i - 3.5f) * 0.72f;
            CreateCube($"Arena_Seat_{i}", new Vector3(xOffset, 0.44f, 0f),    new Vector3(0.5f, 0.08f, 0.45f), seatMat, chairsGo.transform);
            CreateCube($"Arena_Back_{i}", new Vector3(xOffset, 0.76f, 0.21f), new Vector3(0.5f, 0.52f, 0.07f), seatMat, chairsGo.transform);
        }

        // Platform / step under the chairs
        CreateCube("Arena_ChairPlatform",
            new Vector3(0f, 0f, -17f),
            new Vector3(6.5f, 0.12f, 1.1f),
            CreateMat(new Color(0.12f, 0.12f, 0.17f), 0.15f),
            parent);

        // ── 4. SCORE SCREEN ───────────────────────────────────────────────────
        var screenGo = CreateCube("Arena_Screen", new Vector3(0f, 5f, 8f), new Vector3(3.5f, 2f, 0.08f),
            CreateMat(new Color(0f, 0.04f, 0.1f), 0f, emit: true, emitCol: new Color(0f, 0.15f, 0.5f) * 2f), parent);
        screenGo.transform.localEulerAngles = new Vector3(15f, 0f, 0f);

        // "VR PROJECT" label displayed on the score screen surface
        var textGo = new GameObject("Arena_Screen_Text");
        Undo.RegisterCreatedObjectUndo(textGo, "Build Arena");
        textGo.transform.SetParent(screenGo.transform, false);
        // Place just in front of the screen's -Z face (0.7 * 0.08 scale = 0.056 m clearance)
        textGo.transform.localPosition = new Vector3(0f, 0f, -0.7f);
        // Compensate parent scale so 1 local unit == 1 world unit on X and Y
        textGo.transform.localScale    = new Vector3(1f / 3.5f, 0.5f, 1f);

        var tmp = textGo.AddComponent<TextMeshPro>();
        // Size the text rect to fill most of the screen (world units after scale compensation)
        tmp.rectTransform.sizeDelta      = new Vector2(2.8f, 1.6f);
        tmp.rectTransform.anchoredPosition = Vector2.zero;

        var font = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
        if (font != null) tmp.font = font;

        tmp.text             = "VR PROJECT";
        tmp.fontStyle        = FontStyles.Bold;
        tmp.alignment        = TextAlignmentOptions.Center;
        tmp.enableAutoSizing = true;
        tmp.fontSizeMin      = 0.5f;
        tmp.fontSizeMax      = 5f;
        tmp.color            = new Color(0.75f, 0.95f, 1f);

        // ── 5. CEILING LIGHTS (FIX 4: boosted intensity + range) ──────────────
        CreatePointLight("Arena_Light_1", new Vector3(0f, 5.5f, -8f), parent);
        CreatePointLight("Arena_Light_2", new Vector3(0f, 5.5f,  0f), parent);
        CreatePointLight("Arena_Light_3", new Vector3(0f, 5.5f,  8f), parent);

        // ── FOG ───────────────────────────────────────────────────────────────
        RenderSettings.fog              = true;
        RenderSettings.fogMode          = FogMode.Linear;
        RenderSettings.fogColor         = new Color(0.09f, 0.09f, 0.12f);
        RenderSettings.fogStartDistance = 12f;
        RenderSettings.fogEndDistance   = 32f;

        // ── AMBIENT LIGHT ─────────────────────────────────────────────────────
        RenderSettings.ambientMode  = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.1f, 0.1f, 0.15f);

        // ════════════════════════════════════════════════════════════════════════
        // NEW ADDITIONS
        // ════════════════════════════════════════════════════════════════════════

        // ── 6. NEON WALL STRIPS (FIX 1+2: corrected emission flags + brighter) ─
        var neonMat = CreateMat(
            new Color(0.05f, 0f, 0.15f), smooth: 0.5f,
            emit: true, emitCol: new Color(0.4f, 0f, 1f) * 4f);

        CreateCube("Arena_NeonLeft",  new Vector3(-8.7f, 0.12f, 0f), new Vector3(0.06f, 0.14f, 38f), neonMat, parent);
        CreateCube("Arena_NeonRight", new Vector3( 8.7f, 0.12f, 0f), new Vector3(0.06f, 0.14f, 38f), neonMat, parent);

        // FIX 3: boosted neon point lights
        CreateCustomPointLight("Arena_NeonLight_Left",
            new Vector3(-8.7f, 0.35f, 0f),
            new Color(0.45f, 0.05f, 1f), intensity: 1.2f, range: 5f, shadows: LightShadows.None,
            parent);
        CreateCustomPointLight("Arena_NeonLight_Right",
            new Vector3( 8.7f, 0.35f, 0f),
            new Color(0.45f, 0.05f, 1f), intensity: 1.2f, range: 5f, shadows: LightShadows.None,
            parent);

        // ── 7. LANE SPOTLIGHTS (FIX 5: aimed at pin deck) ─────────────────────
        // Positioned above the lane, angled ~22° downward toward the pin area (z ≈ 18).
        CreateSpotLight("Arena_Spot_1", new Vector3(-1.5f, 5.5f, 2f), new Vector3(22f, 0f, 0f),
            new Color(1f, 0.95f, 0.85f), intensity: 4f, range: 15f, spotAngle: 35f, parent);
        CreateSpotLight("Arena_Spot_2", new Vector3( 1.5f, 5.5f, 2f), new Vector3(22f, 0f, 0f),
            new Color(1f, 0.95f, 0.85f), intensity: 4f, range: 15f, spotAngle: 35f, parent);

        // ── 8. SIDE SCOREBOARD SCREEN ─────────────────────────────────────────
        var sideScreenMat = CreateMat(new Color(0f, 0.03f, 0.08f), 0f,
            emit: true, emitCol: new Color(0f, 0.1f, 0.4f) * 1.8f);
        var sideScreen = CreateCube("Arena_SideScreen",
            new Vector3(-8.6f, 3.8f, -5f),
            new Vector3(0.08f, 1.6f, 2.8f),
            sideScreenMat, parent);
        sideScreen.transform.localEulerAngles = new Vector3(0f, 90f, 0f);

        // Thin border strips parented to the screen so rotation is inherited automatically.
        var borderMat = CreateMat(new Color(0f, 0.02f, 0.06f), 0.5f,
            emit: true, emitCol: new Color(0f, 0.4f, 1f) * 2f);
        CreateCube("Arena_SideScreen_BorderTop",
            new Vector3(0f,  0.825f,  0f),    new Vector3(0.04f, 0.05f, 2.9f), borderMat, sideScreen.transform);
        CreateCube("Arena_SideScreen_BorderBottom",
            new Vector3(0f, -0.825f,  0f),    new Vector3(0.04f, 0.05f, 2.9f), borderMat, sideScreen.transform);
        CreateCube("Arena_SideScreen_BorderLeft",
            new Vector3(0f,  0f,      1.425f), new Vector3(0.04f, 1.7f, 0.05f), borderMat, sideScreen.transform);
        CreateCube("Arena_SideScreen_BorderRight",
            new Vector3(0f,  0f,     -1.425f), new Vector3(0.04f, 1.7f, 0.05f), borderMat, sideScreen.transform);

        // ── 9. BALL RETURN RACK ───────────────────────────────────────────────
        var rackGo = new GameObject("Arena_BallRack");
        Undo.RegisterCreatedObjectUndo(rackGo, "Build Arena");
        rackGo.transform.SetParent(parent, false);
        rackGo.transform.localPosition = new Vector3(6f, 0f, -14f);

        var chromeMat = CreateMetallicMat(new Color(0.55f, 0.55f, 0.6f), metallic: 0.9f, smoothness: 0.85f);
        CreateCube("Arena_Rack_Base",  new Vector3(     0f,    0f, 0f), new Vector3(0.9f,  0.08f, 0.55f), chromeMat, rackGo.transform);
        CreateCube("Arena_Rack_PostL", new Vector3(-0.38f, 0.36f, 0f), new Vector3(0.07f, 0.65f, 0.07f), chromeMat, rackGo.transform);
        CreateCube("Arena_Rack_PostR", new Vector3( 0.38f, 0.36f, 0f), new Vector3(0.07f, 0.65f, 0.07f), chromeMat, rackGo.transform);
        CreateCube("Arena_Rack_Rail",  new Vector3(     0f, 0.68f, 0f), new Vector3(0.9f,  0.07f, 0.07f), chromeMat, rackGo.transform);

        // ── 10. SIDE WALL TVs ─────────────────────────────────────────────────
        var tvMat = CreateMat(new Color(0f, 0.02f, 0.06f), 0f,
            emit: true, emitCol: new Color(0f, 0.2f, 0.7f) * 3f);
        var tvBorderMat = CreateMat(new Color(0f, 0.1f, 0.3f), 0.5f,
            emit: true, emitCol: new Color(0f, 0.5f, 1f) * 3f);

        // Left TV
        var tvLeft = CreateCube("Arena_TV_Left",
            new Vector3(-8.65f, 3.2f, 2f),
            new Vector3(0.08f, 1.4f, 2.4f),
            tvMat, parent);
        tvLeft.transform.localEulerAngles = Vector3.zero;
        CreateCube("Arena_TV_Left_BorderTop",
            new Vector3(0f,  0.73f,  0f),  new Vector3(0.06f, 0.05f, 2.45f), tvBorderMat, tvLeft.transform);
        CreateCube("Arena_TV_Left_BorderBottom",
            new Vector3(0f, -0.73f,  0f),  new Vector3(0.06f, 0.05f, 2.45f), tvBorderMat, tvLeft.transform);
        CreateCube("Arena_TV_Left_BorderLeft",
            new Vector3(0f,  0f,     1.23f), new Vector3(0.06f, 1.45f, 0.05f), tvBorderMat, tvLeft.transform);
        CreateCube("Arena_TV_Left_BorderRight",
            new Vector3(0f,  0f,    -1.23f), new Vector3(0.06f, 1.45f, 0.05f), tvBorderMat, tvLeft.transform);
        CreateCustomPointLight("Arena_TV_Light_Left",
            new Vector3(-7.8f, 3.2f, 2f),
            new Color(0f, 0.4f, 1f), intensity: 0.5f, range: 3f, shadows: LightShadows.None, parent);

        // Right TV
        var tvRight = CreateCube("Arena_TV_Right",
            new Vector3(8.65f, 3.2f, 2f),
            new Vector3(0.08f, 1.4f, 2.4f),
            tvMat, parent);
        tvRight.transform.localEulerAngles = Vector3.zero;
        CreateCube("Arena_TV_Right_BorderTop",
            new Vector3(0f,  0.73f,  0f),  new Vector3(0.06f, 0.05f, 2.45f), tvBorderMat, tvRight.transform);
        CreateCube("Arena_TV_Right_BorderBottom",
            new Vector3(0f, -0.73f,  0f),  new Vector3(0.06f, 0.05f, 2.45f), tvBorderMat, tvRight.transform);
        CreateCube("Arena_TV_Right_BorderLeft",
            new Vector3(0f,  0f,     1.23f), new Vector3(0.06f, 1.45f, 0.05f), tvBorderMat, tvRight.transform);
        CreateCube("Arena_TV_Right_BorderRight",
            new Vector3(0f,  0f,    -1.23f), new Vector3(0.06f, 1.45f, 0.05f), tvBorderMat, tvRight.transform);
        CreateCustomPointLight("Arena_TV_Light_Right",
            new Vector3(7.8f, 3.2f, 2f),
            new Color(0f, 0.4f, 1f), intensity: 0.5f, range: 3f, shadows: LightShadows.None, parent);

        // ── 11. ENTRANCE BANNERS ──────────────────────────────────────────────
        CreateCube("Arena_Banner_Left",
            new Vector3(-4f, 4.5f, -17f),
            new Vector3(0.06f, 3.5f, 0.9f),
            CreateMat(new Color(0.15f, 0f, 0f), 0f,
                emit: true, emitCol: new Color(0.8f, 0f, 0.1f) * 1.8f),
            parent);
        CreateCube("Arena_Banner_Right",
            new Vector3(4f, 4.5f, -17f),
            new Vector3(0.06f, 3.5f, 0.9f),
            CreateMat(new Color(0f, 0f, 0.15f), 0f,
                emit: true, emitCol: new Color(0f, 0.2f, 0.9f) * 1.8f),
            parent);

        // ── 12. DECORATIVE CEILING BEAM ───────────────────────────────────────
        CreateCube("Arena_CeilingBeam",
            new Vector3(0f, 5.8f, -13f),
            new Vector3(18f, 0.4f, 0.5f),
            CreateMat(new Color(0.15f, 0.15f, 0.22f), 0.4f),
            parent);
        CreateCube("Arena_BeamGlow",
            new Vector3(0f, 5.58f, -13f),
            new Vector3(17f, 0.05f, 0.35f),
            CreateMat(new Color(0f, 0.05f, 0.2f), 0f,
                emit: true, emitCol: new Color(0.2f, 0.4f, 1f) * 2f),
            parent);

        // ── 13. ENTRANCE POINT LIGHT ──────────────────────────────────────────
        CreateCustomPointLight("Arena_EntranceLight",
            new Vector3(0f, 4.5f, -16f),
            new Color(1f, 0.85f, 0.6f), intensity: 2f, range: 8f, shadows: LightShadows.None,
            parent);

        // ── 14. CEILING EDGE GLOW STRIPS ─────────────────────────────────────
        var ceilGlowMat = CreateMat(new Color(0.05f, 0.08f, 0.15f), 0f,
            emit: true, emitCol: new Color(0.4f, 0.6f, 1f) * 1.8f);
        CreateCube("Arena_CeilGlow_Left",
            new Vector3(-8.8f, 6.6f, 0f),
            new Vector3(0.1f, 0.08f, 40f),
            ceilGlowMat, parent);
        CreateCube("Arena_CeilGlow_Right",
            new Vector3(8.8f, 6.6f, 0f),
            new Vector3(0.1f, 0.08f, 40f),
            ceilGlowMat, parent);

        // ── MARK DIRTY ────────────────────────────────────────────────────────
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        Debug.Log("[ArenaBuildMenu] Arena built! In Scene view: click the sun icon in the Scene toolbar " +
                  "to toggle 'Illuminate Scene' — make sure it is ON. " +
                  "Also check the small light bulb icon is enabled. Press Ctrl+S to save.");
    }

    [MenuItem("Tools/Clear Bowling Arena")]
    public static void ClearBowlingArena()
    {
        var all = GameObject.FindObjectsByType<GameObject>(FindObjectsSortMode.None);
        foreach (var g in all)
        {
            if (g != null && g.name.StartsWith("Arena_"))
                Undo.DestroyObjectImmediate(g);
        }

        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        Debug.Log("[ArenaBuildMenu] Cleared Arena_ objects. Press Ctrl+S to save.");
    }

    // ── HELPERS ───────────────────────────────────────────────────────────────

    /// <summary>Creates a Cube primitive, strips its Collider, assigns material and shadow settings, and registers Undo.</summary>
    private static GameObject CreateCube(string objName, Vector3 localPos, Vector3 localScale, Material mat, Transform parentTransform)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = objName;

        Object.DestroyImmediate(go.GetComponent<Collider>());

        go.transform.SetParent(parentTransform, false);
        go.transform.localPosition = localPos;
        go.transform.localScale    = localScale;

        var mr = go.GetComponent<MeshRenderer>();
        mr.sharedMaterial    = mat;
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows    = false;

        Undo.RegisterCreatedObjectUndo(go, "Build Arena");
        return go;
    }

    /// <summary>Creates a warm ceiling Point Light at boosted intensity and registers Undo.</summary>
    private static void CreatePointLight(string lightName, Vector3 localPos, Transform parentTransform)
    {
        var lgo = new GameObject(lightName);
        lgo.transform.SetParent(parentTransform, false);
        lgo.transform.localPosition = localPos;

        var lt = lgo.AddComponent<Light>();
        lt.type      = LightType.Point;
        lt.color     = new Color(1f, 0.9f, 0.7f);
        lt.intensity = 3.5f;   // FIX 4: was 2.5f
        lt.range     = 13f;    // FIX 4: was 10f
        lt.shadows   = LightShadows.Soft;

        Undo.RegisterCreatedObjectUndo(lgo, "Build Arena");
    }

    /// <summary>Creates a Point Light with fully custom parameters and registers Undo.</summary>
    private static void CreateCustomPointLight(string lightName, Vector3 localPos, Color color,
        float intensity, float range, LightShadows shadows, Transform parentTransform)
    {
        var lgo = new GameObject(lightName);
        lgo.transform.SetParent(parentTransform, false);
        lgo.transform.localPosition = localPos;

        var lt = lgo.AddComponent<Light>();
        lt.type      = LightType.Point;
        lt.color     = color;
        lt.intensity = intensity;
        lt.range     = range;
        lt.shadows   = shadows;

        Undo.RegisterCreatedObjectUndo(lgo, "Build Arena");
    }

    /// <summary>Creates a Spot Light aimed by localEulerAngles and registers Undo.</summary>
    private static void CreateSpotLight(string lightName, Vector3 localPos, Vector3 localEuler,
        Color color, float intensity, float range, float spotAngle, Transform parentTransform)
    {
        var lgo = new GameObject(lightName);
        lgo.transform.SetParent(parentTransform, false);
        lgo.transform.localPosition    = localPos;
        lgo.transform.localEulerAngles = localEuler;

        var lt = lgo.AddComponent<Light>();
        lt.type      = LightType.Spot;
        lt.color     = color;
        lt.intensity = intensity;
        lt.range     = range;
        lt.spotAngle = spotAngle;
        lt.shadows   = LightShadows.None;

        Undo.RegisterCreatedObjectUndo(lgo, "Build Arena");
    }

    /// <summary>
    /// Creates a Standard material with color, smoothness, optional metallic, and optional emission.
    /// Setting globalIlluminationFlags to RealtimeEmissive ensures emission is visible in both
    /// Scene view and Play mode, not just the editor preview.
    /// </summary>
    private static Material CreateMat(Color col, float smooth, float metallic = 0f,
        bool emit = false, Color emitCol = default)
    {
        var m = new Material(Shader.Find("Standard"));
        m.color = col;
        m.SetFloat("_Glossiness", smooth);
        m.SetFloat("_Metallic",   metallic);
        if (emit)
        {
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", emitCol);
            // Critical: without this flag emission shows in the editor but not in Play mode.
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        }
        return m;
    }

    /// <summary>Creates a Standard material tuned for metallic/chrome props.</summary>
    private static Material CreateMetallicMat(Color c, float metallic, float smoothness)
    {
        var m = new Material(Shader.Find("Standard"));
        m.color = c;
        m.SetFloat("_Metallic",   metallic);
        m.SetFloat("_Glossiness", smoothness);
        return m;
    }
}
