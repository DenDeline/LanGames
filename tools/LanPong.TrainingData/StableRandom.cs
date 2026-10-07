namespace LanPong.TrainingData;

/// <summary>Explicit SplitMix64 stream so match seeds do not depend on System.Random revisions.</summary>
internal struct StableRandom(ulong seed)
{
    private ulong _state = seed;

    public ulong NextUInt64()
    {
        unchecked
        {
            var value = _state += 0x9e3779b97f4a7c15UL;
            value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL;
            value = (value ^ (value >> 27)) * 0x94d049bb133111ebUL;
            return value ^ (value >> 31);
        }
    }

    public double NextUnit() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    public static ulong DeriveSeed(ulong masterSeed, int splitId, int matchIndex)
    {
        if (splitId < 0 || matchIndex < 0) throw new ArgumentOutOfRangeException();
        unchecked
        {
            var stream = new StableRandom(masterSeed ^
                ((ulong)(splitId + 1) * 0xd1b54a32d192ed03UL) ^
                ((ulong)(matchIndex + 1) * 0xabc98388fb8fac03UL));
            return stream.NextUInt64();
        }
    }
}
