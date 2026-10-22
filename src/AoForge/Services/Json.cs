using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace AoForge.Services;

// DataContractJsonSerializer keeps the exe free of NuGet dependencies.
public static class Json
{
    static DataContractJsonSerializer Serializer<T>() =>
        new(typeof(T), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });

    public static T Parse<T>(byte[] utf8)
    {
        using var ms = new MemoryStream(utf8);
        return (T)Serializer<T>().ReadObject(ms);
    }

    public static T Parse<T>(string text) => Parse<T>(Encoding.UTF8.GetBytes(text));

    public static T ReadFile<T>(string path) => Parse<T>(File.ReadAllBytes(path));

    public static byte[] ToBytes<T>(T value)
    {
        using var ms = new MemoryStream();
        using (var writer = JsonReaderWriterFactory.CreateJsonWriter(ms, Encoding.UTF8, false, true, "  "))
            Serializer<T>().WriteObject(writer, value);
        return ms.ToArray();
    }

    // Writes to a temp file first so a crash never leaves half a file.
    public static void WriteFile<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, ToBytes(value));
        if (File.Exists(path))
            File.Replace(tmp, path, null);
        else
            File.Move(tmp, path);
    }
}
