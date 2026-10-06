namespace Pidge.Core.Idna;

internal enum IdnaStatus
{
    Valid = 0,
    Ignored = 1,
    Mapped = 2,
    Disallowed = 3,
}

/// <summary>The bidi classes the bidi rule tells apart. Anything else is <see cref="Other"/>.</summary>
internal enum BidiClass
{
    L,
    R,
    AL,
    AN,
    EN,
    ES,
    CS,
    ET,
    ON,
    BN,
    NSM,
    Other,
}

internal enum JoiningType
{
    NonJoining,
    JoinCausing,
    Dual,
    Left,
    Right,
    Transparent,
}

/// <summary>
/// Lookups over the generated tables in <c>IdnaTables.g.cs</c>, which hold one
/// entry per run of code points that share a value.
/// </summary>
internal static partial class IdnaTables
{
    /// <summary>The UTS 46 status of <paramref name="codePoint"/>, and its replacement when mapped.</summary>
    public static IdnaStatus Status(int codePoint, out ReadOnlySpan<char> replacement)
    {
        var value = MappingValues[RunOf(MappingStarts, codePoint)];
        var status = (IdnaStatus)(value & 3);
        replacement = status == IdnaStatus.Mapped
            ? MappingText.AsSpan(value >> 8, (value >> 2) & 63)
            : default;
        return status;
    }

    public static BidiClass BidiClassOf(int codePoint) => (BidiClass)(PropertiesOf(codePoint) & 0xF);

    public static JoiningType JoiningTypeOf(int codePoint) => (JoiningType)((PropertiesOf(codePoint) >> 4) & 7);

    /// <summary>General category Mn, Mc or Me.</summary>
    public static bool IsMark(int codePoint) => (PropertiesOf(codePoint) & 0x80) != 0;

    /// <summary>Canonical combining class 9.</summary>
    public static bool IsVirama(int codePoint) => (PropertiesOf(codePoint) & 0x100) != 0;

    private static int PropertiesOf(int codePoint) => PropertyValues[RunOf(PropertyStarts, codePoint)];

    private static int RunOf(ReadOnlySpan<int> starts, int codePoint)
    {
        var index = starts.BinarySearch(codePoint);
        return index >= 0 ? index : ~index - 1;
    }
}
