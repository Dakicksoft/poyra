namespace Poyra.Modules.Recon.Domain;

/// <summary>
/// Komisyon anlaşması seçimi — <b>EN ÖZEL eşleşme kazanır</b>: kartın bankasına özel oran
/// (on-us) varsa o, yoksa genel oran (<c>BankCode = null</c>).
///
/// Bu kural TEK bir yerde yaşamak zorundadır, çünkü aynı oranı üç ayrı tüketici okur:
/// rota motorunun maliyet sinyali, alacak defterinin beklenen komisyonu ve ekstre
/// denetiminin karşılaştırma tabanı. Üçü ayrışırsa sonuç sessiz bir yanlış değil,
/// <b>bankaya haksız suçlama</b> olur: rota %1,80 (on-us) bekler, defter %2,50 (genel)
/// yazar, ekstrede banka %1,80 keser ve denetim "banka eksik kesmiş" diye sahte bulgu
/// üretir. Bu yüzden üç tüketici de bu fonksiyondan geçer.
/// </summary>
public static class CommissionAgreementResolver
{
    /// <param name="cardBank">Kartı çıkaran banka kodu; bilinmiyorsa null → yalnız genel oran.</param>
    public static CommissionAgreement? Resolve(
        IEnumerable<CommissionAgreement> agreements, int installments, string? cardBank)
    {
        CommissionAgreement? general = null;

        foreach (var agreement in agreements)
        {
            if (agreement.InstallmentCount != installments)
                continue;

            if (agreement.BankCode is null)
            {
                general = agreement;
                continue;
            }

            // Kart bankası bilinmiyorsa bankaya özel oran UYGULANAMAZ: hosted akışta kart
            // henüz girilmemiş olabilir. O hâlde genel orana düşülür — on-us varsayımıyla
            // ucuz bir oran uydurmak, defterde eksik alacak yazmak demektir.
            if (cardBank is { Length: > 0 }
                && string.Equals(agreement.BankCode, cardBank, StringComparison.OrdinalIgnoreCase))
                return agreement;
        }

        return general;
    }
}
