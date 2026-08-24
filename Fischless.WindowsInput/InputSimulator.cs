namespace Fischless.WindowsInput;

public class InputSimulator : IInputSimulator
{
    public InputSimulator(IKeyboardSimulator keyboardSimulator, IMouseSimulator mouseSimulator, IInputDeviceStateAdaptor inputDeviceStateAdaptor)
    {
        _keyboardSimulator = keyboardSimulator;
        _mouseSimulator = mouseSimulator;
        _inputDeviceState = inputDeviceStateAdaptor;
    }

    public InputSimulator()
    {
        _keyboardSimulator = new KeyboardSimulator(this);
        _mouseSimulator = new MouseSimulator(this);
        _inputDeviceState = new WindowsInputDeviceStateAdaptor();
    }

    public InputSimulator(Action beforeInputDispatch)
    {
        if (beforeInputDispatch == null)
        {
            throw new ArgumentNullException(nameof(beforeInputDispatch));
        }

        var messageDispatcher = new WindowsInputMessageDispatcher(beforeInputDispatch);
        _keyboardSimulator = new KeyboardSimulator(this, messageDispatcher);
        _mouseSimulator = new MouseSimulator(this, messageDispatcher);
        _inputDeviceState = new WindowsInputDeviceStateAdaptor();
    }

    public IKeyboardSimulator Keyboard => _keyboardSimulator;

    public IMouseSimulator Mouse => _mouseSimulator;

    public IInputDeviceStateAdaptor InputDeviceState => _inputDeviceState;

    private readonly IKeyboardSimulator _keyboardSimulator;

    private readonly IMouseSimulator _mouseSimulator;

    private readonly IInputDeviceStateAdaptor _inputDeviceState;
}
