namespace Poyra.SharedKernel.Time;

/// <summary>
/// Resmî tatil takvimi.
/// <b>Neden çekirdekte:</b> takvim <see cref="IClock"/> ile aynı kategoridedir — herkesin
/// tükettiği, kimsenin sahiplenmediği bir zaman kavramı. Tüketen her modül kendi kopyasını
/// tanımlasaydı (İtirazlar, Defter, ileride Hakediş) aynı arayüz üç kez yazılırdı.
/// </summary>
public interface IBankHolidayCalendar
{
    Task<IReadOnlySet<DateOnly>> GetHolidaysAsync(CancellationToken ct);
}
