namespace AkuWM.Core;

/// <summary>
/// Short, stable, random ids for the things the configuration lists.
/// </summary>
/// <remarks>
/// Deliberately NOT derived from the content: the Python prototype hashed the
/// criteria and the actions, so editing a rule changed its id and broke the
/// link between the common layer and the machine layer that overrides it.
/// An id is minted once, when the item is created, and never changes again.
/// </remarks>
public static class Ids
{
    private const string Alphabet = "0123456789abcdefghijklmnopqrstuvwxyz";

    /// <summary>The generator, swapped for a deterministic one in tests.</summary>
    public static Func<string, string> Generator { get; set; } = prefix => Mint(prefix, Random.Shared);

    public static string New(string prefix) => Generator(prefix);

    public static string Mint(string prefix, Random random)
    {
        Span<char> chars = stackalloc char[6];
        for (int i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[random.Next(Alphabet.Length)];
        }

        return $"{prefix}-{new string(chars)}";
    }

    /// <summary>
    /// A generator that walks a fixed sequence, so a test can assert on whole
    /// files. Not used outside tests.
    /// </summary>
    public static Func<string, string> Sequential()
    {
        var counters = new Dictionary<string, int>(StringComparer.Ordinal);
        return prefix =>
        {
            counters.TryGetValue(prefix, out int n);
            counters[prefix] = ++n;
            return $"{prefix}-{n:0000}";
        };
    }
}
