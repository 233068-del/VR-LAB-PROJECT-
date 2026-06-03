using UnityEngine;

/// <summary>
/// Represents a single bowling pin. Uses a dot-product tilt threshold to decide
/// whether the pin has been knocked over, then hides it after a settle delay.
/// Stores its own initial world-space transform so it can self-reset correctly.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class Pin : MonoBehaviour
{
    // ── Inspector ──────────────────────────────────────────────────────────────
    [Tooltip("Pin is considered knocked when dot(pin.up, world.up) drops below this value (0.7 ≈ ~45° tilt).")]
    [SerializeField] private float knockedDotThreshold = 0.7f;

    [Tooltip("Seconds after being flagged as knocked before this pin is hidden.")]
    [SerializeField] private float toppleLife = 3f;

    [Tooltip("Maximum number of rotation checks before giving up (one check per second).")]
    [SerializeField] private int maxTries = 10;

    // ── Cached components ──────────────────────────────────────────────────────
    private Rigidbody rb;

    // ── Stored initial world-space transform ───────────────────────────────────
    private Vector3    initialWorldPosition;
    private Quaternion initialWorldRotation;

    /// <summary>
    /// The pin's "up" direction in world space at spawn time.
    /// Because pin models can be rotated (e.g. local Z=270° so their long axis
    /// runs along local X), this is NOT necessarily Vector3.up — it is whatever
    /// direction transform.up points when the pin is perfectly upright.
    /// Tilt detection compares the CURRENT transform.up against this baseline,
    /// so it works correctly regardless of the model's initial orientation.
    /// </summary>
    private Vector3 initialWorldUp;

    // ── State ──────────────────────────────────────────────────────────────────
    private int currentTries;

    // ──────────────────────────────────────────────────────────────────────────
    // Unity lifecycle
    // ──────────────────────────────────────────────────────────────────────────

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();

        // Store world-space transform at spawn so ResetPin() always returns the
        // pin to the correct position regardless of parent moves.
        initialWorldPosition = transform.position;
        initialWorldRotation = transform.rotation;

        // Cache the "standing" direction so IsKnocked() works for any model orientation.
        // The pins in this project have local rotation z=270, so transform.up is NOT
        // Vector3.up — this baseline stores whatever world direction the pin's local Y
        // axis actually points when upright.
        initialWorldUp = transform.up;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Topple detection API
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>Starts repeating rotation checks to determine whether the pin is knocked.</summary>
    public void CheckTopple()
    {
        CancelToppleCheck();
        InvokeRepeating(nameof(CheckRotation), 0f, 1f);
    }

    /// <summary>Cancels any pending topple or hide invocations and resets the check counter.</summary>
    public void CancelToppleCheck()
    {
        currentTries = 0;
        CancelInvoke(nameof(CheckRotation));
        CancelInvoke(nameof(HidePin));
    }

    private void CheckRotation()
    {
        currentTries++;

        // A dot product < threshold means the pin's up-axis has tilted significantly
        // away from world up — the pin is considered knocked.
        if (IsKnocked())
        {
            CancelInvoke(nameof(CheckRotation));
            Invoke(nameof(HidePin), toppleLife);
        }
        else if (currentTries > maxTries)
        {
            // Gave up — pin stayed upright for the full check period.
            CancelToppleCheck();
        }
    }

    private void HidePin()
    {
        gameObject.SetActive(false);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Reset
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Snaps this pin back to its initial world-space position and rotation,
    /// zeroes its Rigidbody velocities, and re-activates the GameObject.
    /// </summary>
    public void ResetPin()
    {
        CancelToppleCheck();

        // Restore transform first so the Rigidbody wakes at the correct position.
        transform.position = initialWorldPosition;
        transform.rotation = initialWorldRotation;

        // Zero out all motion.
        rb.linearVelocity  = Vector3.zero;
        rb.angularVelocity = Vector3.zero;

        // Wake the Rigidbody so it participates in physics immediately.
        rb.WakeUp();

        gameObject.SetActive(true);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Queries
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if the pin has tipped beyond the tilt threshold.
    /// Compares the current world-up direction against the stored initial baseline
    /// so the check is correct regardless of the pin model's initial rotation.
    /// A dot value of 1.0 means perfectly upright; below knockedDotThreshold means knocked.
    /// </summary>
    public bool IsKnocked()
    {
        return Vector3.Dot(transform.up, initialWorldUp) < knockedDotThreshold;
    }

    /// <summary>Returns true when the pin is active in the scene and upright.</summary>
    public bool IsStanding => gameObject.activeSelf && !IsKnocked();
}
