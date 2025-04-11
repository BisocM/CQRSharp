namespace CQRSharp.Sample.Management.Menu;

public class MenuStateMachine(MenuState initialState)
{
    public MenuState CurrentState { get; private set; } = initialState;

    public MenuState PreviousState { get; private set; } = initialState;

    public void TransitionTo(MenuState newState)
    {
        PreviousState = CurrentState;
        CurrentState = newState;
    }

    public void TransitionToPrevious()
    {
        CurrentState = PreviousState;
    }
}