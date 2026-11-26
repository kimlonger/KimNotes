using System;
using System.IO;
using KimNotes.utils;

internal static class Program
{
    private static readonly object writeLock = new object();

    internal static void LogError(Exception error)
    {
        if (error == null) return;
        try
        {
            if (Storage.Incognito) return;
            Directory.CreateDirectory(Storage.Root);
            lock (writeLock)
                File.AppendAllText(Path.Combine(Storage.Root, "error.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{error}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
