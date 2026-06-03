using UnityEngine;
using UnityEngine.SpatialTracking;
using Tilia.Input.UnityInputManager;
using Tilia.Interactions.Interactables.Interactables;
using Tilia.Interactions.Interactables.Interactors;
using Zinnia.Action;

/// <summary>
/// Simulates a VR headset and two controllers using keyboard and mouse so the bowling
/// game can be played without a physical VR headset.
///
/// HOW TO THROW:
///   1. Press G (or LMB) to grab — the ball snaps to your hand automatically.
///   2. Hold E to build forward swing speed (camera remains fully controllable).
///   3. Release G to throw.
///
/// CONTROLS:
///   Tab         - Cycle modes: Head/Body → Right Hand → Left Hand
///   Escape      - Toggle cursor lock
///
///   Head/Body mode (default, and while holding the ball):
///     Mouse     - Look around (head rotation) — works even while grabbing
///     WASD      - Move player along the floor plane
///     E / Q     - Push / pull the grabbed hand forward or backward
///
///   Right/Left Hand modes (use Tab to enter):
///     Mouse X/Y - Move selected hand horizontally and vertically
///     Q / E     - Pull hand back / push hand forward
///     R         - Reset hand to its default offset
///
///   Always:
///     G or LMB  - Right Grip: auto-snaps hand to ball and grabs; release to throw
///     B         - Left Grip (secondary grab)
/// </summary>
public class VRSimulator : MonoBehaviour
{
    private const float DefaultHeadHeight = 1.6f;

    [Header("Movement")]
    [SerializeField] private float moveSpeed = 3f;
    [SerializeField] private float lookSensitivity = 2f;

    [Header("Hand Control")]
    [SerializeField] private float handMouseSensitivity = 0.005f;
    [SerializeField] private float handKeySpeed = 0.05f;
    [SerializeField] private Vector3 rightHandDefaultOffset = new Vector3(0.3f, -0.3f, 0.5f);
    [SerializeField] private Vector3 leftHandDefaultOffset  = new Vector3(-0.3f, -0.3f, 0.5f);

    // ── Rig transforms ────────────────────────────────────────────────────────
    private Transform playArea;
    private Transform headAnchor;
    private Transform leftHandAnchor;
    private Transform rightHandAnchor;

    // ── Interactors ───────────────────────────────────────────────────────────
    private InteractorFacade rightInteractor;
    private InteractorFacade leftInteractor;

    // ── Grab actions (Facade.GrabAction — driven directly each frame) ─────────
    private BooleanAction rightGrabAction;
    private BooleanAction leftGrabAction;

    // ── Interactable objects in the scene ─────────────────────────────────────
    private InteractableFacade ballInteractable;

    // ── Grab state ────────────────────────────────────────────────────────────
    private bool isRightGrabbing;
    private bool isLeftGrabbing;

    // ── Simulation state ──────────────────────────────────────────────────────
    private float headPitch;
    private float headYaw;
    private Vector3 currentRightOffset;
    private Vector3 currentLeftOffset;

    /// <summary>0 = Head/Body, 1 = Right Hand, 2 = Left Hand</summary>
    private int controlMode;

    /// <summary>True when either hand is currently grabbing the ball.</summary>
    public bool IsGrabbing => isRightGrabbing || isLeftGrabbing;

    /// <summary>Controls visibility of the on-screen controls overlay. Hidden by default; toggle with H.</summary>
    private bool showOverlay = false;

    private static readonly string[] ModeLabels = { "Head / Body", "Right Hand", "Left Hand" };

    // ──────────────────────────────────────────────────────────────────────────
    // Unity lifecycle
    // ──────────────────────────────────────────────────────────────────────────

    private void Start()
    {
        // Rig transforms
        playArea        = FindRequiredTransform("CameraRigs.UnityXR");
        headAnchor      = FindRequiredTransform("CameraRigs.UnityXR/HeadAnchor");
        leftHandAnchor  = FindRequiredTransform("CameraRigs.UnityXR/LeftHandAnchor");
        rightHandAnchor = FindRequiredTransform("CameraRigs.UnityXR/RightHandAnchor");

        // Interactors
        rightInteractor = FindInteractor(
            "CameraRigs.TrackedAlias/Aliases/RightControllerAlias/Interactions.Interactor");
        leftInteractor = FindInteractor(
            "CameraRigs.TrackedAlias/Aliases/LeftControllerAlias/Interactions.Interactor");

        // Cache the Facade GrabActions — we drive these directly so the Tilia
        // source chain fires exactly as a real controller press would.
        rightGrabAction = rightInteractor?.GrabAction;
        leftGrabAction  = leftInteractor?.GrabAction;

        if (rightGrabAction == null)
            Debug.LogWarning("[VRSimulator] Right GrabAction not found. Grab will not work.");
        if (leftGrabAction == null)
            Debug.LogWarning("[VRSimulator] Left GrabAction not found. Grab will not work.");

        // Ball interactable
        var ballGO = GameObject.Find("InteractableBall");
        if (ballGO != null)
            ballInteractable = ballGO.GetComponent<InteractableFacade>();
        else
            Debug.LogWarning("[VRSimulator] 'InteractableBall' not found in scene.");

        // Disable OpenVR input readers so hardware-input actions stop competing.
        DisableOpenVRInputComponents("Input.UnityInputManager.OpenVR.RightController");
        DisableOpenVRInputComponents("Input.UnityInputManager.OpenVR.LeftController");

        // Stop XR device pose tracking — we drive transforms directly.
        SetTrackedPoseDriverEnabled(headAnchor,      false);
        SetTrackedPoseDriverEnabled(leftHandAnchor,  false);
        SetTrackedPoseDriverEnabled(rightHandAnchor, false);

        // Initialise head at standing height.
        if (headAnchor != null)
        {
            headAnchor.localPosition = new Vector3(0f, DefaultHeadHeight, 0f);
            headAnchor.localRotation = Quaternion.identity;
        }

        currentRightOffset = rightHandDefaultOffset;
        currentLeftOffset  = leftHandDefaultOffset;

        Cursor.lockState = CursorLockMode.Locked;

        Debug.Log("[VRSimulator] Active — G/LMB: grab & throw | E: push forward | Tab: mode | Esc: cursor.");
    }

    private void Update()
    {
        HandleCursorToggle();
        if (Cursor.lockState != CursorLockMode.Locked)
            return;

        HandleModeSwitch();

        float mouseX = Input.GetAxis("Mouse X");
        float mouseY = Input.GetAxis("Mouse Y");

        switch (controlMode)
        {
            case 0:
                RotateHead(mouseX, mouseY);
                MoveBody();
                // Q/E still drive the active hand's depth so the user can swing
                // and throw while keeping full camera control.
                if (isRightGrabbing)      AdjustHandDepth(ref currentRightOffset);
                else if (isLeftGrabbing)  AdjustHandDepth(ref currentLeftOffset);
                break;
            case 1:
                MoveHand(ref currentRightOffset, mouseX, mouseY, rightHandDefaultOffset);
                break;
            case 2:
                MoveHand(ref currentLeftOffset, mouseX, mouseY, leftHandDefaultOffset);
                break;
        }

        // Apply transforms before grab so the hand is at the correct world
        // position before the grab action fires.
        ApplyHandTransforms();
        HandleGrabbing();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Input handling
    // ──────────────────────────────────────────────────────────────────────────

    private void HandleCursorToggle()
    {
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Cursor.lockState = Cursor.lockState == CursorLockMode.Locked
                ? CursorLockMode.None
                : CursorLockMode.Locked;
        }
    }

    private void HandleModeSwitch()
    {
        if (Input.GetKeyDown(KeyCode.Tab))
        {
            controlMode = (controlMode + 1) % ModeLabels.Length;
            Debug.Log($"[VRSimulator] Mode → {ModeLabels[controlMode]}");
        }

        if (Input.GetKeyDown(KeyCode.H))
            showOverlay = !showOverlay;
    }

    private void RotateHead(float mouseX, float mouseY)
    {
        headYaw   += mouseX * lookSensitivity;
        headPitch  = Mathf.Clamp(headPitch - mouseY * lookSensitivity, -80f, 80f);
        if (headAnchor != null)
            headAnchor.localRotation = Quaternion.Euler(headPitch, headYaw, 0f);
    }

    private void MoveBody()
    {
        if (playArea == null || headAnchor == null)
            return;

        float h = Input.GetAxis("Horizontal");
        float v = Input.GetAxis("Vertical");
        Vector3 forward = Vector3.ProjectOnPlane(headAnchor.forward, Vector3.up).normalized;
        Vector3 right   = Vector3.Cross(Vector3.up, forward);
        playArea.position += (forward * v + right * h) * (moveSpeed * Time.deltaTime);
    }

    private void MoveHand(ref Vector3 offset, float mouseX, float mouseY, Vector3 defaultOffset)
    {
        offset.x += mouseX * handMouseSensitivity;
        offset.y += mouseY * handMouseSensitivity;
        if (Input.GetKey(KeyCode.Q)) offset.z -= handKeySpeed;
        if (Input.GetKey(KeyCode.E)) offset.z += handKeySpeed;
        if (Input.GetKeyDown(KeyCode.R)) offset = defaultOffset;
    }

    /// <summary>
    /// Drives the Facade's GrabAction BooleanAction directly each frame.
    ///
    /// Why not call <c>interactor.Grab(ball)</c>?
    /// <c>Grab()</c> calls <c>ProcessGrabAction</c> which does a one-shot
    /// <c>GrabAction.Receive(true)</c> on the *internal* action. Because the
    /// Facade's GrabAction is that internal action's <em>source</em>, its
    /// persistent false value bleeds back through the source chain and releases
    /// the ball on the very next frame. Driving the Facade's GrabAction directly
    /// every frame keeps the source value true for as long as the key is held —
    /// exactly mirroring real controller behaviour.
    /// </summary>
    private void HandleGrabbing()
    {
        bool rightGrip = Input.GetKey(KeyCode.G) || Input.GetMouseButton(0);
        bool leftGrip  = Input.GetKey(KeyCode.B);

        // ── Right hand ──────────────────────────────────────────────────────
        if (rightGrip && !isRightGrabbing)
        {
            StartGrab(rightInteractor, rightGrabAction, ref currentRightOffset,
                      ref isRightGrabbing, "Right");
        }
        else if (!rightGrip && isRightGrabbing)
        {
            EndGrab(rightInteractor, rightGrabAction, ref isRightGrabbing, "Right");
        }

        // Re-assert every frame so no source can silently reset the value.
        if (isRightGrabbing && rightGrabAction != null)
            rightGrabAction.Receive(true);

        // ── Left hand ───────────────────────────────────────────────────────
        if (leftGrip && !isLeftGrabbing)
        {
            StartGrab(leftInteractor, leftGrabAction, ref currentLeftOffset,
                      ref isLeftGrabbing, "Left");
        }
        else if (!leftGrip && isLeftGrabbing)
        {
            EndGrab(leftInteractor, leftGrabAction, ref isLeftGrabbing, "Left");
        }

        if (isLeftGrabbing && leftGrabAction != null)
            leftGrabAction.Receive(true);
    }

    /// <summary>
    /// Auto-snaps the hand to the ball, simulates a touch so Tilia's touching
    /// list is populated, then drives the GrabAction true so the natural Tilia
    /// grab event chain fires (source → internal action → Activated → publish).
    /// </summary>
    private void StartGrab(InteractorFacade interactor, BooleanAction grabAction,
                           ref Vector3 handOffset, ref bool isGrabbing, string label)
    {
        if (interactor == null || grabAction == null || ballInteractable == null)
            return;

        // Snap this hand to the ball's world position so the ball stays where
        // it is instead of teleporting to wherever the hand happens to be.
        if (headAnchor != null)
        {
            handOffset = headAnchor.InverseTransformPoint(ballInteractable.transform.position);
            ApplyHandTransforms();
        }

        // Populate the interactor's active-collisions list so Tilia knows what
        // to grab when the GrabAction fires.
        interactor.SimulateTouch(ballInteractable);

        // Drive the GrabAction true — propagates via the source chain to the
        // internal GrabAction, fires its Activated event, and publishes the grab.
        grabAction.Receive(true);

        isGrabbing = true;

        // Do NOT auto-switch mode — the user can keep camera control (mode 0)
        // and use Q/E to swing the ball forward for the throw.

        Debug.Log($"[VRSimulator] {label} hand grabbed ball. Hold E to swing, release {(label == "Right" ? "G" : "B")} to throw.");
    }

    private void EndGrab(InteractorFacade interactor, BooleanAction grabAction,
                         ref bool isGrabbing, string label)
    {
        if (grabAction != null)
            grabAction.Receive(false);

        if (interactor != null && ballInteractable != null)
            interactor.SimulateUntouch(ballInteractable);

        isGrabbing = false;
        Debug.Log($"[VRSimulator] {label} hand released — ball thrown.");
    }

    /// <summary>Adjusts forward/back depth of a hand offset via Q/E. Used in mode 0 while grabbing.</summary>
    private void AdjustHandDepth(ref Vector3 offset)
    {
        if (Input.GetKey(KeyCode.Q)) offset.z -= handKeySpeed;
        if (Input.GetKey(KeyCode.E)) offset.z += handKeySpeed;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Transform application
    // ──────────────────────────────────────────────────────────────────────────

    private void ApplyHandTransforms()
    {
        if (headAnchor == null)
            return;

        if (rightHandAnchor != null)
        {
            rightHandAnchor.position = headAnchor.TransformPoint(currentRightOffset);
            rightHandAnchor.rotation = headAnchor.rotation;
        }

        if (leftHandAnchor != null)
        {
            leftHandAnchor.position = headAnchor.TransformPoint(currentLeftOffset);
            leftHandAnchor.rotation = headAnchor.rotation;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Setup helpers
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>Disables all Tilia axis and button input readers under the named root.</summary>
    private static void DisableOpenVRInputComponents(string rootName)
    {
        var root = GameObject.Find(rootName);
        if (root == null)
        {
            Debug.LogWarning($"[VRSimulator] Input root not found: {rootName}");
            return;
        }

        foreach (var c in root.GetComponentsInChildren<UnityInputManagerAxis1DAction>(true))
            c.enabled = false;
        foreach (var c in root.GetComponentsInChildren<UnityInputManagerAxis2DAction>(true))
            c.enabled = false;
        foreach (var c in root.GetComponentsInChildren<UnityInputManagerButtonAction>(true))
            c.enabled = false;
    }

    private static void SetTrackedPoseDriverEnabled(Transform target, bool enabled)
    {
        if (target == null)
            return;
        var driver = target.GetComponent<TrackedPoseDriver>();
        if (driver != null)
            driver.enabled = enabled;
    }

    private static Transform FindRequiredTransform(string path)
    {
        var go = GameObject.Find(path);
        if (go == null)
            Debug.LogWarning($"[VRSimulator] Could not find transform: '{path}'");
        return go?.transform;
    }

    private static InteractorFacade FindInteractor(string path)
    {
        var go = GameObject.Find(path);
        if (go == null)
        {
            Debug.LogWarning($"[VRSimulator] Interactor GameObject not found: '{path}'");
            return null;
        }
        var facade = go.GetComponent<InteractorFacade>();
        if (facade == null)
            Debug.LogWarning($"[VRSimulator] No InteractorFacade on: '{path}'");
        return facade;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // HUD
    // ──────────────────────────────────────────────────────────────────────────

    private void OnGUI()
    {
        if (!showOverlay)
            return;

        const float Width  = 300f;
        const float Height = 160f;

        string grabLine = IsGrabbing
            ? "HOLDING BALL  —  E: swing forward, Q: pull back"
            : "G / LMB: grab ball  |  B: left grab";

        GUI.Box(new Rect(10f, 10f, Width, Height),
            $"VR Simulator  |  Mode: {ModeLabels[controlMode]}\n" +
            "─────────────────────────────────\n" +
            $"{grabLine}\n" +
            "Tab: cycle mode    Esc: cursor\n" +
            "Head: WASD=move  Mouse=look\n" +
            "Hand mode: Mouse=X/Y, Q/E=depth\n" +
            "Throw: grab → hold E → release G");
    }
}
