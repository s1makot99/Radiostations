
namespace Radiostations.Models;
public class ObjectRecord
{
    public int Id { get; set; }
    public long ObjectID { get; set; }
    public int InDoc { get; set; }
    public int ResIndex { get; set; }
    public int IsGsk2011 { get; set; }
    public string ObjectLocationName { get; set; } = string.Empty;
    public string FiasAoguid { get; set; } = string.Empty;
    public string ObjectLocationExtraText { get; set; } = string.Empty;
    public string ObjectName { get; set; } = string.Empty;
    public long EtsId { get; set; }
    public string EtsCode { get; set; } = string.Empty;
    public string EtsName { get; set; } = string.Empty;
    public long DocumentID { get; set; }
}