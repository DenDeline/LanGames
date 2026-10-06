namespace LanPong;

// PongPeer owns synchronization. A passive tab's zero input must not hide an active tab.
internal sealed class LocalControllerInputs
{
    private readonly Dictionary<Guid, (int Axis, DateTime Updated)> _controllers = [];

    public void Set(Guid controllerId, int axis, DateTime now) =>
        _controllers[controllerId] = (Math.Clamp(axis, -1, 1), now);

    public void Remove(Guid controllerId) => _controllers.Remove(controllerId);

    public void Clear() => _controllers.Clear();

    public int GetAxis(DateTime now)
    {
        var newest = DateTime.MinValue;
        var axis = 0;
        foreach (var control in _controllers.Values)
        {
            if (control.Axis == 0 || now - control.Updated > NetworkConstants.InputStaleAfter ||
                control.Updated <= newest)
                continue;

            newest = control.Updated;
            axis = control.Axis;
        }

        return axis;
    }
}
