using DotNative;
using DotNative.Svg;
using Xunit;

var red = Raster(
    """<svg viewBox="0 0 10 10"><rect width="10" height="10" fill="#ff0000"/></svg>"""
);
Assert.Equal(255, At(red, 5, 5).R);
Assert.Equal(0, At(red, 5, 5).G);
Assert.True(At(red, 5, 5).A > 200);

var hole = Raster(
    """
    <svg viewBox="0 0 10 10">
      <path fill="#000" fill-rule="evenodd" d="M0 0H10V10H0Z M3 3H7V7H3Z"/>
    </svg>
    """
);
Assert.True(At(hole, 1, 1).A > 200);
Assert.Equal(0, At(hole, 5, 5).A);

var tinted = SvgRasterizer.Png(
    System.Text.Encoding.UTF8.GetBytes(
        """<svg viewBox="0 0 4 4"><rect width="4" height="4" fill="#112233"/></svg>"""
    ),
    4,
    4,
    new Color(10, 20, 30)
);
Assert.Equal(137, tinted[0]);

var home = File.ReadAllBytes(Asset("home.svg"));
var icon = RasterBytes(home);
var opaque = 0;
var clear = 0;
for (var i = 3; i < icon.Pixels.Length; i += 4)
{
    if (icon.Pixels[i] > 200)
        opaque++;
    else if (icon.Pixels[i] == 0)
        clear++;
}
Assert.True(opaque > 100);
Assert.True(clear > 100);

var sports = File.ReadAllBytes(Asset("all_sports.svg"));
var ball = RasterBytes(sports);
Assert.True(Has(ball, 237, 135, 58));
Assert.True(Has(ball, 246, 246, 242));

Console.WriteLine("PASS");

static string Asset(string name) =>
    Path.GetFullPath(
        Path.Combine(
            AppContext.BaseDirectory,
            "../../../../fixtures",
            name
        )
    );

static Bitmap Raster(string svg) =>
    SvgRasterizer.Rasterize(System.Text.Encoding.UTF8.GetBytes(svg), 32, 32);

static Bitmap RasterBytes(byte[] svg) => SvgRasterizer.Rasterize(svg, 32, 32);

static (byte R, byte G, byte B, byte A) At(Bitmap bitmap, float x, float y)
{
    var px = (int)(x / 10f * bitmap.Width);
    var py = (int)(y / 10f * bitmap.Height);
    var index = (py * bitmap.Width + px) * 4;
    return (bitmap.Pixels[index], bitmap.Pixels[index + 1], bitmap.Pixels[index + 2], bitmap.Pixels[index + 3]);
}

static bool Has(Bitmap bitmap, byte r, byte g, byte b)
{
    for (var i = 0; i < bitmap.Pixels.Length; i += 4)
    {
        if (
            Math.Abs(bitmap.Pixels[i] - r) < 8
            && Math.Abs(bitmap.Pixels[i + 1] - g) < 8
            && Math.Abs(bitmap.Pixels[i + 2] - b) < 8
            && bitmap.Pixels[i + 3] > 200
        )
            return true;
    }
    return false;
}
