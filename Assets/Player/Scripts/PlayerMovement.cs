using UnityEngine;

//runs after other scripts (like moving platforms) so the player follows them on the same frame
//the dash and grapple abilities live in PlayerMovement.Dash.cs and PlayerMovement.Grapple.cs
[DefaultExecutionOrder(100)]
public partial class PlayerMovement : MonoBehaviour
{
    [SerializeField] private bool canJump;
    [SerializeField] private float moveSpeed;
    [SerializeField] private float jumpStartingVelocity;
    [SerializeField] private float gravity;
    [SerializeField] private float fallSpeedMax;
    [SerializeField] private float jumpHeldTimerMax;
    [SerializeField] private float jumpPreloadTimerMax;
    [SerializeField] private float coyoteTimerMax;
    [SerializeField] private LayerMask jumpableMask;

    [Header("Game Feel")]
    [SerializeField] private float groundAcceleration = 80f; //how fast you speed up and stop on the ground
    [SerializeField] private float airAcceleration = 40f; //how fast you can change direction in the air
    [SerializeField] private float groundFriction = 40f; //how fast extra momentum (dash, grapple, launches) wears off on the ground
    [SerializeField] private float airDrag = 1f; //how fast extra momentum wears off in the air (low, so flings and platform jumps carry)

    [Header("Moving Platforms")]
    [SerializeField] private float platformMaxSpeed = 40f; //if the ground moves faster than this it teleported, so don't drag the player along

    private const float GroundedVelocity = -2f; //small downward velocity while grounded so the player doesn't hover
    private const float MaxStepTime = .05f; //longest time step simulated at once, so a lag spike can't fling the player
    private float jumpHeldTimer;
    private float jumpPreloadTimer;
    private float coyoteTimer;
    private bool jumping;
    private bool isGrounded;
    private bool wasGroundedLastFrame;
    private Vector3 velocity;
    private Vector3 velocityInput;
    private Vector3 velocityPhysics;
    private CharacterController controller;
    private Controls controls;
    private Vector3 startPosition;
    private Quaternion startRotation;
    private Transform groundTransform; //what the ground raycasts hit this frame
    private Transform platform; //what we are standing on and moving with (null in the air)
    private Vector3 platformLocalPosition; //where we stand on the platform, in the platform's local space
    private Quaternion platformLastRotation;
    private Vector3 platformVelocity; //how fast the platform carried us this frame



    // --for other scripts (platforms, hazards, UI)--

    /// <summary>
    /// Is the player standing on something?
    /// </summary>
    public bool IsGrounded => isGrounded;

    /// <summary>
    /// The collider's transform the player is standing on, null in the air (a platform can check player.Ground == transform)
    /// </summary>
    public Transform Ground => groundTransform;

    /// <summary>
    /// How fast the player moved last frame, not counting being carried by a platform
    /// </summary>
    public Vector3 Velocity => velocity;

    public bool IsDashing => dashing;
    public bool IsGrappling => grappleState == GrappleState.Attached;

    /// <summary>
    /// Called when the player lands on the ground, with how fast they were falling
    /// </summary>
    public event System.Action<float> Landed;

    /// <summary>
    /// Throw the player with a velocity (bounce pads, launchers...). Replaces their momentum and cancels dash and grapple
    /// </summary>
    public void Launch(Vector3 launchVelocity)
    {
        CancelDash();
        ReleaseGrapple();
        LeavePlatform(false);

        velocityPhysics = launchVelocity;
        jumping = false; //no floaty jump hold, the launch decides the arc
        coyoteTimer = 0;
    }

    /// <summary>
    /// Teleport the player back to their starting position and clear any momentum
    /// </summary>
    public void ReturnToStart()
    {
        //the character controller overrides position changes while enabled, so turn it off while teleporting
        controller.enabled = false;
        transform.SetPositionAndRotation(startPosition, startRotation);
        controller.enabled = true;

        CancelDash();
        ReleaseGrapple(true);
        platform = null; //forget the old platform, or we would get dragged back to it next frame

        velocity = Vector3.zero;
        velocityInput = Vector3.zero;
        velocityPhysics = Vector3.zero;
        jumping = false;
        jumpHeldTimer = 0;
        jumpPreloadTimer = 0;
        coyoteTimer = 0;
    }



    void Start()
    {
        //get the character controller and controls components
        controller = GetComponent<CharacterController>();
        controls = GetComponent<Controls>();

        //remember where the player started so they can be sent back there
        startPosition = transform.position;
        startRotation = transform.rotation;

        airDashesLeft = airDashesMax;
        SetUpGrapple();
    }

    void Update()
    {
        float deltaTime = Mathf.Min(Time.deltaTime, MaxStepTime);
        if (deltaTime <= 0) { //the game is paused
            return;
        }

        //platforms may have moved this frame, make sure the raycasts see where they are now
        Physics.SyncTransforms();

        // --moving platform logic-- ride along with whatever we were standing on last frame
        FollowPlatform();

        // --isGrounded logic--
        UpdateGrounded();

        // --input logic--
        if (controls.GrappleTriggered()) {
            GrapplePressed();
        }
        if (controls.DashTriggered()) {
            TryStartDash();
        }
        if (controls.JumpTriggered()) { //if jump button is pressed
            JumpPressed();
        }

        // --velocity logic--
        Vector3 velocityPhysicsStart = velocityPhysics;
        if (dashing) { //the dash takes over completely while it lasts
            velocity = dashDirection * dashSpeed;
            UpdateDash(deltaTime);
        }
        else {
            if (grappleState == GrappleState.Attached) {
                UpdateGrapplePull(deltaTime);
            }
            else {
                UpdateJumpAndGravity(deltaTime);
                ApplyMomentumDrag(deltaTime);
            }
            UpdateInputVelocity(deltaTime);

            //use the average of the physics velocity at the start and end of the frame, so jumps are the same height at any frame rate
            velocity = velocityInput + (velocityPhysicsStart + velocityPhysics) * .5f;
        }

        controller.Move(velocity * deltaTime); //move the player based on the combined velocity

        // --after moving--
        RememberPlatform();
        UpdateGrappleHook(deltaTime);

        //lower timers at the end of each frame
        jumpPreloadTimer -= deltaTime;
        coyoteTimer -= deltaTime;
        dashCooldownTimer -= deltaTime;
        grappleCooldownTimer -= deltaTime;
    }

    void JumpPressed()
    {
        if (grappleState == GrappleState.Attached) { //jumping while on the rope lets go with a hop
            GrappleJump();
        }
        else if (isGrounded) { //regular jump
            BeginJump();
        }
        else if (coyoteTimer > 0) { //coyote time jump
            BeginJump();
        }
        else { //if you are not grounded and didn't coyote jump, start the jump preload timer
            jumpPreloadTimer = jumpPreloadTimerMax;
        }
    }

    void UpdateGrounded()
    {
        groundTransform = null;
        isGrounded = RaycastTouchesGround();
        if (isGrounded && !wasGroundedLastFrame) {
            GroundEnter();
        }
        if (!isGrounded && wasGroundedLastFrame) {
            GroundExit();
        }
        wasGroundedLastFrame = isGrounded;
        if (isGrounded && velocityPhysics.y <= 0) {
            velocityPhysics.y = GroundedVelocity;
        }
    }

    void UpdateJumpAndGravity(float deltaTime)
    {
        //only apply gravity if you are not jumping or if you are jumping but the jump button is not being held down
        if (jumping && jumpHeldTimer < jumpHeldTimerMax) {
            if (controls.JumpHeld()) {
                //gravity only acts on the part of this frame after the hold time ran out, so jump height doesn't depend on frame rate
                float gravityTime = Mathf.Max(jumpHeldTimer + deltaTime - jumpHeldTimerMax, 0f);
                jumpHeldTimer += deltaTime;
                ApplyGravity(gravityTime);
            }
            else {
                jumpHeldTimer = jumpHeldTimerMax;
                ApplyGravity(deltaTime);
            }
        }
        else {
            ApplyGravity(deltaTime);
        }
    }

    void UpdateInputVelocity(float deltaTime)
    {
        Vector2 moveInput = Vector2.ClampMagnitude(controls.MoveInput(), 1f); //get move input vector and clamp to 1
        Vector3 targetVelocity = (transform.right * moveInput.x + transform.forward * moveInput.y) * moveSpeed; //the velocity the input asks for

        //speed up and slow down toward it instead of snapping, with a bit less control in the air
        float acceleration = isGrounded ? groundAcceleration : airAcceleration;
        velocityInput = Vector3.MoveTowards(velocityInput, targetVelocity, acceleration * deltaTime);
    }

    /// <summary>
    /// Extra sideways momentum (dash, grapple, launches, platforms) wears off, quickly on the ground and slowly in the air
    /// </summary>
    void ApplyMomentumDrag(float deltaTime)
    {
        Vector3 momentum = new Vector3(velocityPhysics.x, 0, velocityPhysics.z);
        momentum = Vector3.MoveTowards(momentum, Vector3.zero, (isGrounded ? groundFriction : airDrag) * deltaTime);
        velocityPhysics.x = momentum.x;
        velocityPhysics.z = momentum.z;
    }

    void ApplyGravity(float deltaTime, float gravityMultiplier = 1f)
    {
        velocityPhysics.y -= gravity * gravityMultiplier * deltaTime; //apply gravity to the physics y velocity
        if (velocityPhysics.y < -fallSpeedMax) { //make sure fall speed never exceeds fallSpeedMax
            velocityPhysics.y = -fallSpeedMax;
        }
        if (isGrounded && velocityPhysics.y < 0) { //if grounded, keep a small downward velocity so the player stays snapped to the ground
            velocityPhysics.y = GroundedVelocity;
        }
    }

    void BeginJump()
    {
        //if you can't jump, don't do anything
        if (!canJump) {
            return;
        }
        //jumping out of a dash keeps the dash momentum (dash jump)
        if (dashing) {
            EndDash();
        }

        //if jumping, set the physics y velocity to the jump starting velocity, reset timers, and set jumping to true
        velocityPhysics.y = jumpStartingVelocity;
        LeavePlatform(true); //keep the platform's speed so jumping on a moving platform lands you back on it
        jumpHeldTimer = 0;
        coyoteTimer = 0;
        jumpPreloadTimer = 0;
        jumping = true;
    }

    void EndJump(){
        jumping = false;
    }

    /// <summary>
    /// Called when you first start touching the ground
    /// </summary>
    void GroundEnter() {
        //if you are jumping and you touch the ground, end the jump
        if (jumping) {
            EndJump();
        }
        //actually landed (not just brushing a ledge on the way up)
        if (velocityPhysics.y <= 0) {
            Landed?.Invoke(-velocityPhysics.y);
            //stick the landing: leftover momentum stops so you don't slide off small platforms
            if (grappleState != GrappleState.Attached) {
                velocityPhysics.x = 0;
                velocityPhysics.z = 0;
            }
        }
        airDashesLeft = airDashesMax;
        //if I just landed on the platform right after pressing the jump button, let me jump
        if (jumpPreloadTimer > 0) {
            BeginJump();
        }
    }

    /// <summary>
    /// Called each frame you are touching the ground
    /// </summary>
    void GroundStay(RaycastHit hit)
    {
        //remember what we are standing on, so we can move with it if it's a moving platform
        groundTransform = hit.collider.transform;
    }

    /// <summary>
    /// Called when you first stop touching the ground
    /// </summary>
    void GroundExit() {
        if (!jumping && velocityPhysics.y <= 0) {
            coyoteTimer = coyoteTimerMax; //start the coyote timer if you walk off a ledge (not when jumping or launched upward)
        }
    }

    /// <summary>
    /// Move and turn with the platform we are standing on, so moving and rotating platforms carry the player
    /// </summary>
    void FollowPlatform()
    {
        platformVelocity = Vector3.zero;
        if (platform == null) {
            return;
        }
        //the platform was switched off, or the player was made its child (which already moves us)
        if (!platform.gameObject.activeInHierarchy || transform.IsChildOf(platform)) {
            platform = null;
            return;
        }

        Vector3 platformMove = platform.TransformPoint(platformLocalPosition) - transform.position;
        if (platformMove.magnitude > platformMaxSpeed * Time.deltaTime) { //the platform teleported (e.g. reset to its start), don't drag the player with it
            platform = null;
            return;
        }

        //turn with the platform (only left and right, so the camera never tilts)
        Vector3 turnedForward = platform.rotation * Quaternion.Inverse(platformLastRotation) * Vector3.forward;
        float turnAngle = Vector3.SignedAngle(Vector3.forward, Vector3.ProjectOnPlane(turnedForward, Vector3.up), Vector3.up);
        transform.Rotate(0f, turnAngle, 0f, Space.World);

        controller.Move(platformMove); //move through the character controller so the platform can't push us into walls
        platformVelocity = platformMove / Time.deltaTime;
    }

    /// <summary>
    /// Remember what we are standing on (and where on it) so we can move with it next frame
    /// </summary>
    void RememberPlatform()
    {
        if (isGrounded && velocityPhysics.y <= 0 && groundTransform != null) {
            platform = groundTransform;
            platformLocalPosition = platform.InverseTransformPoint(transform.position);
            platformLastRotation = platform.rotation;
        }
        else {
            LeavePlatform(true);
        }
    }

    /// <summary>
    /// Stop moving with the platform, optionally keeping its speed as momentum
    /// </summary>
    void LeavePlatform(bool keepPlatformSpeed)
    {
        if (platform == null) {
            return;
        }
        if (keepPlatformSpeed) {
            velocityPhysics.x += platformVelocity.x;
            velocityPhysics.z += platformVelocity.z;
            velocityPhysics.y += Mathf.Max(platformVelocity.y, 0f); //only upward speed, stepping off a sinking platform shouldn't slam you down
        }
        platform = null;
    }

    /// <summary>
    /// Called by the character controller when it bumps into something while moving
    /// </summary>
    void OnControllerColliderHit(ControllerColliderHit hit)
    {
        //floors are handled by the grounded logic
        if (hit.normal.y > .7f) {
            return;
        }
        //hitting a wall or ceiling removes the momentum pushing into it, so you don't stick to ceilings or keep grinding into walls
        float speedIntoSurface = Vector3.Dot(velocityPhysics, hit.normal);
        if (speedIntoSurface < 0) {
            velocityPhysics -= hit.normal * speedIntoSurface;
        }
        //bonking your head ends the floaty part of the jump
        if (hit.normal.y < -.7f) {
            jumpHeldTimer = jumpHeldTimerMax;
        }
    }

    /// <summary>
    /// Cast raycasts from the middle of the player and from 8 corners to test if the player is on the ground
    /// </summary>
    bool RaycastTouchesGround() {
        float rayLength = controller.height * .5f + controller.skinWidth + .05f; //reach just past the bottom of the capsule

        //test if the middle of the player is touching the ground
        if (RaycastTest(transform.position, rayLength)) {
            return true;
        }

        //test if any of the 8 corners of the player is touching the ground
        float halfWidth = transform.localScale.x * .5f;
        float diagonalWidth = transform.localScale.x * .35f;

        //test the 4 side of the player
        if (RaycastTest(transform.position + (transform.right * halfWidth), rayLength)) {
            return true;
        }
        if (RaycastTest(transform.position + (-transform.right * halfWidth), rayLength)) {
            return true;
        }
        if (RaycastTest(transform.position + (transform.forward * halfWidth), rayLength)) {
            return true;
        }
        if (RaycastTest(transform.position + (-transform.forward * halfWidth), rayLength)) {
            return true;
        }
        //test the 4 corners of the player
        if (RaycastTest(transform.position + (transform.right * diagonalWidth) + (transform.forward * diagonalWidth), rayLength)) {
            return true;
        }
        if (RaycastTest(transform.position + (transform.right * diagonalWidth) + (-transform.forward * diagonalWidth), rayLength)) {
            return true;
        }
        if (RaycastTest(transform.position + (-transform.right * diagonalWidth) + (transform.forward * diagonalWidth), rayLength)) {
            return true;
        }
        if (RaycastTest(transform.position + (-transform.right * diagonalWidth) + (-transform.forward * diagonalWidth), rayLength)) {
            return true;
        }

        //if we are not touching the ground, return false
        return false;
    }

    /// <summary>
    /// Test a single raycast to see if it hits the ground
    /// </summary>
    /// <param name="startingPoint"> Where the ray begins (it will cast down from here)</param>
    /// <param name="rayLength">How long the ray casts</param>
    bool RaycastTest(Vector3 startingPoint, float rayLength) {
        //cast a ray down from the starting point and check if it hits the ground
        RaycastHit hit;
        if (Physics.Raycast(startingPoint, transform.TransformDirection(Vector3.down), out hit, rayLength, jumpableMask, QueryTriggerInteraction.Ignore)) {
            //if it hits the ground, call the GroundStay function and return true
            GroundStay(hit);
            return true;
        }
        else {
            //if it doesn't hit the ground, return false
            return false;
        }
    }
}
