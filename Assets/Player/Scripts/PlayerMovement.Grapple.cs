using UnityEngine;

//grappling hook: press the grapple button (Q) to fire a hook at what you are looking at, and get pulled toward it
//you let go automatically when you arrive (with a small hop onto the ledge), or early by pressing grapple again or jumping (you keep your speed)
public partial class PlayerMovement
{
    private enum GrappleState { Idle, Firing, Attached, Retracting }

    [Header("Grapple")]
    [SerializeField] private bool canGrapple = true;
    [SerializeField] private LayerMask grappleableMask = ~0; //what the hook can stick to
    [SerializeField] private float grappleRange = 35f;
    [SerializeField] private float grappleAimAssist = .4f; //forgiving aim: near misses within this radius still hit
    [SerializeField] private float grappleHookSpeed = 120f; //how fast the hook flies out
    [SerializeField] private float grapplePullAcceleration = 50f;
    [SerializeField] private float grappleMaxSpeed = 20f;
    [SerializeField] private float grappleGravityMultiplier = .3f; //how much gravity still pulls you down while on the rope
    [SerializeField] private float grappleTargetHeight = 1.2f; //pull toward a point this far above the hook, so you end up on top of ledges
    [SerializeField] private float grappleArriveDistance = 1.5f; //let go automatically this close to the hook
    [SerializeField] private float grappleArriveSpeed = 4f; //the most speed you keep when you arrive, so you don't overshoot small platforms
    [SerializeField] private float grappleArriveHop = 3f; //small upward pop when you arrive, to get over the ledge
    [SerializeField] private float grappleMaxTime = 3f; //let go after pulling this long no matter what
    [SerializeField] private float grappleCooldown = .25f;
    [SerializeField] private Material ropeMaterial; //optional, a plain unlit material is used if empty
    [SerializeField] private Color ropeColor = new Color(.85f, .85f, .75f);
    [SerializeField] private float ropeWidth = .04f;
    [SerializeField] private bool showCrosshair = true; //dot in the middle of the screen that lights up when the hook can reach

    private const float GrappleStuckTime = .3f; //let go if we stop getting closer for this long (stuck behind a wall)
    private const float GrappleMinApproachSpeed = 1f;
    private const float GrappleSidewaysDamping = 4f; //bleeds off sideways speed so you head for the hook instead of circling it
    private GrappleState grappleState = GrappleState.Idle;
    private bool grappleHasTarget; //false when the hook was fired at nothing
    private Collider grappleCollider; //what the hook is stuck in (it can be a moving platform)
    private Vector3 grappleLocalPoint; //where the hook is stuck, in the collider's local space
    private float grappleTargetOffset; //how far above the hook we pull toward
    private Vector3 grappleMissPoint; //where a hook that hit nothing flies to
    private Vector3 hookPosition; //where the end of the rope is right now
    private float grappleTimer;
    private float grappleStuckTimer;
    private float grappleLastDistance;
    private float grappleCooldownTimer;
    private bool grappleAimValid; //is there something to grapple under the crosshair right now?
    private Transform cameraTransform;
    private LineRenderer rope;

    void SetUpGrapple()
    {
        //aim from the player's camera
        Camera playerCamera = GetComponentInChildren<Camera>();
        cameraTransform = playerCamera != null ? playerCamera.transform : transform;

        //make the rope
        GameObject ropeObject = new GameObject("GrappleRope");
        ropeObject.transform.SetParent(transform, false);
        rope = ropeObject.AddComponent<LineRenderer>();
        if (ropeMaterial != null) {
            rope.material = ropeMaterial;
        }
        else {
            Shader ropeShader = Shader.Find("Sprites/Default"); //simple unlit shader that uses the rope color, always included in builds
            if (ropeShader != null) {
                rope.material = new Material(ropeShader);
            }
        }
        rope.startColor = ropeColor;
        rope.endColor = ropeColor;
        rope.widthMultiplier = ropeWidth;
        rope.positionCount = 2;
        rope.useWorldSpace = true;
        rope.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        rope.receiveShadows = false;
        rope.enabled = false;
    }

    void GrapplePressed()
    {
        //pressing again while the hook is out lets go
        if (grappleState == GrappleState.Firing || grappleState == GrappleState.Attached) {
            ReleaseGrapple();
            return;
        }
        if (!canGrapple || grappleCooldownTimer > 0) {
            return;
        }

        //fire at what's under the crosshair, or out to max range if there's nothing there
        RaycastHit hit;
        grappleHasTarget = FindGrapplePoint(out hit);
        if (grappleHasTarget) {
            grappleCollider = hit.collider;
            grappleLocalPoint = hit.collider.transform.InverseTransformPoint(hit.point);
            grappleTargetOffset = hit.normal.y < -.5f ? 0 : grappleTargetHeight; //no point aiming above a ceiling
        }
        else {
            grappleMissPoint = cameraTransform.position + cameraTransform.forward * grappleRange;
        }
        hookPosition = RopeStart();
        grappleState = GrappleState.Firing;
        grappleCooldownTimer = grappleCooldown;
        rope.enabled = true;
    }

    /// <summary>
    /// Move the end of the rope: fly out, stay stuck to the (maybe moving) target, or fly back
    /// </summary>
    void UpdateGrappleHook(float deltaTime)
    {
        switch (grappleState) {
            case GrappleState.Firing:
                if (grappleHasTarget && !GrappleTargetStillThere()) {
                    ReleaseGrapple();
                    break;
                }
                Vector3 hookGoal = grappleHasTarget ? GrapplePoint() : grappleMissPoint;
                hookPosition = Vector3.MoveTowards(hookPosition, hookGoal, grappleHookSpeed * deltaTime);
                if (hookPosition == hookGoal) {
                    if (grappleHasTarget) {
                        AttachGrapple();
                    }
                    else {
                        grappleState = GrappleState.Retracting;
                    }
                }
                break;

            case GrappleState.Attached:
                if (!GrappleTargetStillThere()) { //the platform was destroyed or switched off
                    ReleaseGrapple();
                    break;
                }
                hookPosition = GrapplePoint();
                break;

            case GrappleState.Retracting:
                Vector3 ropeStart = RopeStart();
                hookPosition = Vector3.MoveTowards(hookPosition, ropeStart, grappleHookSpeed * 1.5f * deltaTime);
                if (hookPosition == ropeStart) {
                    grappleState = GrappleState.Idle;
                    rope.enabled = false;
                }
                break;
        }

        //light up the crosshair when there's something in reach
        RaycastHit hit;
        grappleAimValid = canGrapple && showCrosshair && grappleState != GrappleState.Attached && FindGrapplePoint(out hit);
    }

    void AttachGrapple()
    {
        grappleState = GrappleState.Attached;
        grappleTimer = 0;
        grappleStuckTimer = 0;
        grappleLastDistance = Vector3.Distance(transform.position, GrapplePullTarget());

        if (dashing) { //keep the dash's momentum and let the rope take over
            EndDash();
        }
        EndSlide(); //same for a slide
        jumping = false; //no floaty jump hold while on the rope
        airDashesLeft = airDashesMax; //grappling gives your air dash and double jump back
        airJumpsLeft = airJumpsMax;
        if (velocityPhysics.y < 0) { //stop falling so the pull kicks in right away
            velocityPhysics.y = 0;
        }
    }

    /// <summary>
    /// Called every frame while the hook is stuck in something: pull the player toward it
    /// </summary>
    void UpdateGrapplePull(float deltaTime)
    {
        Vector3 toTarget = GrapplePullTarget() - transform.position;
        float distance = toTarget.magnitude;

        //made it: let go with a small hop so you land on top instead of flying past
        if (distance < grappleArriveDistance || Vector3.Distance(transform.position, GrapplePoint()) < grappleArriveDistance) {
            velocityPhysics = Vector3.ClampMagnitude(velocityPhysics, grappleArriveSpeed);
            velocityPhysics.y = Mathf.Max(velocityPhysics.y, grappleArriveHop);
            ReleaseGrapple();
            return;
        }

        //let go if we stopped getting closer (stuck behind something) or have been pulling too long
        bool gettingCloser = grappleLastDistance - distance > GrappleMinApproachSpeed * deltaTime;
        grappleStuckTimer = gettingCloser ? 0 : grappleStuckTimer + deltaTime;
        grappleLastDistance = distance;
        grappleTimer += deltaTime;
        if (grappleStuckTimer > GrappleStuckTime || grappleTimer > grappleMaxTime) {
            ReleaseGrapple();
            return;
        }

        //pull toward the hook, with a little gravity so it still feels heavy
        Vector3 pullDirection = toTarget / distance;
        velocityPhysics += pullDirection * grapplePullAcceleration * deltaTime;
        velocityPhysics.y -= gravity * grappleGravityMultiplier * deltaTime;

        //bleed off sideways speed so you head for the hook instead of circling around it
        Vector3 sidewaysVelocity = Vector3.ProjectOnPlane(velocityPhysics, pullDirection);
        velocityPhysics -= sidewaysVelocity * Mathf.Min(GrappleSidewaysDamping * deltaTime, 1f);

        velocityPhysics = Vector3.ClampMagnitude(velocityPhysics, grappleMaxSpeed);
    }

    /// <summary>
    /// Jumping while on the rope lets go and adds a hop on top of your swing speed
    /// </summary>
    void GrappleJump()
    {
        ReleaseGrapple();
        if (canJump) {
            velocityPhysics.y = Mathf.Max(velocityPhysics.y, jumpStartingVelocity);
        }
    }

    /// <summary>
    /// Let go of the rope (you keep your speed). The hook flies back, or vanishes right away if instantly is true (teleports)
    /// </summary>
    void ReleaseGrapple(bool instantly = false)
    {
        if (grappleState == GrappleState.Firing || grappleState == GrappleState.Attached) {
            grappleState = GrappleState.Retracting;
        }
        grappleHasTarget = false;
        grappleCollider = null;
        if (instantly) {
            grappleState = GrappleState.Idle;
            if (rope != null) {
                rope.enabled = false;
            }
        }
    }

    /// <summary>
    /// Look for something to grapple under the crosshair
    /// </summary>
    bool FindGrapplePoint(out RaycastHit hit)
    {
        Ray aim = new Ray(cameraTransform.position, cameraTransform.forward);
        //try an exact ray first, then a thicker one so grabbing the edge of a platform doesn't need to be pixel perfect
        if (Physics.Raycast(aim, out hit, grappleRange, grappleableMask, QueryTriggerInteraction.Ignore) ||
            Physics.SphereCast(aim, grappleAimAssist, out hit, grappleRange, grappleableMask, QueryTriggerInteraction.Ignore)) {
            return hit.distance > grappleArriveDistance; //too close to be worth pulling toward
        }
        return false;
    }

    bool GrappleTargetStillThere()
    {
        return grappleCollider != null && grappleCollider.enabled && grappleCollider.gameObject.activeInHierarchy;
    }

    Vector3 GrapplePoint()
    {
        return grappleCollider.transform.TransformPoint(grappleLocalPoint);
    }

    Vector3 GrapplePullTarget()
    {
        return GrapplePoint() + Vector3.up * grappleTargetOffset;
    }

    /// <summary>
    /// Where the rope comes out of, a bit to the right and below the camera like it's held in your hand
    /// </summary>
    Vector3 RopeStart()
    {
        return cameraTransform.position + cameraTransform.right * .3f - cameraTransform.up * .25f + cameraTransform.forward * .3f;
    }

    //draw the rope after everything (including the camera) has moved, so it doesn't lag a frame behind
    void LateUpdate()
    {
        if (grappleState == GrappleState.Idle) {
            return;
        }
        rope.SetPosition(0, RopeStart());
        rope.SetPosition(1, hookPosition);
    }

    void OnGUI()
    {
        if (!showCrosshair || !canGrapple) {
            return;
        }
        //small dot in the middle of the screen, colored when the hook can reach what you are aiming at
        const float size = 6f;
        Color oldColor = GUI.color;
        GUI.color = grappleAimValid ? new Color(.3f, 1f, .9f) : new Color(1f, 1f, 1f, .4f);
        GUI.DrawTexture(new Rect((Screen.width - size) * .5f, (Screen.height - size) * .5f, size, size), Texture2D.whiteTexture);
        GUI.color = oldColor;
    }
}
