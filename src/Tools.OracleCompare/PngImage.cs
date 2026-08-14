using System.IO.Compression;

namespace FairyNext.Tools.OracleCompare;

/// <summary>
/// 最小 PNG 解码器 → RGBA8（row 0 = 图像顶行）。
///
/// 为什么要自己解：像素门要能在**没有 Unity、没有第三方图像库**的 CI 上跑，而 golden 里存的是 PNG
/// （一份产物，人能直接看）。若改存 raw 像素旁路文件，PNG 与 raw 会各自漂移——两份事实源就是两份 bug。
/// zlib 流用 <see cref="DeflateStream"/> 解（netstandard2.1 内置），只需跳过 2 字节 zlib 头。
///
/// 覆盖面：bitDepth=8、colorType 0/2/4/6、非隔行、filter 0-4。Unity 的 EncodeToPNG(RGBA32) 落在 colorType=6。
/// 其余（16 位、调色板、Adam7 隔行）直接抛——**响亮失败**，不做静默降级。
/// </summary>
public sealed class PngImage
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>长度 = Width*Height*4，顺序 R,G,B,A，row-major，row 0 为顶行。</summary>
    public byte[] Rgba { get; }

    private PngImage(int width, int height, byte[] rgba)
    {
        Width = width;
        Height = height;
        Rgba = rgba;
    }

    /// <summary>
    /// 用现成的 RGBA 缓冲建图。给两类调用方：单测构造扰动图（否则要在测试里再写一个 PNG 编码器），
    /// 以及未来的 mock 参考光栅——它本来就产出 RGBA 缓冲，不该为了进比对器先编码成 PNG 再解回来。
    /// </summary>
    public static PngImage FromRgba(int width, int height, byte[] rgba)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException($"尺寸非法 {width}x{height}");
        if (rgba.Length != width * height * 4)
            throw new ArgumentException($"缓冲长度 {rgba.Length} ≠ {width}*{height}*4");
        return new PngImage(width, height, rgba);
    }

    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    public static PngImage Decode(byte[] png)
    {
        if (png.Length < 8) throw new FormatException("PNG: 文件太短");
        for (int i = 0; i < 8; i++)
            if (png[i] != Signature[i]) throw new FormatException("PNG: 签名不对");

        int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
        bool sawIhdr = false;
        var idat = new MemoryStream();

        int p = 8;
        while (p + 8 <= png.Length)
        {
            int len = ReadInt32(png, p);
            string type = System.Text.Encoding.ASCII.GetString(png, p + 4, 4);
            int dataAt = p + 8;
            if (len < 0 || dataAt + len + 4 > png.Length) throw new FormatException($"PNG: 块 {type} 长度越界");

            if (type == "IHDR")
            {
                width = ReadInt32(png, dataAt);
                height = ReadInt32(png, dataAt + 4);
                bitDepth = png[dataAt + 8];
                colorType = png[dataAt + 9];
                interlace = png[dataAt + 12];
                sawIhdr = true;
            }
            else if (type == "IDAT") idat.Write(png, dataAt, len);
            else if (type == "IEND") break;

            p = dataAt + len + 4; // + CRC
        }

        if (!sawIhdr) throw new FormatException("PNG: 缺 IHDR");
        if (bitDepth != 8) throw new FormatException($"PNG: 只支持 bitDepth=8，实为 {bitDepth}");
        if (interlace != 0) throw new FormatException("PNG: 不支持 Adam7 隔行");
        if (width <= 0 || height <= 0) throw new FormatException($"PNG: 尺寸非法 {width}x{height}");

        int channels = colorType switch
        {
            0 => 1,  // 灰度
            2 => 3,  // RGB
            4 => 2,  // 灰度 + alpha
            6 => 4,  // RGBA
            _ => throw new FormatException($"PNG: 不支持 colorType={colorType}（3=调色板未实现）")
        };

        byte[] raw = Inflate(idat.ToArray());
        int stride = width * channels;
        long need = (long)(stride + 1) * height;
        if (raw.Length < need) throw new FormatException($"PNG: 解压后 {raw.Length}B < 需要 {need}B");

        byte[] rgba = new byte[width * height * 4];
        byte[] prior = new byte[stride];
        byte[] line = new byte[stride];

        int src = 0;
        for (int y = 0; y < height; y++)
        {
            int filter = raw[src++];
            Buffer.BlockCopy(raw, src, line, 0, stride);
            src += stride;
            Unfilter(filter, line, prior, channels);

            int dst = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int s = x * channels;
                byte r, g, b, a;
                switch (channels)
                {
                    case 1: r = g = b = line[s]; a = 255; break;
                    case 2: r = g = b = line[s]; a = line[s + 1]; break;
                    case 3: r = line[s]; g = line[s + 1]; b = line[s + 2]; a = 255; break;
                    default: r = line[s]; g = line[s + 1]; b = line[s + 2]; a = line[s + 3]; break;
                }
                rgba[dst++] = r; rgba[dst++] = g; rgba[dst++] = b; rgba[dst++] = a;
            }

            byte[] swap = prior; prior = line; line = swap;
        }

        return new PngImage(width, height, rgba);
    }

    public static PngImage Load(string path) => Decode(File.ReadAllBytes(path));

    private static int ReadInt32(byte[] b, int at)
        => (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3];

    private static byte[] Inflate(byte[] zlib)
    {
        if (zlib.Length < 2) throw new FormatException("PNG: IDAT 为空");
        // zlib 头 2 字节（CMF/FLG）；FLG bit5 = 有 FDICT（PNG 不允许），这里只跳头。
        using var input = new MemoryStream(zlib, 2, zlib.Length - 2, writable: false);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    private static void Unfilter(int filter, byte[] line, byte[] prior, int bpp)
    {
        switch (filter)
        {
            case 0: break;
            case 1:
                for (int i = bpp; i < line.Length; i++) line[i] = (byte)(line[i] + line[i - bpp]);
                break;
            case 2:
                for (int i = 0; i < line.Length; i++) line[i] = (byte)(line[i] + prior[i]);
                break;
            case 3:
                for (int i = 0; i < line.Length; i++)
                {
                    int left = i >= bpp ? line[i - bpp] : 0;
                    line[i] = (byte)(line[i] + ((left + prior[i]) >> 1));
                }
                break;
            case 4:
                for (int i = 0; i < line.Length; i++)
                {
                    int a = i >= bpp ? line[i - bpp] : 0;
                    int b = prior[i];
                    int c = i >= bpp ? prior[i - bpp] : 0;
                    int pp = a + b - c;
                    int pa = Math.Abs(pp - a), pb = Math.Abs(pp - b), pc = Math.Abs(pp - c);
                    int pred = (pa <= pb && pa <= pc) ? a : (pb <= pc ? b : c);
                    line[i] = (byte)(line[i] + pred);
                }
                break;
            default: throw new FormatException($"PNG: 未知 filter {filter}");
        }
    }
}
