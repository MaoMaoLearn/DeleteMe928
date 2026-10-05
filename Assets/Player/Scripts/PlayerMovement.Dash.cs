using UnityEngine;

//dash ability: press the dash button (Shift) to burst in the direction you are moving (or forward if standing still)
//the dash ignores gravity while it lasts, you get one dash in the air until you land or grapple, and jumping out of a dash keeps its speed
public partial class PlayerMovement
{
    [Header("Dash")]
    [SerializeField] private bool canDash = true;
    [SerializeField] private float dashSpeed = 22f;
    [SerializeField] private float dashDuration = .16f;
    [SerializeField] private float dashCooldown = .5f;
    [SerializeField] private float dashExitSpeed = 4f; //extra speed you keep after the dash ends, wears off like other momentum
    [SerializeField] private int airDashesMax = 1; //how many dashes you get in the air before landing again

    private bool dashing;
    private float dashTimer;
    private float dashCooldownTimer;
    private int airDashesLeft;
    private Vector3 dashDirection;

    void TryStartDash()
    {
        if (!canDash || dashing || dashCooldownTimer > 0) {
            return;
        }
        if (!isGrounded && airDashesLeft <= 0) {
            return;
        }
        //dashing lets go of the rope (a hook that is still flying keeps going, so you can dash and then get pulled)
        if (grappleState == GrappleState.Attached) {
            ReleaseGrapple();
        }
        EndSlide(); //dashing out of a slide ends the slide

        //dash the way you are holding, or forward if you aren't holding a direction
        Vector2 moveInput = controls.MoveInput();
        dashDirection = transform.right * moveInput.x + transform.forward * moveInput.y;
        dashDirection.y = 0;
        if (dashDirection.sqrMagnitude < .01f) {
            dashDirection = transform.forward;
        }
        dashDirection.Normalize();

        if (!isGrounded) {
            airDashesLeft--;
        }
        dashing = true;
        dashTimer = dashDuration;
        dashCooldownTimer = dashCooldown;
        jumping = false; //stop the floaty jump hold
        velocityPhysics = Vector3.zero; //the dash ignores gravity and old momentum while it lasts
    }

    void UpdateDash(float deltaTime)
    {
        dashTimer -= deltaTime;
        if (dashTimer <= 0) {
            EndDash();
        }
    }

    /// <summary>
    /// Finish the dash, coming out of it at run speed plus a little extra momentum
    /// </summary>
    void EndDash()
    {
        if (!dashing) {
            return;
        }
        dashing = false;
        velocityInput = dashDirection * moveSpeed;
        velocityPhysics = dashDirection * dashExitSpeed;
    }

    /// <summary>
    /// Stop the dash without keeping any of its speed (teleports, launches)
    /// </summary>
    void CancelDash()
    {
        dashing = false;
        dashTimer = 0;
    }
}
