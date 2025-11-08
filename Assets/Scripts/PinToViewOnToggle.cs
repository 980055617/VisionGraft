using UnityEngine;

/// <summary>
/// Toggles an object so that a button press snaps it in front of the user,
/// and it stays there until the button is pressed again.
/// </summary>
public class PinToViewOnToggle : MonoBehaviour
{
    [Header("References")]
    [SerializeField] Transform centerEye;

    [Header("Placement")]
    [SerializeField, Range(0.2f, 3f)] float distance = 0.6f;
    [SerializeField] bool matchRotation = true;

    [Header("Input")]
    [SerializeField] OVRInput.Button triggerButton = OVRInput.Button.One; // A button on the right controller

    void Awake()
    {
        ResolveCenterEye();
    }

    void OnEnable()
    {
        ResolveCenterEye();
    }

    void Update()
    {
        if (!OVRInput.GetDown(triggerButton)) return;
        if (!centerEye && !ResolveCenterEye()) return;

        PinToCurrentView();
    }

    bool ResolveCenterEye()
    {
        if (centerEye) return true;

        var rig = FindObjectOfType<OVRCameraRig>();
        if (rig && rig.centerEyeAnchor)
        {
            centerEye = rig.centerEyeAnchor;
            return true;
        }

        if (Camera.main)
        {
            centerEye = Camera.main.transform;
            return true;
        }

        return false;
    }

    void PinToCurrentView()
    {
        // Place the object in front of the user and keep its world-space pose.
        transform.position = centerEye.position + centerEye.forward * distance;

        if (matchRotation)
            transform.rotation = Quaternion.LookRotation(centerEye.forward, Vector3.up);

        transform.SetParent(null, true);
    }
}
