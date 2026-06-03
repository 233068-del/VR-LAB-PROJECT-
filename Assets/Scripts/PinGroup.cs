using UnityEngine;

/// <summary>
/// Manages the collection of bowling pins as a unit.
/// Exposes reset, topple-check, and standing-count operations.
/// </summary>
public class PinGroup : MonoBehaviour
{
    // ── Private state ──────────────────────────────────────────────────────────
    private Pin[] pins;

    // ──────────────────────────────────────────────────────────────────────────
    // Unity lifecycle
    // ──────────────────────────────────────────────────────────────────────────

    private void Awake()
    {
        // Include inactive pins (already-knocked ones) in the collection.
        pins = GetComponentsInChildren<Pin>(true);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Public API
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>Total number of pins managed by this group.</summary>
    public int TotalPins => pins != null ? pins.Length : 0;

    /// <summary>Returns the number of pins that are currently active (not knocked/hidden).</summary>
    public int CountStanding()
    {
        int count = 0;
        if (pins == null) return count;

        foreach (Pin pin in pins)
        {
            if (pin != null && pin.gameObject.activeSelf)
                count++;
        }
        return count;
    }

    /// <summary>
    /// Resets every pin to its initial position, re-enables it, and zeroes its Rigidbody.
    /// Uses Pin.ResetPin() which handles null-safe Rigidbody access internally.
    /// </summary>
    public void ResetPositions()
    {
        if (pins == null) return;

        foreach (Pin pin in pins)
        {
            if (pin == null) continue;
            pin.ResetPin();
        }
    }

    /// <summary>Starts topple-checking on every active pin (call when ball reaches the pin deck).</summary>
    public void StartToppleChecks()
    {
        if (pins == null) return;

        foreach (Pin pin in pins)
        {
            if (pin != null && pin.gameObject.activeSelf)
                pin.CheckTopple();
        }
    }

    /// <summary>Cancels all pending topple and hide invocations on every pin.</summary>
    public void CancelAllToppleChecks()
    {
        if (pins == null) return;

        foreach (Pin pin in pins)
        {
            if (pin != null)
                pin.CancelToppleCheck();
        }
    }
}
