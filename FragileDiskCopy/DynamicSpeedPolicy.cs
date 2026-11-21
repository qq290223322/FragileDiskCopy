namespace FragileDiskCopy;

internal sealed class DynamicSpeedPolicy
{
    public const long StartingBytesPerSecond = 512 * 1024;

    private readonly long _baseBytesPerSecond;
    private readonly long _maximumBytesPerSecond;
    private readonly long _step;
    private readonly int _growthIntervalSeconds;
    private int _stableSeconds;

    public DynamicSpeedPolicy(
        long baseBytesPerSecond,
        long maximumBytesPerSecond,
        int growthIntervalSeconds)
    {
        _baseBytesPerSecond = baseBytesPerSecond;
        _maximumBytesPerSecond = maximumBytesPerSecond;
        _growthIntervalSeconds = Math.Max(10, growthIntervalSeconds);
        _step = Math.Max(64 * 1024L, baseBytesPerSecond / 10);
        CurrentLimit = Math.Min(StartingBytesPerSecond, maximumBytesPerSecond);
    }

    public long CurrentLimit { get; private set; }

    public bool RecordStableSecond(out long newLimit)
    {
        newLimit = CurrentLimit;
        if (CurrentLimit >= _maximumBytesPerSecond)
            return false;

        _stableSeconds++;
        if (_stableSeconds < _growthIntervalSeconds)
            return false;

        _stableSeconds = 0;
        CurrentLimit = Math.Min(_maximumBytesPerSecond, CurrentLimit + _step);
        newLimit = CurrentLimit;
        return true;
    }

    public (long Previous, long Next) ReduceAfterStall()
    {
        ResetStability();
        var previous = CurrentLimit;
        CurrentLimit = Math.Max(StartingBytesPerSecond, CurrentLimit * 9 / 10);
        return (previous, CurrentLimit);
    }

    public void ResetStability()
    {
        _stableSeconds = 0;
    }
}
