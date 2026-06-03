using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using Zinnia.Tracking.Collision;

/// <summary>
/// Reflects the bowling ball's velocity off a bumper wall using the real contact
/// normal, preserving 80 % of the original speed. Also plays a thud AudioClip
/// and triggers haptic feedback on both controllers.
///
/// Usage:
///   Attach to the bumper parent GameObject. Wire each bumper's Zinnia
///   CollisionTracker.CollisionStarted UnityEvent → this Bounce() method.
///
/// Enable / disable via BowlingGameManager.SetBumpersEnabled(bool).
/// </summary>
public class Bumper : MonoBehaviour
{
    // ── Inspector ──────────────────────────────────────────────────────────────
    [Tooltip("Fraction of speed kept after bouncing. 1.0 = no energy loss; 0.8 = 20 % loss.")]
    [SerializeField] private float bounceDamping = 0.8f;

    [Tooltip("One-shot clip played when the ball strikes a bumper.")]
    [SerializeField] private AudioClip thadClip;

    [Header("Haptics")]
    [Tooltip("Vibration amplitude sent to the controller on a bumper hit (0–1).")]
    [SerializeField] private float hapticAmplitude = 0.3f;
    [Tooltip("Duration in seconds of the haptic pulse on a bumper hit.")]
    [SerializeField] private float hapticDuration = 0.1f;

    // ── Cached components ──────────────────────────────────────────────────────
    private AudioSource audioSource;

    // ──────────────────────────────────────────────────────────────────────────
    // Unity lifecycle
    // ──────────────────────────────────────────────────────────────────────────

    private void Awake()
    {
        // Ensure an AudioSource exists for the thud clip.
        if (!TryGetComponent(out audioSource))
            audioSource = gameObject.AddComponent<AudioSource>();

        audioSource.playOnAwake = false;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Zinnia collision event handler
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Wire this to the bumper's Zinnia CollisionTracker.CollisionStarted event.
    /// Reflects the ball velocity off the contact normal and applies energy damping.
    /// Fully null-safe — safe to call even when trigger events fire.
    /// </summary>
    public void Bounce(CollisionNotifier.EventData data)
    {
        // ── Guard 1: EventData itself ──────────────────────────────────────────
        if (data == null)
        {
            Debug.LogWarning("[Bumper] Bounce received null EventData — skipped.");
            return;
        }

        // ── Guard 2: ColliderData — the collider we collided with ──────────────
        if (data.ColliderData == null)
        {
            Debug.LogWarning("[Bumper] Bounce: ColliderData is null — skipped.");
            return;
        }

        // ── Guard 3: CollisionData — null for trigger events ───────────────────
        Collision collision = data.CollisionData;
        if (collision == null)
        {
            // Trigger events carry no physics Collision struct — silently ignore.
            return;
        }

        // ── Resolve the ball Rigidbody null-safely ─────────────────────────────
        Rigidbody rb = collision.rigidbody;
        if (rb == null)
        {
            // Fallback: try the other collider's attached Rigidbody.
            if (collision.contactCount == 0) return;
            if (!collision.GetContact(0).otherCollider.TryGetComponent(out rb)) return;
        }

        // ── Compute reflected velocity ─────────────────────────────────────────
        // Use the actual contact normal so angled bumpers reflect correctly.
        // Fall back to world-right (the safe axis for a straight lane) if no contacts.
        Vector3 normal = Vector3.right;
        if (collision.contactCount > 0)
            normal = collision.GetContact(0).normal;

        // Vector3.Reflect returns the incoming velocity mirrored about the normal,
        // scaled by bounceDamping to lose a fraction of energy on each bounce.
        rb.linearVelocity = Vector3.Reflect(rb.linearVelocity, normal) * bounceDamping;

        // ── Notify BallController for haptics (preferred path) ─────────────────
        if (rb.TryGetComponent<BallController>(out BallController ball))
        {
            ball.OnBumperHit();
        }
        else
        {
            // Fallback: pulse haptics directly if BallController is absent.
            SendHapticPulse(hapticAmplitude, hapticDuration);
        }

        // ── Play thud audio ────────────────────────────────────────────────────
        if (thadClip != null)
            audioSource.PlayOneShot(thadClip);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Haptics — Unity XR API (Unity 6 compatible)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>Sends a haptic impulse to both controllers.</summary>
    internal static void SendHapticPulse(float amplitude, float duration)
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
