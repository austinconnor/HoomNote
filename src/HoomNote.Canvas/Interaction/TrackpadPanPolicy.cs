namespace HoomNote.Canvas.Interaction;

public static class TrackpadPanPolicy
{
    public static int WheelDeltaForPan(int wheelDelta, bool horizontal) =>
        horizontal ? -wheelDelta : wheelDelta;
}
