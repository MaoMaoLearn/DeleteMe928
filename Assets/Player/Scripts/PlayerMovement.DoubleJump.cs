using UnityEngine;

//double jump: press jump again in the air for a second jump
//you get it back when you land or when the grapple hook grabs something
public partial class PlayerMovement
{
    [Header("Double Jump")]
    [SerializeField] private int airJumpsMax = 1; //how many extra jumps you get in the air before landing again (1 is a double jump, 0 turns it off)
    [SerializeField] private float airJumpStartingVelocity = 6f;

    private int airJumpsLeft;

    /// <summary>
    /// Jump in the air if there is an air jump left. Returns false if there wasn't
    /// </summary>
    bool TryAirJump()
    {
        if (!canJump || airJumpsLeft <= 0) {
            return false;
        }
        airJumpsLeft--;
        BeginJump(airJumpStartingVelocity); //replaces the falling speed, so it works the same however fast you were falling
        return true;
    }
}
