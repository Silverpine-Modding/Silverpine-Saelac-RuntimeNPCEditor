#nullable enable

using System;
using System.IO;

namespace RuntimeNPCEditor;

internal static class AtomicFile
{
    internal static void WriteAllText(string path, string contents)
    {
        // Keep the prior complete file if serialization/write/replacement fails.
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, contents);
            if (File.Exists(path))
                File.Replace(temporary, path, path + ".bak");
            else
                File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
