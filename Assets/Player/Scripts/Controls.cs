using UnityEngine;
using UnityEngine.InputSystem;

public class Controls : MonoBehaviour
{
    private PlayerInput playerInput;

    private void Awake()
    {
        playerInput = GetComponent<PlayerInput>();
    }

    public Vector2 MoveInput() => playerInput.actions["Move"].ReadValue<Vector2>(); //analog stick/WASD input for movement
    public Vector2 LookMouseInput() => playerInput.actions["LookMouse"].ReadValue<Vector2>(); //analog stick/mouse input for looking around
    public Vector2 LookStickInput() => playerInput.actions["LookStick"].ReadValue<Vector2>(); //analog stick input for looking around
    public bool JumpHeld() => playerInput.actions["Jump"].ReadValue<float>() > 0.9f; //is the jump button being held down this frame?
    public bool JumpTriggered() => playerInput.actions["Jump"].triggered; //was the jump button just pressed this frame?
    public bool InteractHeld() => playerInput.actions["Interact"].ReadValue<float>() > 0.9f; //is the interact button being held down this frame?
    public bool InteractTriggered() => playerInput.actions["Interact"].triggered; //was the interact button just pressed this frame?
    public bool Interact2Held() => playerInput.actions["Interact2"].ReadValue<float>() > 0.9f; //is the interact2 button being held down this frame?
    public bool Interact2Triggered() => playerInput.actions["Interact2"].triggered; //was the interact2 button just pressed this frame?
    public bool DashTriggered() => playerInput.actions["Dash"].triggered; //was the dash button (Shift) just pressed this frame?
    public bool GrappleTriggered() => playerInput.actions["Grapple"].triggered; //was the grapple button (Q) just pressed this frame?
    public bool SlideTriggered() => playerInput.actions["Crouch"].triggered; //was the slide button (C) just pressed this frame?
}
