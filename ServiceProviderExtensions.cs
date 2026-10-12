using Microsoft.Extensions.DependencyInjection;

namespace DotNative.Svg;

public static class SvgServiceProviderExtensions
{
#if NET10_0_OR_GREATER
    extension(IServiceProvider services)
    {
        /// <summary>Resolves the registered plugin using the provider's DI lifetime.</summary>
        public ISvg Svg => services.GetRequiredService<ISvg>();
    }
#else
    /// <summary>Resolves the registered plugin using the provider's DI lifetime.</summary>
    public static ISvg Svg(this IServiceProvider services) => services.GetRequiredService<ISvg>();
#endif
}
