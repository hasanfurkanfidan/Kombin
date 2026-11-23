using FluentValidation;
using Kombin.Api.Features.Catalog;
using Kombin.Api.Features.Wardrobe.BulkAdd;
using Kombin.Api.Persistence.Entities.Catalog;

namespace Kombin.Api.Features.Wardrobe.BulkConfirm;

/// <summary>
/// 1 to 12 pieces (the most a photo ever has), no piece twice; each piece's edits follow <c>UpdateItemValidator</c>'s rules for the same fields.
/// Whether the IDs belong to the batch and the slugs exist is the handler's (400 <c>Wardrobe.BulkUnknownPiece</c>, <c>Wardrobe.UnknownCategory</c>).
/// </summary>
internal sealed class BulkConfirmValidator : AbstractValidator<BulkConfirmRequest>
{
    public BulkConfirmValidator()
    {
        RuleFor(request => request.Pieces)
            .NotEmpty()
            .WithMessage("'Pieces' must list at least one piece; cancel the batch to add none.");

        RuleFor(request => request.Pieces!)
            .Must(pieces => pieces.Count <= BulkAddOptionsValidator.VisionPieceLimit)
            .WithMessage($"'Pieces' must not list more than {BulkAddOptionsValidator.VisionPieceLimit} pieces.")
            .Must(pieces => pieces.Where(piece => piece is not null).GroupBy(piece => piece.Id).All(group => group.Count() == 1))
            .WithMessage("'Pieces' must not list a piece twice.")
            .When(request => request.Pieces is not null);

        RuleForEach(request => request.Pieces).NotNull().SetValidator(new BulkConfirmPieceValidator());
    }
}

internal sealed class BulkConfirmPieceValidator : AbstractValidator<BulkConfirmPiece>
{
    public BulkConfirmPieceValidator()
    {
        RuleFor(piece => piece.Id).NotEqual(Guid.Empty);

        RuleFor(piece => piece.CategorySlug).NotEmpty().MaximumLength(100).When(piece => piece.CategorySlug is not null);

        RuleFor(piece => piece.PrimaryColor).Must(EnumNames.IsValid<ProductColor>).When(piece => piece.PrimaryColor is not null)
            .WithMessage($"'Primary Color' must be one of: {string.Join(", ", Enum.GetNames<ProductColor>())}.");
        RuleFor(piece => piece.SecondaryColor).Must(EnumNames.IsValid<ProductColor>).When(piece => piece.SecondaryColor is not null)
            .WithMessage($"'Secondary Color' must be one of: {string.Join(", ", Enum.GetNames<ProductColor>())}.");

        RuleForEach(piece => piece.Clear)
            .Must(field => BulkConfirmPiece.ClearableFields.Contains(field, StringComparer.OrdinalIgnoreCase))
            .WithMessage($"'Clear' entries must be one of: {string.Join(", ", BulkConfirmPiece.ClearableFields)}.");

        RuleFor(piece => piece)
            .Must(piece => !(piece.SecondaryColor is not null && piece.Clears("secondaryColor")))
            .OverridePropertyName("clear")
            .WithMessage("A field cannot be set and cleared at once: secondaryColor.");
    }
}
