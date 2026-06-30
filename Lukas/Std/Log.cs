// Lukas/Std/Log.cs

using System;
using System.Threading;

namespace Lukas.Std;

/// <summary>
/// 极简控制台日志：带时间戳与级别（INFO/WARN/ERR）。
/// 通过 <see cref="Gate"/> 加锁串行写出，避免多线程下日志行交错。
/// </summary>
public static class Log
{
    private static readonly Lock Gate = new();

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERR ", message);

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";
        lock (Gate)
        {
            Io.Println(line);
            Io.FlushOut();
        }
    }
}
