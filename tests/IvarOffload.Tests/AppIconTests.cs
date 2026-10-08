using System.Buffers.Binary;

namespace IvarOffload.Tests;

/// <summary>
/// The app icon and the in-app logo are well-formed: a damaged icon only shows at startup, as an error before the
/// window opens (tools/New-AppIcon.ps1 builds both).
/// </summary>
public class AppIconTests
{
    private static readonly byte[] PngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    private static string AppFolder()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Join(dir, "IvarOffload.slnx"))) return Path.Join(dir, "src", "IvarOffload.App");
        throw new DirectoryNotFoundException("The repository root was not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void The_app_icon_holds_every_size_as_an_image()
    {
        byte[] ico = File.ReadAllBytes(Path.Join(AppFolder(), "app.ico"));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2))); // an icon, not a cursor
        int count = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4));
        var sizes = new List<int>();
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> entry = ico.AsSpan(6 + 16 * i, 16);
            int size = entry[0] == 0 ? 256 : entry[0];
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]), offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            Assert.True(offset + length <= ico.Length, $"the {size} px image runs past the end of the file");
            ReadOnlySpan<byte> image = ico.AsSpan(offset, length);
            if (image.StartsWith(PngSignature)) Assert.Equal(size, BinaryPrimitives.ReadInt32BigEndian(image[16..])); // PNG width
            else
            {
                // A classic frame: BITMAPINFOHEADER, 32 bits per pixel, twice the height (the colour and the mask).
                Assert.Equal(40, BinaryPrimitives.ReadInt32LittleEndian(image));
                Assert.Equal(size, BinaryPrimitives.ReadInt32LittleEndian(image[4..]));
                Assert.Equal(size * 2, BinaryPrimitives.ReadInt32LittleEndian(image[8..]));
                Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(image[14..]));
            }
            sizes.Add(size);
        }
        Assert.Equal([16, 20, 24, 32, 40, 48, 64, 128, 256], sizes.Order());
    }

    [Fact]
    public void The_logo_mask_is_a_png()
    {
        byte[] mask = File.ReadAllBytes(Path.Join(AppFolder(), "Assets", "ivar-offload-mask.png"));
        Assert.True(mask.AsSpan().StartsWith(PngSignature));
        Assert.True(BinaryPrimitives.ReadInt32BigEndian(mask.AsSpan(20)) >= 100); // PNG height
    }
}
