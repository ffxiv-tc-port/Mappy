using Lumina.Excel.Sheets;

namespace Mappy.Classes.Caches;

public class TripleTriadCache : MappyCache<uint, bool>
{
    protected override bool LoadValue(uint key) => Service.DataManager.GetExcelSheet<TripleTriad>().HasRow(key);
}