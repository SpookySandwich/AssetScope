using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace AssetScope.Core.Diagnostics;

/// <summary>
/// Synchronous, process-local diagnostic log for the native shell. A new main
/// process truncates the previous log so a reproduction contains one session.
/// Writes are deliberately synchronous: fatal WinUI/WinRT exceptions may end
/// the process immediately after an unhandled-exception callback returns.
/// </summary>
public static partial class AppLog
{
    private static readonly object Gate = new();
    private static bool _initialized;

    public static string FilePath { get; private set; } =
        Path.Combine(AppSettings.Directory, "app.log");

    public static void Initialize(string? filePath = null)
    {
        lock (Gate)
        {
            FilePath = filePath ?? Path.Combine(AppSettings.Directory, "app.log");
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

            var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version?.ToString() ?? "unknown";
            var header = new StringBuilder()
                .AppendLine("# assetscope diagnostic log")
                .AppendLine($"# startedUtc={DateTimeOffset.UtcNow:O}")
                .AppendLine($"# version={version} pid={Environment.ProcessId} architecture={RuntimeInformation.ProcessArchitecture}")
                .AppendLine($"# os={Sanitize(RuntimeInformation.OSDescription)} runtime={Sanitize(RuntimeInformation.FrameworkDescription)}")
                .AppendLine("# Previous session log was replaced at startup. Secrets and tokens are redacted.")
                .AppendLine();

            File.WriteAllText(FilePath, header.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _initialized = true;
        }
    }

    public static void Breadcrumb(string area, string action) => Write("TRACE", area, action);
    public static void Info(string area, string message) => Write("INFO", area, message);
    public static void Warn(string area, string message) => Write("WARN", area, message);
    public static void Error(string area, string message, Exception? exception = null) =>
        Write("ERROR", area, exception is null ? message : $"{message}{Environment.NewLine}{FormatException(exception)}");
    public static void Fatal(string area, Exception exception) =>
        Write("FATAL", area, FormatException(exception));

    private static void Write(string level, string area, string message)
    {
        try
        {
            lock (Gate)
            {
                if (!_initialized) Initialize();
                var safeArea = Sanitize(area).ReplaceLineEndings(" ");
                var safeMessage = Sanitize(message).ReplaceLineEndings(Environment.NewLine + "    ");
                var line = $"{DateTimeOffset.UtcNow:O} [{level}] [t{Environment.CurrentManagedThreadId}] [{safeArea}] {safeMessage}{Environment.NewLine}";
                File.AppendAllText(FilePath, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch
        {
            // Logging must never become a second crash path.
        }
    }

    private static string FormatException(Exception exception)
    {
        var builder = new StringBuilder();
        var current = exception;
        for (var depth = 0; current is not null && depth < 8; depth++)
        {
            if (depth > 0) builder.AppendLine("Caused by:");
            builder.Append(current.GetType().FullName)
                .Append(" (HRESULT 0x")
                .Append(current.HResult.ToString("X8"))
                .Append("): ")
                .AppendLine(current.Message);
            if (!string.IsNullOrWhiteSpace(current.StackTrace)) builder.AppendLine(current.StackTrace);
            current = current.InnerException;
        }
        return builder.ToString().TrimEnd();
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var result = PlaidTokenPattern().Replace(value, "$1<redacted>");
        result = NamedSecretPattern().Replace(result, "$1<redacted>");
        return result.Length <= 32_000 ? result : result[..32_000] + "…<truncated>";
    }

    [GeneratedRegex(@"(?i)\b(access-(?:sandbox|development|production)-|public-(?:sandbox|development|production)-|link-(?:sandbox|development|production)-)[A-Za-z0-9_-]+")]
    private static partial Regex PlaidTokenPattern();

    [GeneratedRegex(@"(?i)((?:PLAID[_ -]?(?:SECRET|CLIENT[_ -]?ID)|Authorization|Bearer|secret|access[_ -]?token)\s*[:=]\s*)[^\s,;]+")]
    private static partial Regex NamedSecretPattern();
}
