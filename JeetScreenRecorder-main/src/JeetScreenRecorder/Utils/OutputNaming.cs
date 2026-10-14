namespace JeetScreenRecorder.Utils;

public static class OutputNaming
{
    public static string Generate(string prefix, string extension, DateTime now) =>
        $"{prefix}{now:yyyy-MM-dd_HH-mm-ss}.{extension.TrimStart('.')}";
}
