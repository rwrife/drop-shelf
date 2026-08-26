namespace DropShelf.Core;

public enum ValidationErrorCode
{
    Required, TooLong, InvalidIdentifier, InvalidTimestamp, InvalidOrdinal, InvalidPath, InvalidUrl,
    UnsupportedUrlScheme, InvalidPayload, DuplicateIdentifier, ItemNotFound, InvalidSettings, InvalidExport,
}

public sealed class ShelfValidationException : Exception
{
    public ShelfValidationException(ValidationErrorCode code, string field, string message) : base(message) =>
        (Code, Field) = (code, field);

    public ValidationErrorCode Code { get; }
    public string Field { get; }
}

internal static class Input
{
    public static string Required(string? value, int maximumLength, string field, bool trim = false)
    {
        if (value is null)
        {
            throw Error(ValidationErrorCode.Required, field, "A value is required.");
        }

        string normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Normalize();
        normalized = trim ? normalized.Trim() : normalized.TrimEnd();
        return string.IsNullOrWhiteSpace(normalized)
            ? throw Error(ValidationErrorCode.Required, field, "A non-empty value is required.")
            : normalized.Length > maximumLength
            ? throw Error(ValidationErrorCode.TooLong, field, $"The value exceeds the {maximumLength} character limit.")
            : normalized;
    }

    public static string? Optional(string? value, int maximumLength, string field) =>
        string.IsNullOrWhiteSpace(value) ? null : Required(value, maximumLength, field, true);

    public static ShelfValidationException Error(ValidationErrorCode code, string field, string message) => new(code, field, message);
}
