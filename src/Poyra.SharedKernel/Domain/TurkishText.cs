namespace Poyra.SharedKernel.Domain;

public static class TurkishText
{
    public static string Fold(string value)
        => value.Trim().Replace('İ', 'i').ToLowerInvariant();

    public static string? FoldOrNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : Fold(value);

    public static string NormalizeEmail(string email) => Fold(email);
}
