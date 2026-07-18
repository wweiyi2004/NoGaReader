using System.Xml;
using System.Xml.Linq;

namespace NoGaReader.Services;

internal static class SafeXml
{
    public static XDocument Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public static XDocument Load(Stream stream)
    {
        return Load(stream, DtdProcessing.Prohibit);
    }

    public static XDocument LoadContent(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream, DtdProcessing.Ignore);
    }

    private static XDocument Load(Stream stream, DtdProcessing dtdProcessing)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = dtdProcessing,
            XmlResolver = null,
            MaxCharactersInDocument = 100_000_000,
            IgnoreComments = true
        };

        using var reader = XmlReader.Create(stream, settings);
        return XDocument.Load(reader, LoadOptions.None);
    }
}
