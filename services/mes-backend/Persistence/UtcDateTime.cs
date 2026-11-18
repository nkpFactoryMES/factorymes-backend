using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FactoryMES.Backend.Persistence;

public static class UtcDateTime
{
    public static DateTime RequireUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc
        ? value
        : throw new ArgumentException(
        "UTC DateTime is required; normalize the API timestamp explicitly."
    );

    public static readonly ValueConverter<DateTime, DateTime> Converter =
        new(
        value => RequireUtc(value),
        value => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    );
}
