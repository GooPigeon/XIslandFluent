using System.IO;
using System.Text;

namespace XIslandFluent.Services;

public static class DiagnosticsLog
{
    private static readonly object Gate = new();
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XIslandFluent", "logs");

    public static string CurrentPath => Path.Combine(Folder, $"xisland-{DateTime.Now:yyyyMMdd}.log");

    public static void Write(string message, Exception? exception = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                var builder = new StringBuilder()
                    .Append(DateTimeOffset.Now.ToString("O"))
                    .Append(" | ")
                    .Append(message);
                if (exception is not null)
                    builder.Append(" | ").Append(exception.GetType().FullName).Append(": ").Append(exception.Message).AppendLine().Append(exception.StackTrace);
                builder.AppendLine();
                File.AppendAllText(CurrentPath, builder.ToString(), Encoding.UTF8);
            }
        }
        catch { }
    }
}
