using UnityEngine;

//slide: press the slide button (C) on the ground to drop low and slide the way you are moving (or forward if standing still), faster than running
//the slide slows down by itself and you stand back up when it runs out. Jumping or sliding off a ledge keeps the slide's speed
//the player is shorter while sliding, so you can slide under low things, and you stay crouched under them until there is room to stand
public partial class PlayerMovement
{
    [Header("Slide")]
    [SerializeField] private bool canSlide = true;
    [SerializeField] private float slideSpeed = 11f; //speed at the start of the slide
    [SerializeField] private float slideSpeedMax = 16f; //the most speed you can carry into a slide (from a dash, grapple or launch)
    [SerializeField] private float slideFriction = 9f; //how fast the slide slows down
    [SerializeField] private float slideBrakeMultiplier = 3f; //holding back slows the slide this much faster
    [SerializeField] private float slideEndSpeed = 4f; //the slide ends when it gets this slow
    [SerializeField] private float slideSteering = 60f; //how many degrees per second you can turn the slide
    [SerializeField] private float slideCooldown = .3f;
    [SerializeField] private float slideHeight = 1f; //how tall the player is while sliding
    [SerializeField] private float slidePreloadTimerMax = .15f; //pressing slide this long before landing slides as you land
    [SerializeField] private float crouchSpeedMultiplier = .5f; //walking speed while stuck crouching under something low

    private bool sliding;
    private bool crouched; //the player is short right now (sliding, or no room to stand back up yet)
    private float slideCooldownTimer;
    private float slidePreloadTimer;
    private float standHeight;
    private Vector3 standCenter;
    private static readonly Collider[] headroomHits = new Collider[8];

    void SetUpSlide()
    {
        standHeight = controller.height;
        standCenter = controller.center;
    }

    void SlidePressed()
    {
        if (!TryStartSlide()) {
            slidePreloadTimer = slidePreloadTimerMax; //remember the press so landing right after it still slides
        }
    }

    bool TryStartSlide()
    {
        if (!canSlide || sliding || slideCooldownTimer > 0) {
            return false;
        }
        if (!isGrounded || grappleState == GrappleState.Attached) {
            return false;
        }
        //sliding out of a dash keeps (some of) the dash's speed
        Vector3 carriedVelocity = dashing ? dashDirection * dashSpeed : velocityInput + velocityPhysics;
        carriedVelocity.y = 0;
        CancelDash();

        //slide the way you are holding, or the way you are already moving, or forward if you are standing still
        Vector2 moveInput = controls.MoveInput();
        Vector3 slideDirection = transform.right * moveInput.x + transform.forward * moveInput.y;
        slideDirection.y = 0;
        if (slideDirection.sqrMagnitude < .01f) {
            slideDirection = carriedVelocity;
        }
        if (slideDirection.sqrMagnitude < .01f) {
            slideDirection = transform.forward;
        }
        slideDirection.Normalize();

        sliding = true;
        slidePreloadTimer = 0;
        SetCrouched(true);
        //the slide takes over all sideways movement while it lasts (velocityInput is the slide's velocity until it ends)
        velocityInput = slideDirection * Mathf.Clamp(carriedVelocity.magnitude, slideSpeed, Mathf.Max(slideSpeed, slideSpeedMax));
        velocityPhysics.x = 0;
        velocityPhysics.z = 0;
        return true;
    }

    /// <summary>
    /// Called every frame while sliding instead of the normal input movement: slow down, steer a little, and end when it runs out
    /// </summary>
    void UpdateSlide(float deltaTime)
    {
        //slid off a ledge: the slide is over but its speed carries through the air
        if (!isGrounded) {
            EndSlide();
            return;
        }

        float speed = velocityInput.magnitude;
        Vector3 slideDirection = speed > .01f ? velocityInput / speed : transform.forward;
        float friction = slideFriction;

        Vector2 moveInput = Vector2.ClampMagnitude(controls.MoveInput(), 1f);
        Vector3 inputDirection = transform.right * moveInput.x + transform.forward * moveInput.y;
        if (inputDirection.sqrMagnitude > .01f) {
            if (Vector3.Dot(inputDirection, slideDirection) < -.5f) { //holding back brakes
                friction *= slideBrakeMultiplier;
            }
            else { //holding any other way steers the slide a little
                slideDirection = Vector3.RotateTowards(slideDirection, inputDirection.normalized, slideSteering * Mathf.Deg2Rad * deltaTime, 0f);
            }
        }

        speed -= friction * deltaTime;
        velocityInput = slideDirection * Mathf.Max(speed, 0f);
        if (speed <= slideEndSpeed) {
            EndSlide();
        }
    }

    /// <summary>
    /// Finish the slide. Any speed above running speed becomes momentum, so jumping out of a slide or sliding off a ledge carries it
    /// </summary>
    void EndSlide()
    {
        if (!sliding) {
            return;
        }
        sliding = false;
        slideCooldownTimer = slideCooldown;

        float speed = velocityInput.magnitude;
        if (speed > moveSpeed) {
            Vector3 slideDirection = velocityInput / speed;
            velocityInput = slideDirection * moveSpeed;
            velocityPhysics += slideDirection * (speed - moveSpeed);
        }
        TryStandUp();
    }

    /// <summary>
    /// Stop sliding and stand up right away without keeping any of the slide's speed (teleports)
    /// </summary>
    void CancelSlide()
    {
        sliding = false;
        slidePreloadTimer = 0;
        SetCrouched(false);
    }

    /// <summary>
    /// Stand back up if there is room above the player's head (otherwise stay crouched and try again next frame)
    /// </summary>
    void TryStandUp()
    {
        if (crouched && !sliding && HasRoomToStand()) {
            SetCrouched(false);
        }
    }

    /// <summary>
    /// Make the character controller short or full height, keeping the feet where they are
    /// </summary>
    void SetCrouched(bool crouch)
    {
        crouched = crouch;
        float height = crouch ? Mathf.Clamp(slideHeight, controller.radius * 2f, standHeight) : standHeight;
        controller.height = height;
        controller.center = standCenter + Vector3.down * ((standHeight - height) * .5f);
    }

    bool HasRoomToStand()
    {
        //check the space the top of the standing capsule would take up (a little thinner, so walls we are touching don't count)
        float radius = controller.radius * .9f;
        Vector3 feet = transform.position + standCenter + Vector3.down * (standHeight * .5f);
        Vector3 crouchedTop = feet + Vector3.up * (controller.height - radius);
        Vector3 standingTop = feet + Vector3.up * (standHeight - radius);
        int hitCount = Physics.OverlapCapsuleNonAlloc(crouchedTop, standingTop, radius, headroomHits, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < hitCount; i++) {
            if (!headroomHits[i].transform.IsChildOf(transform)) { //our own colliders are not in the way
                return false;
            }
        }
        return true;
    }
}
