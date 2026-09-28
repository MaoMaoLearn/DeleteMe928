using UnityEngine;

public class PlayerCameraControl : MonoBehaviour
{

    [SerializeField] private Transform cameraHolder;
    [SerializeField] private float mouseSensitivity;
    [SerializeField] private float stickSensitivity;

    [Header("Game Feel")]
    [SerializeField] private float speedFovKick = 12f; //how much wider the view gets when moving fast (dash, grapple)
    [SerializeField] private float speedFovMinSpeed = 7f; //speed where the view starts to widen (normal running is 5)
    [SerializeField] private float speedFovMaxSpeed = 22f; //speed where the view is widest
    [SerializeField] private float fovSmoothing = 10f;
    [SerializeField] private float landingDipPerSpeed = .02f; //how far the camera dips per unit of landing speed
    [SerializeField] private float landingDipMax = .3f;
    [SerializeField] private float landingDipMinSpeed = 5f; //softer landings don't dip the camera

    private const float DipSpringStrength = 15f; //how fast the camera springs back up after a landing dip

    private float rotationX = 0f;
    private float inputX;
    private float inputY;
    private Controls controls;
    private PlayerMovement movement;
    private Camera playerCamera;
    private float baseFov;
    private Vector3 cameraHolderStartPosition;
    private float dipOffset;
    private float dipVelocity;

    void Start()
    {
        //lock mouse and disable cursor
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        //get the controls component
        controls = GetComponent<Controls>();

        //get what the game feel effects need
        movement = GetComponent<PlayerMovement>();
        movement.Landed += OnLanded;
        playerCamera = cameraHolder.GetComponentInChildren<Camera>();
        baseFov = playerCamera.fieldOfView;
        cameraHolderStartPosition = cameraHolder.localPosition;
    }

    void OnDestroy()
    {
        if (movement != null) {
            movement.Landed -= OnLanded;
        }
    }

    void Update()
    {
        //get inputX based on mouse or right stick movement
        inputX = controls.LookMouseInput().x * mouseSensitivity;
        inputX += controls.LookStickInput().x * stickSensitivity * Time.deltaTime;

        //get inputY based on mouse or right stick movement
        inputY = controls.LookMouseInput().y * mouseSensitivity;
        inputY += controls.LookStickInput().y * stickSensitivity * Time.deltaTime;

        //rotate the entire player left and right based on inputX
        transform.Rotate(Vector3.up * inputX, Space.World);

        //rotate the camera up and down based on inputY, clamped to 90 degrees up and down
        rotationX -= inputY; //invert the inputY so that moving the mouse up looks up and moving the mouse down looks down
        rotationX = Mathf.Clamp(rotationX, -90f, 90f); //clamp the rotationX to 90 degrees up and down
        cameraHolder.localRotation = Quaternion.Euler(rotationX, 0f, 0f); //rotate the camera up and down based on rotationX
    }

    void LateUpdate()
    {
        //widen the view when moving fast, for a sense of speed
        Vector3 velocity = movement.Velocity;
        float horizontalSpeed = new Vector2(velocity.x, velocity.z).magnitude;
        float targetFov = baseFov + speedFovKick * Mathf.InverseLerp(speedFovMinSpeed, speedFovMaxSpeed, horizontalSpeed);
        playerCamera.fieldOfView = Mathf.Lerp(playerCamera.fieldOfView, targetFov, 1f - Mathf.Exp(-fovSmoothing * Time.deltaTime));

        //spring the landing dip back to zero (critically damped, so it settles without bouncing)
        dipVelocity += (-dipOffset * DipSpringStrength * DipSpringStrength - dipVelocity * 2f * DipSpringStrength) * Time.deltaTime;
        dipOffset += dipVelocity * Time.deltaTime;
        cameraHolder.localPosition = cameraHolderStartPosition + Vector3.up * dipOffset;
    }

    /// <summary>
    /// Dip the camera down a little when landing, more for harder landings
    /// </summary>
    void OnLanded(float landingSpeed)
    {
        if (landingSpeed < landingDipMinSpeed) {
            return;
        }
        float dip = Mathf.Min((landingSpeed - landingDipMinSpeed) * landingDipPerSpeed, landingDipMax);
        //kick the camera downward, the spring above dips about "dip" deep before bringing it back up
        dipVelocity = -dip * DipSpringStrength * 2.718f;
    }
}
