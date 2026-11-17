
using System.Xml.Linq;
using Radiostations.Models;

namespace Radiostations.Services;

public class XmlParserService
{
    public List<ObjectRecord> ParseXmlFile(string filePath)
    {
        var result = new List<ObjectRecord>();
        var doc = XDocument.Load(filePath);
        
        var documentIdElement = doc.Root?.Element("DocumentID");
        if (documentIdElement == null) return result;
        
        long documentId = (long)documentIdElement;
        
        var objects = doc.Descendants("Object");
        foreach (var obj in objects)
        {
            var record = new ObjectRecord
            {
                ObjectID = GetLongValue(obj, "ObjectID"),
                InDoc = GetIntValue(obj, "InDoc"),
                ResIndex = GetIntValue(obj, "ResIndex"),
                IsGsk2011 = GetIntValue(obj, "isGsk2011"),
                ObjectLocationName = GetStringValue(obj, "ObjectLocationName"),
                FiasAoguid = GetStringValue(obj, "FiasAoguid"),
                ObjectLocationExtraText = GetStringValue(obj, "ObjectLocationExtraText"),
                ObjectName = GetStringValue(obj, "ObjectName"),
                EtsId = GetLongValue(obj, "EtsId"),
                EtsCode = GetStringValue(obj, "EtsCode"),
                EtsName = GetStringValue(obj, "EtsName"),
                DocumentID = documentId
            };
            result.Add(record);
        }
        
        return result;
    }
    
    private static string GetStringValue(XElement parent, string elementName)
    {
        return parent.Element(elementName)?.Value ?? string.Empty;
    }
    
    private static int GetIntValue(XElement parent, string elementName)
    {
        var element = parent.Element(elementName);
        return element != null && int.TryParse(element.Value, out int value) ? value : 0;
    }
    
    private static long GetLongValue(XElement parent, string elementName)
    {
        var element = parent.Element(elementName);
        return element != null && long.TryParse(element.Value, out long value) ? value : 0;
    }
}
