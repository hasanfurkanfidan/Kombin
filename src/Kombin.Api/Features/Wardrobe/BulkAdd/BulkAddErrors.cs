using Kombin.Api.Common.Results;

namespace Kombin.Api.Features.Wardrobe.BulkAdd;

/// <summary>
/// The request errors of bulk add. Like the single add's they keep English <c>detail</c> and a stable <c>errorCode</c> (the app maps them); the
/// sentences the user reads are <see cref="BulkAddCopy"/>'s. The daily limit's 429 (<see cref="DailyLimitCode"/>) is built where the limit is
/// checked, in the shape of the rate limiter's (<c>retryAfterSeconds</c> and a <c>Retry-After</c> header).
/// </summary>
internal static class BulkAddErrors
{
    public const string DailyLimitCode = "Wardrobe.BulkDailyLimit";

    /// <summary>The flag is off: every route answers this first.</summary>
    public static readonly Error Disabled = Error.NotFound("Wardrobe.BulkDisabled", "Bulk add is not enabled.");

    /// <summary>Also the answer for someone else's batch: whether it exists is none of the caller's business.</summary>
    public static readonly Error NotFound = Error.NotFound("Wardrobe.BulkNotFound", "The batch does not exist.");

    public static readonly Error UnknownPiece = Error.Validation("Wardrobe.BulkUnknownPiece", "A listed piece does not belong to this batch.");

    public static readonly Error AlreadyConfirmed = Error.Conflict("Wardrobe.BulkAlreadyConfirmed", "The batch has already been confirmed.");

    public static readonly Error NotReady = Error.Conflict("Wardrobe.BulkNotReady", "The batch has no pieces to confirm yet.");

    public static readonly Error Expired = Error.Conflict("Wardrobe.BulkExpired", "The batch was cancelled or has expired.");

    public static readonly Error TooManyOpen = Error.Conflict("Wardrobe.BulkTooManyOpen", "Too many batches are open; cancel one first.");

    /// <summary>A listed piece's staged image is gone (deleted or lost by the store): a retry fails the same way, so this is no 503. The way out is cancel.</summary>
    public static readonly Error StagedImageMissing = Error.Conflict(
        "Wardrobe.BulkStagedImageMissing", "A staged image of a listed piece is gone; the batch cannot be confirmed. Cancel it and add the photo again.");

    public static readonly Error StorageUnavailable = Error.Unavailable("Wardrobe.StorageUnavailable", "The photos could not be stored right now; try again.");
}
