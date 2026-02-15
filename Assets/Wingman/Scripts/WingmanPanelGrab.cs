using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.UI;

/// <summary>
/// Handles grab interaction for the Wingman Panel.
/// The panel always follows the user's head (like the tutorial component).
/// Grabbing lets the user reposition it within their HUD — the offset
/// relative to the camera is updated on release so it continues following
/// at the new screen position (e.g. top-right instead of center).
///
/// IMPORTANT: The Rigidbody is kept KINEMATIC when not grabbed. LazyFollow
/// sets transform.position directly in LateUpdate — a non-kinematic Rigidbody
/// fights with the physics engine every frame, causing panels to slowly
/// drift away from the camera when the head is stationary.
/// XRGrabInteractable saves/restores the kinematic state automatically.
/// </summary>
[RequireComponent(typeof(XRGrabInteractable))]
[RequireComponent(typeof(LazyFollow))]
public class WingmanPanelGrab : MonoBehaviour
{
    LazyFollow m_LazyFollow;
    XRGrabInteractable m_GrabInteractable;
    Rigidbody m_Rigidbody;

    // Cache the follow modes so we can restore them after grab
    LazyFollow.PositionFollowMode m_SavedPositionMode;
    LazyFollow.RotationFollowMode m_SavedRotationMode;

    void Awake()
    {
        m_LazyFollow = GetComponent<LazyFollow>();
        m_GrabInteractable = GetComponent<XRGrabInteractable>();
        m_Rigidbody = GetComponent<Rigidbody>();

        // Make kinematic immediately so physics never fights LazyFollow.
        // XRGrabInteractable caches wasKinematic on grab start, so it will
        // save true, flip to false for VelocityTracking, then restore true
        // on release. This must happen in Awake (before XRGrabInteractable
        // reads the initial state).
        if (m_Rigidbody != null)
        {
            m_Rigidbody.isKinematic = true;
            m_Rigidbody.linearVelocity = Vector3.zero;
            m_Rigidbody.angularVelocity = Vector3.zero;
        }
    }

    void OnEnable()
    {
        m_GrabInteractable.selectEntered.AddListener(OnGrabbed);
        m_GrabInteractable.selectExited.AddListener(OnReleased);
    }

    void OnDisable()
    {
        m_GrabInteractable.selectEntered.RemoveListener(OnGrabbed);
        m_GrabInteractable.selectExited.RemoveListener(OnReleased);
    }

    void OnGrabbed(SelectEnterEventArgs args)
    {
        // Save current follow modes
        m_SavedPositionMode = m_LazyFollow.positionFollowMode;
        m_SavedRotationMode = m_LazyFollow.rotationFollowMode;

        // Pause LazyFollow entirely so it doesn't fight the grab movement
        m_LazyFollow.positionFollowMode = LazyFollow.PositionFollowMode.None;
        m_LazyFollow.rotationFollowMode = LazyFollow.RotationFollowMode.None;

        // XRGrabInteractable handles isKinematic = false for VelocityTracking
    }

    void OnReleased(SelectExitEventArgs args)
    {
        // Compute new offset: where is the panel now relative to the camera?
        var cam = Camera.main;
        if (cam != null)
        {
            // Convert the panel's world position to the camera's local space
            // This gives us the offset the user chose by repositioning
            Vector3 localOffset = cam.transform.InverseTransformPoint(transform.position);
            m_LazyFollow.targetOffset = localOffset;
        }

        // Kill any residual velocity and force kinematic so physics
        // doesn't fight LazyFollow. XRGrabInteractable may have already
        // restored isKinematic=true (from wasKinematic), but we enforce
        // it as a safety net.
        if (m_Rigidbody != null)
        {
            m_Rigidbody.linearVelocity = Vector3.zero;
            m_Rigidbody.angularVelocity = Vector3.zero;
            m_Rigidbody.isKinematic = true;
        }

        // Re-enable follow so the panel continues tracking the user's head
        // at its new HUD position
        m_LazyFollow.positionFollowMode = m_SavedPositionMode;
        m_LazyFollow.rotationFollowMode = m_SavedRotationMode;
    }
}
