using System;
using System.Buffers.Binary;
using Lumina.Data.Files;

namespace Mappy.MapRenderer;

/// <summary>
/// 把地圖前景與底紋兩張 BC1（DXT1）貼圖直接解成「相乘後的 RGBA」，不經 Lumina 的整張解碼。
/// 透明照 GPU 取樣 BC1 的規則（color0 &lt;= color1 時第四色是透明），與只有前景、直接交給遊戲貼圖的地圖一致；
/// 不能用 Lumina 的 <c>GetRgbaImageData()</c>：它把 BC1 一律解成不透明，透明處變成黑色。
/// 兩張都不透明的像素＝逐通道 <c>bg * fg / 255</c>；只有一張不透明就取那一張；兩張都透明＝(0,0,0,0)。
/// </summary>
public static class MapTextureBlend
{
    public const int Size = 2048;

    private const int BlocksPerRow = Size / 4;
    private const int Bc1BytesPerBlock = 8;
    private const int Mip0Bytes = BlocksPerRow * BlocksPerRow * Bc1BytesPerBlock;

    /// <summary>
    /// 兩張都是 2048×2048、單層的 BC1 才處理並回傳新的 RGBA 陣列；其餘格式回 null，呼叫端走原本的整張解碼。
    /// </summary>
    public static byte[]? TryBlendBc1(TexFile background, TexFile foreground)
    {
        if (!IsSupported(background) || !IsSupported(foreground)) return null;

        var result = GC.AllocateUninitializedArray<byte>(Size * Size * 4);
        var bgData = background.TextureBuffer.RawData.AsSpan(0, Mip0Bytes);
        var fgData = foreground.TextureBuffer.RawData.AsSpan(0, Mip0Bytes);

        Span<byte> bgPalette = stackalloc byte[16];
        Span<byte> fgPalette = stackalloc byte[16];

        for (var blockY = 0; blockY < BlocksPerRow; blockY++)
        for (var blockX = 0; blockX < BlocksPerRow; blockX++) {
            var blockOffset = (blockY * BlocksPerRow + blockX) * Bc1BytesPerBlock;
            var bgIndices = DecodePalette(bgData.Slice(blockOffset, Bc1BytesPerBlock), bgPalette);
            var fgIndices = DecodePalette(fgData.Slice(blockOffset, Bc1BytesPerBlock), fgPalette);

            for (var pixel = 0; pixel < 16; pixel++) {
                var bg = (int)((bgIndices >> (pixel * 2)) & 3) * 4;
                var fg = (int)((fgIndices >> (pixel * 2)) & 3) * 4;
                var target = ((blockY * 4 + (pixel >> 2)) * Size + blockX * 4 + (pixel & 3)) * 4;

                if ((bgPalette[bg + 3] | fgPalette[fg + 3]) == 0) {
                    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(target, 4), 0);
                    continue;
                }

                result[target + 0] = (byte)(bgPalette[bg + 0] * fgPalette[fg + 0] / 255);
                result[target + 1] = (byte)(bgPalette[bg + 1] * fgPalette[fg + 1] / 255);
                result[target + 2] = (byte)(bgPalette[bg + 2] * fgPalette[fg + 2] / 255);
                result[target + 3] = byte.MaxValue;
            }
        }

        return result;
    }

    private static bool IsSupported(TexFile file)
    {
        var buffer = file.TextureBuffer;
        return file.Header.Format == TexFile.TextureFormat.DXT1
               && buffer.Width == Size && buffer.Height == Size && buffer.Depth == 1
               && buffer.RawData.Length >= Mip0Bytes;
    }

    // 調色盤寫成 4 組 RGBA；透明那一色的 RGB 寫 255（相乘的單位元素），另一張就原樣留下。
    // 回傳 16 個像素的 2 位元索引。
    private static uint DecodePalette(ReadOnlySpan<byte> block, Span<byte> palette)
    {
        var color0 = BinaryPrimitives.ReadUInt16LittleEndian(block);
        var color1 = BinaryPrimitives.ReadUInt16LittleEndian(block[2..]);

        Expand565(color0, palette[..3]);
        Expand565(color1, palette.Slice(4, 3));

        for (var channel = 0; channel < 3; channel++) {
            int c0 = palette[channel];
            int c1 = palette[4 + channel];

            if (color0 > color1) {
                palette[8 + channel] = (byte)((2 * c0 + c1) / 3);
                palette[12 + channel] = (byte)((c0 + 2 * c1) / 3);
            }
            else {
                palette[8 + channel] = (byte)((c0 + c1) / 2);
                palette[12 + channel] = byte.MaxValue;
            }
        }

        palette[3] = palette[7] = palette[11] = byte.MaxValue;
        palette[15] = color0 > color1 ? byte.MaxValue : (byte)0;

        return BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
    }

    private static void Expand565(ushort color, Span<byte> rgb)
    {
        var r = (color & 0xF800) >> 11;
        var g = (color & 0x07E0) >> 5;
        var b = color & 0x001F;

        rgb[0] = (byte)((r << 3) | (r >> 2));
        rgb[1] = (byte)((g << 2) | (g >> 4));
        rgb[2] = (byte)((b << 3) | (b >> 2));
    }
}
