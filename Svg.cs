using System.Security.Cryptography;
using DotNative;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DotNative.Svg;

public interface ISvg
{
    /// <summary>
    /// Rasterizes an SVG document to a PNG sized in logical points.
    /// The bitmap is four times that size so a phone screen stays sharp.
    /// A color replaces painted red, green and blue and keeps the coverage.
    /// </summary>
    ImageSource Picture(
        ReadOnlySpan<byte> document,
        float width,
        float height,
        Color? color = null
    );
}

public static class SvgServices
{
    public static IServiceCollection AddSvg(this IServiceCollection services)
    {
        services.TryAddSingleton<ISvg, SvgRenderer>();
        return services;
    }
}

internal sealed class SvgRenderer : ISvg
{
    private readonly Dictionary<Key, ImageSource> cache = new();
    private readonly Queue<Key> order = new();

    public ImageSource Picture(
        ReadOnlySpan<byte> document,
        float width,
        float height,
        Color? color = null
    )
    {
        var key = new Key(Hash(document), width, height, color ?? default, color.HasValue);
        lock (cache)
        {
            if (cache.TryGetValue(key, out var cached))
                return cached;
        }
        var source = ImageSource.FromBytes(SvgRasterizer.Png(document, width, height, color));
        lock (cache)
        {
            if (cache.TryGetValue(key, out var cached))
                return cached;
            cache.Add(key, source);
            order.Enqueue(key);
            if (order.Count > 64)
                cache.Remove(order.Dequeue());
        }
        return source;
    }

    private static string Hash(ReadOnlySpan<byte> document)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(document, hash);
        return Convert.ToHexString(hash);
    }

    private readonly record struct Key(
        string Document,
        float Width,
        float Height,
        Color Color,
        bool Tinted
    );
}
