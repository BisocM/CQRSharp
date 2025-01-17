namespace CQRSharp.Sample.Management.Menu;

public class MenuStateMachine(MenuState initialState)
{
    private MenuState _currentState = initialState;
    private MenuState _previousState = initialState;

    public void TransitionTo(MenuState newState)
    {
        _previousState = _currentState;
        _currentState = newState;
    }
    
    public void TransitionToPrevious() => _currentState = _previousState;
    
    public MenuState CurrentState => _currentState;
    public MenuState PreviousState => _previousState;
}