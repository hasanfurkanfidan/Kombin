using Kombin.Api.Persistence.Entities.Wardrobe;

namespace Kombin.Api.Features.Wardrobe.BulkAdd;

/// <summary>
/// Every Turkish line bulk add writes itself, in one place (the app never invents a sentence where the API returns none). The refusal and the
/// reason sentences are the vision team's, copied verbatim from <c>kombin-vision/docs/design/bulk-add-briefs.md</c>; the rest follows the app
/// glossary. <c>CopyGlossaryTests</c> scans this file and <c>BulkAddCopyTests</c> pins every line. A line names no number that is a setting, and
/// none carries an exclamation mark or an emoji.
/// </summary>
internal static class BulkAddCopy
{
    /// <summary>What the app shows for a reason code a newer vision service sends that we do not know yet: never an error.</summary>
    public const string UnknownReason = "Bu parçayı kontrol et.";

    /// <summary>A refusal code we do not know (a newer vision service): the photo is still refused, with advice that fits most refusals.</summary>
    public const string UnknownRefusal = "Bu fotoğraftan parçaları ayıramadık; kıyafetleri yere ya da yatağa serip yukarıdan çekmeyi dene.";

    /// <summary>The hint on a piece that looks like one of the user's own: a hint, never a block.</summary>
    public const string LikelyDuplicate = "Dolabında benzeri var.";

    /// <summary>The refusal codes of <c>POST /v1/garment/bulk</c> and the sentence the app says for each (the brief's table).</summary>
    public static readonly IReadOnlyDictionary<string, string> Refusals = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["closet"] = "Dolabın içini değil, kıyafetleri yere ya da yatağa serip yukarıdan çek.",
        ["rail"] = "Askıdaki kıyafetleri ayıramıyoruz; yere ya da yatağa serip yukarıdan çek.",
        ["pile"] = "Kıyafetler üst üste yığılmış; aralarında boşluk bırakarak ser ve tekrar çek.",
        ["worn"] = "Üzerindeki kıyafetleri değil, serilmiş kıyafetleri çek.",
        ["nothing_found"] = "Fotoğrafta kıyafet bulamadık.",
        ["too_dark"] = "Fotoğraf çok karanlık; daha aydınlık bir yerde tekrar çek.",
        ["too_blurry"] = "Fotoğraf bulanık; telefonu sabit tutup tekrar çek.",
    };

    /// <summary>The reason codes of a piece that is not pre-ticked and the sentence under its thumbnail (the brief's table).</summary>
    public static readonly IReadOnlyDictionary<string, string> Reasons = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["partly_hidden"] = "Bu parçanın bir kısmı başka bir kıyafetin altında kalmış; istersen tek başına ekle.",
        ["touching"] = "Bu parça yanındakine değiyor; kesimini kontrol et.",
        ["cut_by_frame"] = "Bu parça fotoğrafın kenarından taşıyor; tamamı görünmüyor olabilir.",
        ["may_be_several"] = "Bu parça birden fazla kıyafet olabilir; kontrol et.",
        ["may_not_be_garment"] = "Bunun bir kıyafet olduğundan emin değiliz; kontrol et.",
        ["too_small"] = "Bu parça çok küçük göründü; istersen yakından tek başına ekle.",
        ["unsure_outline"] = "Bu parçanın kenarlarını net ayıramadık; kesimini kontrol et.",
    };

    /// <summary>The sentence for a refusal code; an unknown code gets <see cref="UnknownRefusal"/>.</summary>
    public static string ForRefusal(string? code) => code is not null && Refusals.TryGetValue(code, out var sentence) ? sentence : UnknownRefusal;

    /// <summary>The sentence for a reason code; an unknown code gets <see cref="UnknownReason"/>.</summary>
    public static string ForReason(string? code) => code is not null && Reasons.TryGetValue(code, out var sentence) ? sentence : UnknownReason;

    /// <summary>The brief's line for garments the service saw together and did not cut apart; the app also shows a button to the single add.</summary>
    public static string Unseparated(int count) => $"{count} parçayı ayıramadık; onları tek tek ekleyebilirsin.";

    /// <summary>
    /// The service found more pieces than the batch keeps (a lower <c>BulkAdd:MaxPiecesPerPhoto</c> or a longer answer than the brief's 12). The line
    /// says how many were left out and what to do, and no limit, which is a setting.
    /// </summary>
    public static string CutOff(int count) => $"Fotoğrafta {count} parça daha vardı; bir seferde bu kadarını gösterebiliyoruz. Kalanları ayrı bir fotoğrafla ekleyebilirsin.";

    /// <summary>The sentence for a failed batch; whether "tekrar dene" makes sense is <see cref="IsRetryable"/>.</summary>
    public static string ForFailure(BulkFailureCode code) => code switch
    {
        BulkFailureCode.VisionBusy or BulkFailureCode.VisionUnavailable => "Şu an çok yoğunuz; birazdan tekrar dene.",
        BulkFailureCode.PhotoUnreadable => "Bu fotoğrafı açamadık; başka bir fotoğrafla dene.",
        BulkFailureCode.AnalysisFailed => "Fotoğrafı işlerken bir şeyler ters gitti; tekrar dene.",
        BulkFailureCode.Interrupted => "Fotoğrafı işlerken takıldık; tekrar dene.",
        _ => "Fotoğrafı işlerken bir şeyler ters gitti; tekrar dene.",
    };

    /// <summary>A photo vision refused as a file is not worth sending again; every other failure is ours or temporary, and <c>process</c> starts it again.</summary>
    public static bool IsRetryable(BulkFailureCode code) => code != BulkFailureCode.PhotoUnreadable;
}
