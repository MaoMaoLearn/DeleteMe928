using UnityEngine;

//runs after other scripts (like moving platforms) so the player follows them on the same frame
//the abilities live in their own files: PlayerMovement.Dash.cs, PlayerMovement.Grapple.cs, PlayerMovement.DoubleJump.cs and PlayerMovement.Slide.cs
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
    private bool groundUnderCenter; //is there ground right under the middle of the player, or are they only standing on a ledge with the edge of their feet?
    private float groundDistance; //how far below the player's center the ground they stand on is
    private static readonly RaycastHit[] groundRayHits = new RaycastHit[8];
    //where the 8 ground rays around the player start, as (right, forward) offsets from the middle: the 4 sides and the 4 corners
    private static readonly Vector2[] groundRayOffsets = {
        new Vector2(.5f, 0f), new Vector2(-.5f, 0f), new Vector2(0f, .5f), new Vector2(0f, -.5f),
        new Vector2(.35f, .35f), new Vector2(.35f, -.35f), new Vector2(-.35f, .35f), new Vector2(-.35f, -.35f),
    };
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
    public bool IsSliding => sliding;

    /// <summary>
    /// How much shorter than standing the player is right now (0 unless sliding or crouched under something)
    /// </summary>
    public float HeightLost => crouched ? standHeight - controller.height : 0f;

    /// <summary>
    /// Called when the player lands on the ground, with how fast they were falling
    /// </summary>
    public event System.Action<float> Landed;

    /// <summary>
    /// Throw the player with a velocity (bounce pads, launchers...). Replaces their momentum and cancels dash, slide and grapple
    /// </summary>
    public void Launch(Vector3 launchVelocity)
    {
        CancelDash();
        EndSlide();
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
        CancelSlide();
        ReleaseGrapple(true);
        platform = null; //forget the old platform, or we would get dragged back to it next frame
        airDashesLeft = airDashesMax;
        airJumpsLeft = airJumpsMax;

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
        airJumpsLeft = airJumpsMax;
        SetUpSlide();
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
        if (controls.SlideTriggered()) {
            SlidePressed();
        }
        if (controls.JumpTriggered()) { //if jump button is pressed
            JumpPressed();
        }
        TryStandUp(); //after sliding under something low, stand back up as soon as there is room

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
            if (sliding) { //the slide replaces the normal input movement while it lasts
                UpdateSlide(deltaTime);
            }
            else {
                UpdateInputVelocity(deltaTime);
            }

            //use the average of the physics velocity at the start and end of the frame, so jumps are the same height at any frame rate
            velocity = velocityInput + (velocityPhysicsStart + velocityPhysics) * .5f;
        }

        MovePlayer(velocity * deltaTime); //move the player based on the combined velocity

        // --after moving--
        RememberPlatform();
        UpdateGrappleHook(deltaTime);

        //lower timers at the end of each frame
        jumpPreloadTimer -= deltaTime;
        coyoteTimer -= deltaTime;
        dashCooldownTimer -= deltaTime;
        grappleCooldownTimer -= deltaTime;
        slideCooldownTimer -= deltaTime;
        slidePreloadTimer -= deltaTime;
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
        else if (!TryAirJump()) { //double jump, or if there is none left start the jump preload timer
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
            velocityPhysics.y = GroundStickVelocity();
            StandOnLedge();
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
        float speed = crouched ? moveSpeed * crouchSpeedMultiplier : moveSpeed; //slower while crouched under something low
        Vector3 targetVelocity = (transform.right * moveInput.x + transform.forward * moveInput.y) * speed; //the velocity the input asks for

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
            velocityPhysics.y = GroundStickVelocity();
        }
    }

    /// <summary>
    /// The downward velocity that keeps the player snapped to the ground (so they follow slopes and steps down).
    /// On a ledge with nothing under the middle of the player there is no push down: it would only press the round bottom of the capsule over the edge
    /// </summary>
    float GroundStickVelocity()
    {
        return groundUnderCenter ? GroundedVelocity : 0f;
    }

    void BeginJump()
    {
        BeginJump(jumpStartingVelocity);
    }

    void BeginJump(float startingVelocity)
    {
        //if you can't jump, don't do anything
        if (!canJump) {
            return;
        }
        //jumping out of a dash or a slide keeps its momentum (dash jump, slide jump)
        if (dashing) {
            EndDash();
        }
        EndSlide();

        //if jumping, set the physics y velocity to the jump starting velocity, reset timers, and set jumping to true
        velocityPhysics.y = startingVelocity;
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
        airJumpsLeft = airJumpsMax;
        //if I just landed on the platform right after pressing the jump button, let me jump
        if (jumpPreloadTimer > 0) {
            BeginJump();
        }
        //same for the slide button
        else if (slidePreloadTimer > 0 && velocityPhysics.y <= 0) {
            TryStartSlide();
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
    /// Move the player. When falling onto a ledge with only the edge of their feet, land standing on it
    /// (the round bottom of the capsule would otherwise slip down past the edge and leave the player hanging below the floor)
    /// </summary>
    void MovePlayer(Vector3 movement)
    {
        if (isGrounded || movement.y >= 0) {
            controller.Move(movement);
            return;
        }
        //move sideways first, then look for ground under where we ended up and fall no further than that
        controller.Move(new Vector3(movement.x, 0, movement.z));
        float standingDistance = StandingDistance();
        if (FindGround(standingDistance - movement.y, out RaycastHit hit, out bool underCenter) && !underCenter && hit.distance >= standingDistance - controller.stepOffset) {
            movement.y = -Mathf.Max(hit.distance - standingDistance, 0f);
        }
        controller.Move(new Vector3(0, movement.y, 0));
    }

    /// <summary>
    /// When standing on a ledge with only the edge of the feet, stand at the height of the ledge (like stepping up onto it)
    /// </summary>
    void StandOnLedge()
    {
        float lift = StandingDistance() - groundDistance;
        if (!groundUnderCenter && Mathf.Abs(lift) > .001f) {
            controller.Move(Vector3.up * lift);
        }
    }

    /// <summary>
    /// How far below the player's center the ground is when they stand on it (the character controller rests its skin width above the ground)
    /// </summary>
    float StandingDistance()
    {
        return controller.height * .5f - controller.center.y + controller.skinWidth;
    }

    /// <summary>
    /// Cast raycasts from the middle of the player and from 8 corners to test if the player is on the ground
    /// </summary>
    bool RaycastTouchesGround() {
        float rayLength = StandingDistance() + .05f; //reach just past the bottom of the capsule

        RaycastHit hit;
        if (!FindGround(rayLength, out hit, out groundUnderCenter)) {
            return false;
        }
        //a ledge that only a corner of the player is over, and that is too high to step onto, is not ground (the player is hanging on its side)
        if (!groundUnderCenter && hit.distance < StandingDistance() - controller.stepOffset) {
            return false;
        }
        groundDistance = hit.distance;
        GroundStay(hit);
        return true;
    }

    /// <summary>
    /// Cast raycasts from the middle of the player and from 8 corners to look for ground within rayLength below the player's center
    /// </summary>
    /// <param name="closestHit">The highest ground found (the middle of the player wins a tie)</param>
    /// <param name="underCenter">Is there ground right under the middle of the player, so the round bottom of the capsule has something to rest on?</param>
    bool FindGround(float rayLength, out RaycastHit closestHit, out bool underCenter) {
        closestHit = default;
        bool found = false;

        //test the middle of the player. It reaches a step further down, to also find slopes and steps the player is standing on the edge of
        RaycastHit hit;
        underCenter = RaycastTest(transform.position, rayLength + controller.stepOffset, out hit);
        if (underCenter && hit.distance <= rayLength) {
            closestHit = hit;
            found = true;
        }

        //test the 4 sides and the 4 corners of the player
        float width = transform.localScale.x;
        foreach (Vector2 offset in groundRayOffsets) {
            Vector3 startingPoint = transform.position + (transform.right * offset.x + transform.forward * offset.y) * width;
            if (RaycastTest(startingPoint, rayLength, out hit) && (!found || hit.distance < closestHit.distance)) {
                closestHit = hit;
                found = true;
            }
        }
        return found;
    }

    /// <summary>
    /// Test a single raycast to see if it hits the ground
    /// </summary>
    /// <param name="startingPoint"> Where the ray begins (it will cast down from here)</param>
    /// <param name="rayLength">How long the ray casts</param>
    /// <param name="closestHit">The closest ground the ray hit</param>
    bool RaycastTest(Vector3 startingPoint, float rayLength, out RaycastHit closestHit) {
        closestHit = default;
        bool found = false;
        //cast a ray down from the starting point and check if it hits the ground
        int hitCount = Physics.RaycastNonAlloc(startingPoint, transform.TransformDirection(Vector3.down), groundRayHits, rayLength, jumpableMask, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < hitCount; i++) {
            RaycastHit hit = groundRayHits[i];
            if (hit.collider.transform.IsChildOf(transform)) { //the player's own colliders are never ground
                continue;
            }
            if (!found || hit.distance < closestHit.distance) {
                closestHit = hit;
                found = true;
            }
        }
        return found;
    }
}
