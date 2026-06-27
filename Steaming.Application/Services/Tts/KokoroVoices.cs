namespace Steaming.Application.Services.Tts;

// Loads a single Kokoro voice "style" embedding. The onnx-community voice pack ships one raw
// float32 file per voice (e.g. voices/af_heart.bin), shaped [510, 1, 256] — i.e. 510 length-indexed
// rows of a 256-dim style vector. Kokoro picks the row matching the token count.
public sealed class KokoroVoice
{
    public const int StyleDim = 256;

    private readonly float[] _data;   // flattened rows * StyleDim
    private readonly int _rows;

    private KokoroVoice(float[] data)
    {
        _data = data;
        _rows = data.Length / StyleDim;
    }

    public static KokoroVoice Load(string binPath)
    {
        if (string.IsNullOrWhiteSpace(binPath) || !File.Exists(binPath))
            throw new FileNotFoundException("Kokoro voice file not found", binPath ?? "(null)");

        var bytes = File.ReadAllBytes(binPath);
        if (bytes.Length % 4 != 0 || bytes.Length < StyleDim * 4)
            throw new InvalidDataException($"Unexpected Kokoro voice file size: {bytes.Length}");

        var data = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, data, 0, bytes.Length);   // little-endian float32 on Windows x64
        return new KokoroVoice(data);
    }

    // Returns the 256-dim style vector for the given token count.
    public float[] GetStyle(int tokenCount)
    {
        int row = Math.Clamp(tokenCount, 0, Math.Max(0, _rows - 1));
        var style = new float[StyleDim];
        Array.Copy(_data, row * StyleDim, style, 0, StyleDim);
        return style;
    }
}
