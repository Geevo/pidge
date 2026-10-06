namespace Pidge.Core;

public static class Ids
{
    /// <summary>Generates an identifier for a request, tab, or key/value row.</summary>
    public static string NewId() => Guid.NewGuid().ToString();
}
