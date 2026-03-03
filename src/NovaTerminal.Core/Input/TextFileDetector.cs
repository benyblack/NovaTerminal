using System;
using System.IO;
using System.Text;

namespace NovaTerminal.Core.Input
{
    public static class TextFileDetector
    {
        public static bool IsTextFile(string path, int maxSize = 256 * 1024)
        {
            try
            {
                var fileInfo = new FileInfo(path);
                if (!fileInfo.Exists || fileInfo.Length == 0 || fileInfo.Length > maxSize)
                {
                    return false;
                }

                int bytesToRead = (int)Math.Min(8192, fileInfo.Length);
                byte[] buffer = new byte[bytesToRead];

                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    int bytesRead = fs.Read(buffer, 0, bytesToRead);
                    if (bytesRead == 0) return false;

                    int nulCount = 0;
                    for (int i = 0; i < bytesRead; i++)
                    {
                        if (buffer[i] == 0x00)
                        {
                            nulCount++;
                            return false; 
                        }
                    }

                    var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
                    string decoded = utf8.GetString(buffer, 0, bytesRead);

                    int replacementCharCount = 0;
                    foreach (char c in decoded)
                    {
                        if (c == '\uFFFD')
                        {
                            replacementCharCount++;
                        }
                    }

                    if (replacementCharCount > (decoded.Length * 0.05))
                    {
                        return false;
                    }

                    return true;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
