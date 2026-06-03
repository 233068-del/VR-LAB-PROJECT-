using UnityEngine;

/// <summary>
/// Attach this to a trigger collider placed in each gutter channel.
/// When the bowling ball enters the trigger, it is marked as a gutter ball:
/// its forward momentum is zeroed and the ball reset sequence begins.
/// The collider on this GameObject must have <see cref="Collider.isTrigger"/> = true.
/// </summary>
public class GutterTrigger : MonoBehaviour
{
    private void Awake()
    {
        // Guarantee this collider is set as a trigger at runtime, regardless of Inspector setting.
        if (TryGetComponent<Collider>(out Collider col))
            col.isTrigger = true;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (other == null) return;

        // Try to find BallController on the entering collider's GameObject.
        if (!other.TryGetComponent<BallController>(out BallController ball))
        {
            // Ball's root may be a parent if the collider is on a child object.
            ball = other.GetComponentInParent<BallController>();
        }

        if (ball == null) return;

        ball.OnGutterEntered();
    }
}
