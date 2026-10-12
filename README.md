# DotNative.Svg

Rasterizes an SVG document into a PNG `ImageSource`.

```csharp
builder.Services.AddSvg();
var icon = services.Svg.Picture(svgBytes, 24, 24, new Color(38, 38, 38));
```

The bitmap is four times the requested point size, so a phone screen stays sharp.
Pass a color to replace the painted red, green and blue channels while keeping
coverage. Leave the color empty to keep the colors in the file.

| Element | Supported |
| --- | --- |
| `path`, `rect`, `circle`, `ellipse`, `line`, `polyline`, `polygon` | Yes |
| Groups, transforms, opacity, fill and stroke | Yes |
| `clipPath` | Yes |
| Filters, masks, text, gradients, images | No |

Paths use the SVG command set, including arcs. A filter on a group is ignored and
its children are still painted.

## Service access

Import `DotNative.Svg` to access the plugin through `IServiceProvider`:

```csharp
using DotNative.Svg;

var plugin = services.Svg;
```

The getter calls `GetRequiredService<ISvg>()` on every access. Register the plugin
with `AddSvg()` before building the provider.

A `net10.0` application uses the property syntax with C# 14 or later. A `net9.0`
application uses `services.Svg()`.
