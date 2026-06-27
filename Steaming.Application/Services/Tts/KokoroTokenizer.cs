namespace Steaming.Application.Services.Tts;

// Maps IPA phonemes to the exact token ids from the official
// onnx-community/Kokoro-82M-v1.0-ONNX tokenizer.json.
// The ids are sparse and must not be regenerated from string position.
public static class KokoroTokenizer
{
    // Kokoro's hard context limit (excluding the two surrounding "$" pad tokens).
    public const int MaxTokens = 510;
    public const int MaxTokenId = 177;

    private static readonly Dictionary<char, long> Vocab = new()
    {
        ['$'] = 0,
        [';'] = 1,
        [':'] = 2,
        [','] = 3,
        ['.'] = 4,
        ['!'] = 5,
        ['?'] = 6,
        ['—'] = 9,
        ['…'] = 10,
        ['"'] = 11,
        ['('] = 12,
        [')'] = 13,
        ['“'] = 14,
        ['”'] = 15,
        [' '] = 16,
        ['̃'] = 17,
        ['ʣ'] = 18,
        ['ʥ'] = 19,
        ['ʦ'] = 20,
        ['ʨ'] = 21,
        ['ᵝ'] = 22,
        ['ꭧ'] = 23,
        ['A'] = 24,
        ['I'] = 25,
        ['O'] = 31,
        ['Q'] = 33,
        ['S'] = 35,
        ['T'] = 36,
        ['W'] = 39,
        ['Y'] = 41,
        ['ᵊ'] = 42,
        ['a'] = 43,
        ['b'] = 44,
        ['c'] = 45,
        ['d'] = 46,
        ['e'] = 47,
        ['f'] = 48,
        ['h'] = 50,
        ['i'] = 51,
        ['j'] = 52,
        ['k'] = 53,
        ['l'] = 54,
        ['m'] = 55,
        ['n'] = 56,
        ['o'] = 57,
        ['p'] = 58,
        ['q'] = 59,
        ['r'] = 60,
        ['s'] = 61,
        ['t'] = 62,
        ['u'] = 63,
        ['v'] = 64,
        ['w'] = 65,
        ['x'] = 66,
        ['y'] = 67,
        ['z'] = 68,
        ['ɑ'] = 69,
        ['ɐ'] = 70,
        ['ɒ'] = 71,
        ['æ'] = 72,
        ['β'] = 75,
        ['ɔ'] = 76,
        ['ɕ'] = 77,
        ['ç'] = 78,
        ['ɖ'] = 80,
        ['ð'] = 81,
        ['ʤ'] = 82,
        ['ə'] = 83,
        ['ɚ'] = 85,
        ['ɛ'] = 86,
        ['ɜ'] = 87,
        ['ɟ'] = 90,
        ['ɡ'] = 92,
        ['ɥ'] = 99,
        ['ɨ'] = 101,
        ['ɪ'] = 102,
        ['ʝ'] = 103,
        ['ɯ'] = 110,
        ['ɰ'] = 111,
        ['ŋ'] = 112,
        ['ɳ'] = 113,
        ['ɲ'] = 114,
        ['ɴ'] = 115,
        ['ø'] = 116,
        ['ɸ'] = 118,
        ['θ'] = 119,
        ['œ'] = 120,
        ['ɹ'] = 123,
        ['ɾ'] = 125,
        ['ɻ'] = 126,
        ['ʁ'] = 128,
        ['ɽ'] = 129,
        ['ʂ'] = 130,
        ['ʃ'] = 131,
        ['ʈ'] = 132,
        ['ʧ'] = 133,
        ['ʊ'] = 135,
        ['ʋ'] = 136,
        ['ʌ'] = 138,
        ['ɣ'] = 139,
        ['ɤ'] = 140,
        ['χ'] = 142,
        ['ʎ'] = 143,
        ['ʒ'] = 147,
        ['ʔ'] = 148,
        ['ˈ'] = 156,
        ['ˌ'] = 157,
        ['ː'] = 158,
        ['ʰ'] = 162,
        ['ʲ'] = 164,
        ['↓'] = 169,
        ['→'] = 171,
        ['↗'] = 172,
        ['↘'] = 173,
        ['ᵻ'] = 177,
    };

    // Converts a phoneme string to the inner token id list (without the surrounding pad tokens),
    // dropping unsupported characters and truncating to MaxTokens.
    public static long[] Encode(string phonemes)
    {
        var ids = new List<long>(Math.Min(phonemes.Length, MaxTokens));
        foreach (var ch in phonemes)
        {
            if (Vocab.TryGetValue(ch, out var id))
                ids.Add(id);
        }

        if (ids.Count > MaxTokens)
            ids.RemoveRange(MaxTokens, ids.Count - MaxTokens);

        return ids.ToArray();
    }
}
