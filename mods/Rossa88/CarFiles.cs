using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace Rossa88
{
    // The car's files (fit table and model) are built into the dll as resources, so the whole mod is one
    // file (what RevaMP's mod transfer and approved list handle, and the approved hash then covers the model too).
    // A file with the same name in the cars folder next to the dll wins, for tuning without a rebuild.
    public static class CarFiles
    {
        public static byte[] ReadAllBytes(string path)
        {
            if (File.Exists(path)) return File.ReadAllBytes(path);
            string name = Path.GetFileName(path);
            Assembly asm = typeof(CarFiles).Assembly;
            foreach (string res in asm.GetManifestResourceNames())
            {
                // resources are named "Rossa88.cars.<file>" (see Rossa88.csproj); match on the file name
                if (!res.EndsWith("." + name, System.StringComparison.OrdinalIgnoreCase) && res != name) continue;
                using (Stream s = asm.GetManifestResourceStream(res))
                using (var m = new MemoryStream())
                {
                    s.CopyTo(m);
                    return m.ToArray();
                }
            }
            throw new FileNotFoundException("Not in the cars folder and not built into the dll: " + name, path);
        }

        public static string[] ReadAllLines(string path)
        {
            string text = Encoding.UTF8.GetString(ReadAllBytes(path));
            if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);   // a byte order mark
            var lines = new List<string>(text.Split('\n'));
            for (int i = 0; i < lines.Count; i++) lines[i] = lines[i].TrimEnd('\r');
            return lines.ToArray();
        }
    }
}
