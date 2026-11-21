namespace FragileDiskCopy;

internal sealed class StallEscalationTracker
{
    private bool _stallRecorded;
    private int _nextReductionAt = 10;

    public bool RecordStall(int quietSeconds)
    {
        if (_stallRecorded || quietSeconds < 5)
            return false;
        _stallRecorded = true;
        return true;
    }

    public bool ShouldReduce(int quietSeconds)
    {
        if (quietSeconds < _nextReductionAt)
            return false;
        _nextReductionAt += 10;
        return true;
    }

    public void Reset()
    {
        _stallRecorded = false;
        _nextReductionAt = 10;
    }
}
