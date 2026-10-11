using System.Globalization;
using System.Text;

namespace Anjal.Webmail.Services;

/// <summary>
/// A QR code (ISO/IEC 18004) for the authenticator link (rc.13): byte mode,
/// error correction level M, versions 1 to 10 - up to 213 bytes, far more
/// than an otpauth link needs. Drawn as an SVG, so it is made on the server
/// and no outside service ever sees the secret.
/// </summary>
public static class QrCode
{
    /// <summary>The most bytes that can be encoded (version 10, level M).</summary>
    public const int MaxBytes = 213;

    // Level M, by version 1-10: error-correction codewords per block, and the blocks
    // (short blocks, their data codewords, long blocks, their data codewords).
    private static readonly int[] EcPerBlock = { 0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26 };

    private static readonly (int Short, int ShortData, int Long, int LongData)[] Blocks =
    {
        (0, 0, 0, 0), (1, 16, 0, 0), (1, 28, 0, 0), (1, 44, 0, 0), (2, 32, 0, 0), (2, 43, 0, 0),
        (4, 27, 0, 0), (4, 31, 0, 0), (2, 38, 2, 39), (3, 36, 2, 37), (4, 43, 1, 44),
    };

    private static readonly int[][] Alignment =
    {
        System.Array.Empty<int>(), System.Array.Empty<int>(), new[] { 6, 18 }, new[] { 6, 22 }, new[] { 6, 26 }, new[] { 6, 30 },
        new[] { 6, 34 }, new[] { 6, 22, 38 }, new[] { 6, 24, 42 }, new[] { 6, 26, 46 }, new[] { 6, 28, 50 },
    };

    /// <summary>The modules of the code for some text: true is dark. [row, column].</summary>
    /// <param name="text">The text, encoded as UTF-8.</param>
    /// <returns>The square of modules, without the quiet zone.</returns>
    public static bool[,] Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        byte[] data = Encoding.UTF8.GetBytes(text);
        int version = 1;
        while (version <= 10 && Capacity(version) < 4 + (version < 10 ? 8 : 16) + (data.Length * 8))
        {
            version++;
        }
        if (version > 10)
        {
            throw new ArgumentException($"At most {MaxBytes} bytes fit in this QR code.", nameof(text));
        }
        byte[] codewords = Interleave(version, DataCodewords(version, data));
        var grid = new Grid(version);
        grid.DrawFunctionPatterns();
        grid.PlaceData(codewords);

        int best = 0;
        int bestPenalty = int.MaxValue;
        for (int mask = 0; mask < 8; mask++)
        {
            grid.ApplyMask(mask);
            grid.DrawFormat(mask);
            int penalty = grid.Penalty();
            if (penalty < bestPenalty)
            {
                best = mask;
                bestPenalty = penalty;
            }
            grid.ApplyMask(mask);
        }
        grid.ApplyMask(best);
        grid.DrawFormat(best);
        return grid.Modules;
    }

    /// <summary>The code as an SVG picture: dark modules on white, with the four-module quiet zone.</summary>
    /// <param name="text">The text.</param>
    /// <param name="label">What a screen reader says.</param>
    /// <returns>SVG markup.</returns>
    public static string Svg(string text, string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        bool[,] m = Encode(text);
        int size = m.GetLength(0);
        int whole = size + 8;
        var path = new StringBuilder();
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (m[y, x])
                {
                    path.Append(CultureInfo.InvariantCulture, $"M{x + 4} {y + 4}h1v1h-1z");
                }
            }
        }
        return $"<svg class=\"qr\" xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {whole} {whole}\" role=\"img\" aria-label=\"{HtmlSanitizer.Escape(label)}\" shape-rendering=\"crispEdges\">"
            + $"<rect width=\"{whole}\" height=\"{whole}\" fill=\"#FFFFFF\"/><path d=\"{path}\" fill=\"#12201F\"/></svg>";
    }

    private static int Capacity(int version)
    {
        (int s, int sd, int l, int ld) = Blocks[version];
        return ((s * sd) + (l * ld)) * 8;
    }

    private static byte[] DataCodewords(int version, byte[] data)
    {
        int capacityBits = Capacity(version);
        var bits = new List<bool>(capacityBits);
        void Put(int value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                bits.Add(((value >> i) & 1) != 0);
            }
        }
        Put(0b0100, 4);
        Put(data.Length, version < 10 ? 8 : 16);
        foreach (byte b in data)
        {
            Put(b, 8);
        }
        Put(0, Math.Min(4, capacityBits - bits.Count));
        while (bits.Count % 8 != 0)
        {
            bits.Add(false);
        }
        bool pad = true;
        while (bits.Count < capacityBits)
        {
            Put(pad ? 0xEC : 0x11, 8);
            pad = !pad;
        }
        byte[] result = new byte[capacityBits / 8];
        for (int i = 0; i < bits.Count; i++)
        {
            if (bits[i])
            {
                result[i >> 3] |= (byte)(0x80 >> (i & 7));
            }
        }
        return result;
    }

    private static byte[] Interleave(int version, byte[] data)
    {
        (int s, int sd, int l, int ld) = Blocks[version];
        int ec = EcPerBlock[version];
        byte[] divisor = Divisor(ec);
        var dataBlocks = new List<byte[]>();
        int at = 0;
        for (int i = 0; i < s + l; i++)
        {
            int len = i < s ? sd : ld;
            dataBlocks.Add(data[at..(at + len)]);
            at += len;
        }
        List<byte[]> ecBlocks = dataBlocks.Select(b => Remainder(b, divisor)).ToList();
        var result = new List<byte>();
        int longest = Math.Max(sd, ld);
        for (int i = 0; i < longest; i++)
        {
            foreach (byte[] b in dataBlocks)
            {
                if (i < b.Length)
                {
                    result.Add(b[i]);
                }
            }
        }
        for (int i = 0; i < ec; i++)
        {
            foreach (byte[] b in ecBlocks)
            {
                result.Add(b[i]);
            }
        }
        return result.ToArray();
    }

    private static byte[] Divisor(int degree)
    {
        byte[] result = new byte[degree];
        result[degree - 1] = 1;
        int root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < degree; j++)
            {
                result[j] = Multiply(result[j], root);
                if (j + 1 < degree)
                {
                    result[j] ^= result[j + 1];
                }
            }
            root = Multiply(root, 0x02);
        }
        return result;
    }

    private static byte[] Remainder(byte[] data, byte[] divisor)
    {
        byte[] result = new byte[divisor.Length];
        foreach (byte b in data)
        {
            int factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (int i = 0; i < result.Length; i++)
            {
                result[i] ^= Multiply(divisor[i], factor);
            }
        }
        return result;
    }

    private static byte Multiply(int x, int y)
    {
        int z = 0;
        for (int i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return (byte)z;
    }

    private sealed class Grid
    {
        private readonly int version;
        private readonly int size;
        private readonly bool[,] function;

        public Grid(int version)
        {
            this.version = version;
            this.size = 17 + (4 * version);
            this.Modules = new bool[this.size, this.size];
            this.function = new bool[this.size, this.size];
        }

        public bool[,] Modules { get; }

        public void DrawFunctionPatterns()
        {
            for (int i = 0; i < this.size; i++)
            {
                this.Set(6, i, i % 2 == 0);
                this.Set(i, 6, i % 2 == 0);
            }
            this.Finder(3, 3);
            this.Finder(this.size - 4, 3);
            this.Finder(3, this.size - 4);
            int[] pos = Alignment[this.version];
            for (int i = 0; i < pos.Length; i++)
            {
                for (int j = 0; j < pos.Length; j++)
                {
                    bool corner = (i == 0 && j == 0) || (i == 0 && j == pos.Length - 1) || (i == pos.Length - 1 && j == 0);
                    if (!corner)
                    {
                        this.AlignmentAt(pos[i], pos[j]);
                    }
                }
            }
            this.DrawFormat(0);
            if (this.version >= 7)
            {
                int rem = this.version;
                for (int i = 0; i < 12; i++)
                {
                    rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
                }
                long bits = ((long)this.version << 12) | (uint)rem;
                for (int i = 0; i < 18; i++)
                {
                    bool bit = ((bits >> i) & 1) != 0;
                    int a = this.size - 11 + (i % 3);
                    int b = i / 3;
                    this.Set(a, b, bit);
                    this.Set(b, a, bit);
                }
            }
        }

        public void DrawFormat(int mask)
        {
            // Level M's format bits are 00.
            int data = mask;
            int rem = data;
            for (int i = 0; i < 10; i++)
            {
                rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            }
            int bits = ((data << 10) | rem) ^ 0x5412;
            for (int i = 0; i <= 5; i++)
            {
                this.Set(8, i, Bit(bits, i));
            }
            this.Set(8, 7, Bit(bits, 6));
            this.Set(8, 8, Bit(bits, 7));
            this.Set(7, 8, Bit(bits, 8));
            for (int i = 9; i < 15; i++)
            {
                this.Set(14 - i, 8, Bit(bits, i));
            }
            for (int i = 0; i < 8; i++)
            {
                this.Set(this.size - 1 - i, 8, Bit(bits, i));
            }
            for (int i = 8; i < 15; i++)
            {
                this.Set(8, this.size - 15 + i, Bit(bits, i));
            }
            this.Set(8, this.size - 8, true);
        }

        public void PlaceData(byte[] codewords)
        {
            int i = 0;
            for (int right = this.size - 1; right >= 1; right -= 2)
            {
                if (right == 6)
                {
                    right = 5;
                }
                for (int vert = 0; vert < this.size; vert++)
                {
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j;
                        bool upward = ((right + 1) & 2) == 0;
                        int y = upward ? this.size - 1 - vert : vert;
                        if (!this.function[y, x] && i < codewords.Length * 8)
                        {
                            this.Modules[y, x] = ((codewords[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                            i++;
                        }
                    }
                }
            }
        }

        public void ApplyMask(int mask)
        {
            for (int y = 0; y < this.size; y++)
            {
                for (int x = 0; x < this.size; x++)
                {
                    bool invert = mask switch
                    {
                        0 => (x + y) % 2 == 0,
                        1 => y % 2 == 0,
                        2 => x % 3 == 0,
                        3 => (x + y) % 3 == 0,
                        4 => ((x / 3) + (y / 2)) % 2 == 0,
                        5 => ((x * y) % 2) + ((x * y) % 3) == 0,
                        6 => (((x * y) % 2) + ((x * y) % 3)) % 2 == 0,
                        _ => (((x + y) % 2) + ((x * y) % 3)) % 2 == 0,
                    };
                    if (invert && !this.function[y, x])
                    {
                        this.Modules[y, x] = !this.Modules[y, x];
                    }
                }
            }
        }

        public int Penalty()
        {
            int result = 0;
            for (int pass = 0; pass < 2; pass++)
            {
                for (int a = 0; a < this.size; a++)
                {
                    int run = 1;
                    for (int b = 1; b <= this.size; b++)
                    {
                        if (b < this.size && this.At(pass, a, b) == this.At(pass, a, b - 1))
                        {
                            run++;
                            continue;
                        }
                        if (run >= 5)
                        {
                            result += 3 + (run - 5);
                        }
                        run = 1;
                    }
                    for (int b = 0; b + 11 <= this.size; b++)
                    {
                        if (this.Finderlike(pass, a, b))
                        {
                            result += 40;
                        }
                    }
                }
            }
            int dark = 0;
            for (int y = 0; y < this.size; y++)
            {
                for (int x = 0; x < this.size; x++)
                {
                    if (this.Modules[y, x])
                    {
                        dark++;
                    }
                    if (y + 1 < this.size && x + 1 < this.size)
                    {
                        bool c = this.Modules[y, x];
                        if (c == this.Modules[y, x + 1] && c == this.Modules[y + 1, x] && c == this.Modules[y + 1, x + 1])
                        {
                            result += 3;
                        }
                    }
                }
            }
            int total = this.size * this.size;
            int k = ((Math.Abs((dark * 20) - (total * 10)) + total - 1) / total) - 1;
            return result + (Math.Max(0, k) * 10);
        }

        private static bool Bit(int value, int i) => ((value >> i) & 1) != 0;

        private bool At(int pass, int a, int b) => pass == 0 ? this.Modules[a, b] : this.Modules[b, a];

        // 1011101 with four light modules before or after it.
        private bool Finderlike(int pass, int a, int b)
        {
            bool[] p = { true, false, true, true, true, false, true };
            bool core(int from)
            {
                for (int i = 0; i < 7; i++)
                {
                    if (this.At(pass, a, from + i) != p[i])
                    {
                        return false;
                    }
                }
                return true;
            }
            bool light(int from)
            {
                for (int i = 0; i < 4; i++)
                {
                    if (this.At(pass, a, from + i))
                    {
                        return false;
                    }
                }
                return true;
            }
            return (light(b) && core(b + 4)) || (core(b) && light(b + 7));
        }

        private void Finder(int cx, int cy)
        {
            for (int dy = -4; dy <= 4; dy++)
            {
                for (int dx = -4; dx <= 4; dx++)
                {
                    int x = cx + dx;
                    int y = cy + dy;
                    if (x >= 0 && x < this.size && y >= 0 && y < this.size)
                    {
                        int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                        this.Set(x, y, dist != 2 && dist != 4);
                    }
                }
            }
        }

        private void AlignmentAt(int cx, int cy)
        {
            for (int dy = -2; dy <= 2; dy++)
            {
                for (int dx = -2; dx <= 2; dx++)
                {
                    this.Set(cx + dx, cy + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
                }
            }
        }

        private void Set(int x, int y, bool dark)
        {
            this.Modules[y, x] = dark;
            this.function[y, x] = true;
        }
    }
}
