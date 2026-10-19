using Lumina.Excel.Sheets;
using Lumina.Extensions;

namespace Mappy.Classes.Caches;

public class TooltipCache : MappyCache<uint, string>
{
    protected override string LoadValue(uint key)
    {
        var mapMarker = Service.DataManager.GetExcelSheet<MapSymbol>().FirstOrNull(marker => marker.Icon == key);

        if (mapMarker is null) return string.Empty;
        if (!mapMarker.Value.PlaceName.IsValid) return string.Empty;

        return mapMarker.Value.PlaceName.Value.Name.ExtractText();
    }
}