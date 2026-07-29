namespace SteamLoader.App.Infrastructure.Store;

internal sealed record StoreRegionDefinition(
    string Code,
    string Name,
    string CountryCode,
    string CurrencyCode,
    string CurrencySymbol,
    string Locale,
    string XboxLanguage);

internal static class StoreRegionCatalog
{
    public static IReadOnlyList<StoreRegionDefinition> All { get; } =
    [
        new("US", "United States", "US", "USD", "$", "en-US", "en-us"),
        new("DE", "Eurozone", "DE", "EUR", "€", "de-DE", "de-de"),
        new("GB", "United Kingdom", "GB", "GBP", "£", "en-GB", "en-gb"),
        new("CA", "Canada", "CA", "CAD", "CA$", "en-CA", "en-ca"),
        new("AU", "Australia", "AU", "AUD", "A$", "en-AU", "en-au"),
        new("NZ", "New Zealand", "NZ", "NZD", "NZ$", "en-NZ", "en-nz"),
        new("BR", "Brazil", "BR", "BRL", "R$", "pt-BR", "pt-br"),
        new("MX", "Mexico", "MX", "MXN", "MX$", "es-MX", "es-mx"),
        new("CL", "Chile", "CL", "CLP", "CLP$", "es-CL", "es-cl"),
        new("CO", "Colombia", "CO", "COP", "COL$", "es-CO", "es-co"),
        new("JP", "Japan", "JP", "JPY", "¥", "ja-JP", "ja-jp"),
        new("KR", "South Korea", "KR", "KRW", "₩", "ko-KR", "ko-kr"),
        new("CN", "China", "CN", "CNY", "CN¥", "zh-CN", "zh-cn")
    ];

    public static StoreRegionDefinition Resolve(string? code) =>
        All.FirstOrDefault(region => region.Code.Equals(code?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? All[0];
}
