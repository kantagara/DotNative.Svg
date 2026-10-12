# Contributing

Keep public interfaces, registration methods and rasterizer stages readable with
separate declarations, expanded blocks and consistent indentation.

Every DI plugin exposes exactly one service accessor per target framework. Put
the `IServiceProvider` extension property inside `#if NET10_0_OR_GREATER` and its
`Svg()` extension method inside `#else`. Resolve through `GetRequiredService<T>()`
each time. A `net10.0` application uses the property; `net9.0` uses the method.

The project owns plugin metadata. `build/PluginAuthoring.targets` generates the
NuGet plugin manifest during packing. This plugin is managed: it has no native
registration entry.
