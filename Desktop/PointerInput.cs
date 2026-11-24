namespace CSharpMrp.Desktop;

internal enum PointerPhase
{
    Down,
    Move,
    Up,
    Lost
}

// Windows implicitly captures touch and pen contacts to the control that receives pointer down.
internal sealed class PointerInput : NativeWindow
{
    private readonly Control target;
    private readonly Action<PointerPhase, int, Point> dispatch;

    public PointerInput(Control target, Action<PointerPhase, int, Point> dispatch)
    {
        this.target = target;
        this.dispatch = dispatch;
        target.HandleCreated += (_, _) => AssignHandle(target.Handle);
        target.HandleDestroyed += (_, _) => ReleaseHandle();
        if (target.IsHandleCreated) AssignHandle(target.Handle);
    }

    protected override void WndProc(ref Message message)
    {
        const int pointerUpdate = 0x0245, pointerDown = 0x0246, pointerUp = 0x0247;
        const int pointerCaptureChanged = 0x024C;
        int id = (int)message.WParam & 0xffff;
        switch (message.Msg)
        {
            case pointerDown:
            case pointerUpdate:
            case pointerUp:
                int packedPoint = unchecked((int)message.LParam);
                var position = target.PointToClient(new Point((short)packedPoint, (short)(packedPoint >> 16)));
                var phase = message.Msg == pointerDown ? PointerPhase.Down :
                    message.Msg == pointerUp ? PointerPhase.Up : PointerPhase.Move;
                dispatch(phase, id, position);
                break;
            case pointerCaptureChanged:
                dispatch(PointerPhase.Lost, id, default);
                break;
            default:
                base.WndProc(ref message);
                return;
        }
        message.Result = 0;
    }
}
