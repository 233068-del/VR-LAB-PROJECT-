using UnityEngine;
using Zinnia.Data.Collection.List;

/// <summary>
/// Populates the AllowedCollisions list on the Reset Ball and Reset Pins spatial buttons
/// so the straight pointer can hover and activate them.
/// Attach this to the BowlingGameManager GameObject (or any persistent scene object).
/// </summary>
public class ButtonSetup : MonoBehaviour
{
    private const string PointerName = "Indicators.ObjectPointers.Straight";

    private const string ResetBallCollisionListPath =
        "Reset Ball Button/Internal/Indicators.SpatialTargets.Target/SpatialTargetController/AllowedCollisions/CollisionList";

    private const string ResetPinsCollisionListPath =
        "Reset Pins Button/Internal/Indicators.SpatialTargets.Target/SpatialTargetController/AllowedCollisions/CollisionList";

    private void Awake()
    {
        GameObject pointer = GameObject.Find(PointerName);
        if (pointer == null)
        {
            Debug.LogWarning("[ButtonSetup] Pointer not found: " + PointerName);
            return;
        }

        RegisterPointerWithButton(ResetBallCollisionListPath, pointer);
        RegisterPointerWithButton(ResetPinsCollisionListPath, pointer);
    }

    /// <summary>Adds the pointer to the button's AllowedCollisions list, enabling hover and activation.</summary>
    private void RegisterPointerWithButton(string collisionListPath, GameObject pointer)
    {
        GameObject listGo = GameObject.Find(collisionListPath);
        if (listGo == null)
        {
            Debug.LogWarning("[ButtonSetup] CollisionList not found at: " + collisionListPath);
            return;
        }

        UnityObjectObservableList list = listGo.GetComponent<UnityObjectObservableList>();
        if (list == null)
        {
            Debug.LogWarning("[ButtonSetup] UnityObjectObservableList missing on: " + collisionListPath);
            return;
        }

        list.Add(pointer);
    }
}
