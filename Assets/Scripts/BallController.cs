using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using Tilia.Interactions.Interactables.Interactables;
using Tilia.Interactions.Interactables.Interactors;
using Zinnia.Tracking.Velocity;

/// <summary>
/// Drives all bowling ball physics and VR interaction behaviour.
///
/// Responsibilities:
///   • Applies correct Rigidbody mass, drag, and lane constraints.
///   • Samples the interactor's VelocityTracker at release to compute throw velocity.
///   • Clamps launch speed between <see cref="minLaunchSpeed"/> and <see cref="maxLaunchSpeed"/>.
///   • Applies Y-axis hook spin based on the lateral hand-swipe direction at release.
///   • Adds rolling torque each frame so the ball looks like it is truly rolling.
///   • Detects gutter entry and out-of-bounds, then resets the ball after a delay.
///   • Drives a looping rolling AudioSource (pitch and volume proportional to speed).
///   • Sends haptic pulses on grab, throw, pin hit, and bumper hit.
///
/// Setup:
///   Attach to InteractableBall. Assign <see cref="spawnPoint"/> (the empty
///   Transform that marks the ball's resting position). Assign a PhysicMaterial
///   (dynamicFriction 0.3, staticFriction 0.35, bounciness 0.1) to the ball's
///   SphereCollider in the Inspector.
///   Tag the lane floor GameObject with "Lane" so the rolling audio and stop
///   detection work correctly.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(AudioSource))]
public class BallController : MonoBehaviour
{
    // ── Physics constants ──────────────────────────────────────────────────────
    /// <summary>Real bowling ball mass in kilograms.</summary>
    private const float BallMass        = 4.5f;
    /// <summary>Linear drag (air resistance) — low because a ball rolls, not flies.</summary>
    private const float BallDrag        = 0.02f;
    /// <summary>Angular drag — low so spin is maintained for the hook effect.</summary>
    private const float BallAngularDrag = 0.05f;

    // ── Inspector — Throw Physics ──────────────────────────────────────────────
    [Header("Throw Physics")]
    [Tooltip("Multiplier applied to the tracked hand velocity at the moment of release.")]
    [SerializeField] private float throwForceScalar = 1.5f;
    [Tooltip("Ball always launches at least this fast (m/s) so it always reaches the pins.")]
    [SerializeField] private float minLaunchSpeed = 2f;
    [Tooltip("Ball never launches faster than this (m/s) to prevent it flying off the lane.")]
    [SerializeField] private float maxLaunchSpeed = 12f;
    [Tooltip("Ball is treated as stopped when its speed (m/s) drops below this while on the lane.")]
    [SerializeField] private float stoppedSpeedThreshold = 0.3f;
    [Tooltip("Torque (N·m) applied per unit of forward speed to simulate genuine rolling.")]
    [SerializeField] private float rollingTorqueScale = 10f;
    [Tooltip("Y-axis angular velocity scale applied from lateral hand swipe for hook/curve effect.")]
    [SerializeField] private float hookTorqueScale = 5f;
    [Tooltip("Minimum lateral hand swipe speed (m/s) required to trigger a hook effect.")]
    [SerializeField] private float hookSwipeThreshold = 0.5f;

    // ── Inspector — Bounds & Reset ─────────────────────────────────────────────
    [Header("Bounds & Reset")]
    [Tooltip("Ball is considered out of bounds if it falls below this Y world position.")]
    [SerializeField] private float minY = -1f;
    [Tooltip("Seconds to wait before teleporting the ball back after going out of bounds or in the gutter.")]
    [SerializeField] private float outOfBoundsResetDelay = 1.5f;

    // ── Inspector — Audio ──────────────────────────────────────────────────────
    [Header("Audio")]
    [Tooltip("Looping clip played while the ball rolls on the lane surface.")]
    [SerializeField] private AudioClip rollingClip;
    [Tooltip("One-shot clip played when the ball resets to its spawn point.")]
    [SerializeField] private AudioClip resetClip;
    [Tooltip("Ball must exceed this speed (m/s) while on the lane to play rolling audio.")]
    [SerializeField] private float rollingSpeedThreshold = 0.5f;
    [Tooltip("AudioSource pitch at maximum launch speed.")]
    [SerializeField] private float maxRollingPitch = 1.8f;
    [Tooltip("AudioSource volume at maximum launch speed.")]
    [SerializeField] private float maxRollingVolume = 1f;
    [Tooltip("Rate at which rolling audio volume fades out when the ball decelerates.")]
    [SerializeField] private float audioFadeSpeed = 3f;

    // ── Inspector — Haptics ────────────────────────────────────────────────────
    [Header("Haptics")]
    [SerializeField] private float grabHapticAmplitude    = 0.2f;
    [SerializeField] private float grabHapticDuration     = 0.05f;
    [SerializeField] private float throwHapticAmplitude   = 0.5f;
    [SerializeField] private float throwHapticDuration    = 0.1f;
    [SerializeField] private float pinHitHapticAmplitude  = 0.4f;
    [SerializeField] private float pinHitHapticDuration   = 0.08f;
    [SerializeField] private float bumperHapticAmplitude  = 0.3f;
    [SerializeField] private float bumperHapticDuration   = 0.1f;

    // ── Inspector — Scene References ───────────────────────────────────────────
    [Header("References")]
    [Tooltip("VRTK InteractableFacade on this ball. Auto-found if not assigned.")]
    [SerializeField] private InteractableFacade interactable;
    [Tooltip("Transform at the ball's resting/spawn position. Ball teleports here on reset.")]
    [SerializeField] private Transform spawnPoint;

    // ── Cached components ──────────────────────────────────────────────────────
    private Rigidbody       rb;
    private AudioSource     audioSource;
    private BowlingGameManager gameManager;

    // ── Queries ────────────────────────────────────────────────────────────────
    /// <summary>
    /// Current ball speed in m/s. Polled every frame by BowlingGameManager to drive
    /// WaitingForThrow → BallInMotion and BallInMotion → PinsSettling transitions.
    /// </summary>
    public float CurrentSpeed => rb != null ? rb.linearVelocity.magnitude : 0f;

    // ── Flight / grab state ────────────────────────────────────────────────────
    private bool isGrabbed;
    private bool isInFlight;
    private bool isResetting;
    /// <summary>True while the ball's collider is in contact with a "Lane"-tagged surface.</summary>
    private bool isOnLane;

    // ── Initial spawn transform (fallback when spawnPoint is not assigned) ────
    private Vector3    initialPosition;
    private Quaternion initialRotation;

    // ── Interactor & velocity tracking ────────────────────────────────────────
    /// <summary>The interactor currently holding the ball — null when not grabbed.</summary>
    private InteractorFacade activeInteractor;
    private Vector3 prevHandPosition;
    /// <summary>Smoothed hand velocity in world space, sampled each frame while grabbing.</summary>
    private Vector3 handSwipeVelocity;

    // ──────────────────────────────────────────────────────────────────────────
    // Unity lifecycle
    // ──────────────────────────────────────────────────────────────────────────

    private void Awake()
    {
        rb          = GetComponent<Rigidbody>();
        audioSource = GetComponent<AudioSource>();

        // Cache the world-space transform at scene load so TeleportToSpawn()
        // always has a valid destination even when spawnPoint is not assigned.
        initialPosition = transform.position;
        initialRotation = transform.rotation;

        // Apply bowling ball physics properties.
        rb.mass                   = BallMass;
        rb.linearDamping          = BallDrag;
        rb.angularDamping         = BallAngularDrag;
        rb.interpolation          = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.Continuous;

        // Apply lane constraints at startup so the ball rolls straight before the first grab.
        RestoreLaneConstraints();
    }

    private void Start()
    {
        gameManager = FindAnyObjectByType<BowlingGameManager>();

        // Auto-find InteractableFacade on this same GameObject if not assigned.
        if (interactable == null)
            interactable = GetComponent<InteractableFacade>();

        if (interactable != null)
        {
            interactable.FirstGrabbed.AddListener(OnGrabbed);
            interactable.LastUngrabbed.AddListener(OnReleased);
        }
        else
        {
            Debug.LogWarning("[BallController] No InteractableFacade found on this GameObject. Grab events will not fire.");
        }

        // Prepare the rolling AudioSource as a continuously looping source at zero volume.
        // Volume and pitch are driven in Update() so the fade is smooth.
        if (audioSource != null)
        {
            audioSource.loop        = true;
            audioSource.playOnAwake = false;
            audioSource.volume      = 0f;

            if (rollingClip != null)
            {
                audioSource.clip = rollingClip;
                audioSource.Play(); // Start silently; volume ramps up when rolling.
            }
        }
    }

    private void OnDestroy()
    {
        // Clean up event subscriptions to avoid callbacks after destruction.
        if (interactable != null)
        {
            interactable.FirstGrabbed.RemoveListener(OnGrabbed);
            interactable.LastUngrabbed.RemoveListener(OnReleased);
        }
    }

    private void Update()
    {
        if (isGrabbed)
        {
            // Track hand motion for the hook effect.
            TrackHandSwipe();
            return;
        }

        if (isInFlight && !isResetting)
        {
            ApplyRollingTorque();
            UpdateRollingAudio();
            CheckStoppedOrOutOfBounds();
        }
        else
        {
            FadeOutRollingAudio();
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // VRTK grab / release events
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Called by InteractableFacade.FirstGrabbed.
    /// Removes lane constraints so the ball can be freely held, begins hand tracking.
    /// </summary>
    private void OnGrabbed(InteractorFacade interactor)
    {
        isGrabbed   = true;
        isInFlight  = false;
        isResetting = false;

        // Remove all constraints so the player can position the ball freely.
        rb.constraints = RigidbodyConstraints.None;

        // Cache the interactor to sample its VelocityTracker at release.
        activeInteractor  = interactor;
        prevHandPosition  = interactor != null ? interactor.transform.position : transform.position;
        handSwipeVelocity = Vector3.zero;

        // Pulse haptics to confirm the grab (light, quick).
        SendHapticPulse(grabHapticAmplitude, grabHapticDuration);
    }

    /// <summary>
    /// Called by InteractableFacade.LastUngrabbed.
    /// Samples hand velocity, clamps speed, applies hook spin, and launches the ball.
    /// </summary>
    private void OnReleased(InteractorFacade interactor)
    {
        isGrabbed = false;

        Vector3 throwVelocity = SampleThrowVelocity();
        float   rawSpeed      = throwVelocity.magnitude;

        if (rawSpeed < 0.01f)
        {
            // Ball was set down rather than thrown — keep it in place.
            RestoreLaneConstraints();
            activeInteractor = null;
            return;
        }

        // Clamp final speed between the configured minimum and maximum.
        float clampedSpeed = Mathf.Clamp(rawSpeed * throwForceScalar, minLaunchSpeed, maxLaunchSpeed);
        throwVelocity = throwVelocity.normalized * clampedSpeed;

        // Apply velocity directly (no AddForce) for immediate responsive feel.
        rb.linearVelocity = throwVelocity;

        // Apply hook / curve spin BEFORE restoring freeze constraints.
        ApplyHookSpin();

        // Re-apply lane constraints after hook spin is baked in.
        RestoreLaneConstraints();

        isInFlight       = true;
        activeInteractor = null;

        // Throw haptic: heavier pulse to signal the release.
        SendHapticPulse(throwHapticAmplitude, throwHapticDuration);

        gameManager?.OnBallThrown(clampedSpeed);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Physics helpers
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Clears all Rigidbody constraints so the ball can roll freely down the lane (Z-axis).
    /// Position and rotation are left unconstrained — the lane surface and bumper colliders
    /// provide the physical boundary. Only called when the ball is not being held.
    /// </summary>
    private void RestoreLaneConstraints()
    {
        rb.constraints = RigidbodyConstraints.None;
    }

    /// <summary>
    /// Returns the throw velocity from the interactor's VelocityTracker.
    /// Falls back to the accumulated hand-swipe velocity if the tracker is unavailable.
    /// </summary>
    private Vector3 SampleThrowVelocity()
    {
        if (activeInteractor != null)
        {
            VelocityTracker tracker = activeInteractor.VelocityTracker;
            if (tracker != null)
                return tracker.GetVelocity();
        }

        // Fallback — last known hand velocity from TrackHandSwipe().
        return handSwipeVelocity;
    }

    /// <summary>Records the hand's velocity each frame while the ball is held.</summary>
    private void TrackHandSwipe()
    {
        if (activeInteractor == null) return;

        Vector3 currentPos = activeInteractor.transform.position;
        // Prevent divide-by-zero on the very first frame after grab.
        float dt = Mathf.Max(Time.deltaTime, 0.0001f);
        handSwipeVelocity = (currentPos - prevHandPosition) / dt;
        prevHandPosition  = currentPos;
    }

    /// <summary>
    /// Adds Y-axis angular velocity to simulate a hook or curve effect.
    /// Triggered when the lateral (X-axis) component of the hand swipe exceeds the threshold.
    /// </summary>
    private void ApplyHookSpin()
    {
        float lateralSwipe = handSwipeVelocity.x;
        if (Mathf.Abs(lateralSwipe) < hookSwipeThreshold) return;

        Vector3 av = rb.angularVelocity;
        av.y = lateralSwipe * hookTorqueScale;
        rb.angularVelocity = av;
    }

    /// <summary>
    /// Applies torque around the ball's local right axis proportional to forward speed,
    /// so the ball visually rolls rather than sliding.
    /// ForceMode.Acceleration ignores mass — keeps the effect consistent regardless of ball weight.
    /// </summary>
    private void ApplyRollingTorque()
    {
        float forwardSpeed = Vector3.Dot(rb.linearVelocity, transform.forward);
        rb.AddTorque(transform.right * (forwardSpeed * rollingTorqueScale), ForceMode.Acceleration);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Stopped / out-of-bounds detection
    // ──────────────────────────────────────────────────────────────────────────

    private void CheckStoppedOrOutOfBounds()
    {
        float speed = rb.linearVelocity.magnitude;

        // Ball has come to rest on the lane surface.
        if (isOnLane && speed < stoppedSpeedThreshold)
        {
            Debug.Log("[BallController] Ball stopped on lane — beginning reset sequence.");
            BeginNaturalStop();
            return;
        }

        // Ball has fallen below the lane (pit, back wall gap, etc.).
        if (transform.position.y < minY)
        {
            Debug.Log("[BallController] Ball fell out of bounds — beginning reset sequence.");
            BeginOutOfBoundsReset();
        }
    }

    /// <summary>Natural stop on the lane: notify game manager and reset the ball.</summary>
    private void BeginNaturalStop()
    {
        isInFlight  = false;
        isResetting = true;
        gameManager?.OnBallStopped();
        StartResetCoroutine(0f);
    }

    /// <summary>Ball left the lane (fell off). Reset after delay.</summary>
    private void BeginOutOfBoundsReset()
    {
        isInFlight  = false;
        isResetting = true;
        gameManager?.OnBallStopped();
        StartResetCoroutine(outOfBoundsResetDelay);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Public callbacks from other components
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Called by <see cref="GutterTrigger"/> when the ball enters a gutter channel.
    /// Zeros velocity, marks it as a gutter ball, and starts the reset sequence.
    /// </summary>
    public void OnGutterEntered()
    {
        if (isResetting || !isInFlight) return;

        Debug.Log("[BallController] Ball entered gutter — gutter ball!");
        rb.linearVelocity  = Vector3.zero;
        rb.angularVelocity = Vector3.zero;

        isInFlight  = false;
        isResetting = true;

        gameManager?.OnGutterBall();
        StartResetCoroutine(outOfBoundsResetDelay);
    }

    /// <summary>
    /// Called by <see cref="Bumper"/> when the ball bounces off a bumper wall.
    /// Pulses haptic feedback on the dominant controller.
    /// </summary>
    public void OnBumperHit()
    {
        SendHapticPulse(bumperHapticAmplitude, bumperHapticDuration);
    }

    /// <summary>
    /// Call this when the ball makes first contact with the pin deck.
    /// Pulses haptic feedback and starts topple-checks on all pins.
    /// </summary>
    public void OnPinHit()
    {
        SendHapticPulse(pinHitHapticAmplitude, pinHitHapticDuration);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Reset
    // ──────────────────────────────────────────────────────────────────────────

    private void StartResetCoroutine(float delay)
    {
        StopAllCoroutines();
        StartCoroutine(ResetAfterDelay(delay));
    }

    private IEnumerator ResetAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        TeleportToSpawn();
    }

    /// <summary>
    /// Teleports the ball to the spawn point with zero velocity.
    /// Public so the BowlingGameManager and Reset Ball Button can invoke this directly.
    /// </summary>
    public void TeleportToSpawn()
    {
        StopAllCoroutines();

        // Zero all motion first.
        rb.linearVelocity  = Vector3.zero;
        rb.angularVelocity = Vector3.zero;

        // Determine target position and rotation.
        // Prefer the assigned spawnPoint Transform; fall back to the world position captured
        // at Awake() so the ball always returns to where it started in the scene.
        Vector3    targetPosition = spawnPoint != null ? spawnPoint.position : initialPosition;
        Quaternion targetRotation = spawnPoint != null ? spawnPoint.rotation : initialRotation;

        // Set both the Transform and the Rigidbody position together.
        // With RigidbodyInterpolation.Interpolate enabled, setting only rb.position schedules
        // the move for the next physics tick and the visual transform lags behind.
        // Setting transform.position directly gives an immediate visual snap, while
        // rb.position / rb.rotation keep the physics body in sync.
        // Physics.SyncTransforms() then flushes the change so the collider moves instantly too.
        transform.position = targetPosition;
        transform.rotation = targetRotation;
        rb.position        = targetPosition;
        rb.rotation        = targetRotation;
        Physics.SyncTransforms();

        // Restore lane constraints.
        RestoreLaneConstraints();

        isGrabbed   = false;
        isInFlight  = false;
        isResetting = false;

        // Play the soft reset sound.
        if (resetClip != null && audioSource != null)
            audioSource.PlayOneShot(resetClip);

        // Re-enable the interactable so the player can pick the ball up again.
        if (interactable != null && !interactable.gameObject.activeSelf)
            interactable.gameObject.SetActive(true);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Lane contact tracking (for stop detection and rolling audio)
    // ──────────────────────────────────────────────────────────────────────────

    private void OnCollisionEnter(Collision col)
    {
        if (col.gameObject.tag == "Lane")
            isOnLane = true;
    }

    private void OnCollisionExit(Collision col)
    {
        if (col.gameObject.tag == "Lane")
            isOnLane = false;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Rolling audio
    // ──────────────────────────────────────────────────────────────────────────

    private void UpdateRollingAudio()
    {
        if (audioSource == null || rollingClip == null) return;

        float speed = rb.linearVelocity.magnitude;

        if (isOnLane && speed > rollingSpeedThreshold)
        {
            // Remap speed to [0, 1] then apply to volume and pitch.
            float t = Mathf.InverseLerp(rollingSpeedThreshold, maxLaunchSpeed, speed);
            audioSource.volume = Mathf.Lerp(0f, maxRollingVolume, t);
            audioSource.pitch  = Mathf.Lerp(0.8f, maxRollingPitch, t);
        }
        else
        {
            FadeOutRollingAudio();
        }
    }

    private void FadeOutRollingAudio()
    {
        if (audioSource == null) return;
        audioSource.volume = Mathf.MoveTowards(audioSource.volume, 0f, audioFadeSpeed * Time.deltaTime);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Haptics — Unity XR API (Unity 6 compatible)
    // ──────────────────────────────────────────────────────────────────────────

    private static void SendHapticPulse(float amplitude, float duration)
    {
        SendHapticToNode(XRNode.RightHand, amplitude, duration);
        SendHapticToNode(XRNode.LeftHand,  amplitude, duration);
    }

    private static void SendHapticToNode(XRNode node, float amplitude, float duration)
    {
        var devices = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(node, devices);
        foreach (InputDevice device in devices)
            device.SendHapticImpulse(0, amplitude, duration);
    }
}
