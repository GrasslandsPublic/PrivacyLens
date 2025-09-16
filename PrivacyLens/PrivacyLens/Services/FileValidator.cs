// Services/FileValidator.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PrivacyLens.Services
{
    /// <summary>
    /// Validates downloaded files by checking magic bytes (file signatures)
    /// to ensure files are not corrupted and match expected types.
    /// </summary>
    public static class FileValidator
    {
        /// <summary>
        /// Dictionary of file signatures (magic bytes) for common document types
        /// </summary>
        private static readonly Dictionary<string, List<byte[]>> FileSignatures = new()
        {
            // PDF
            { ".pdf", new List<byte[]>
                {
                    new byte[] { 0x25, 0x50, 0x44, 0x46 } // %PDF
                }
            },
            
            // Microsoft Office Legacy
            { ".doc", new List<byte[]>
                {
                    new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }, // OLE header
                    new byte[] { 0xDB, 0xA5, 0x2D, 0x00 }, // Word 2.0
                    new byte[] { 0x0D, 0x44, 0x4F, 0x43 }  // DOC
                }
            },
            { ".xls", new List<byte[]>
                {
                    new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }, // OLE header
                    new byte[] { 0x09, 0x08, 0x10, 0x00, 0x00, 0x06, 0x05, 0x00 }, // Excel 5.0/95
                    new byte[] { 0xFD, 0xFF, 0xFF, 0xFF } // Excel 2.0
                }
            },
            { ".ppt", new List<byte[]>
                {
                    new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 } // OLE header
                }
            },
            
            // Microsoft Office Open XML (actually ZIP files)
            { ".docx", new List<byte[]>
                {
                    new byte[] { 0x50, 0x4B, 0x03, 0x04 }, // PK.. (ZIP)
                    new byte[] { 0x50, 0x4B, 0x05, 0x06 }, // PK.. (empty archive)
                    new byte[] { 0x50, 0x4B, 0x07, 0x08 }  // PK.. (spanned archive)
                }
            },
            { ".xlsx", new List<byte[]>
                {
                    new byte[] { 0x50, 0x4B, 0x03, 0x04 },
                    new byte[] { 0x50, 0x4B, 0x05, 0x06 },
                    new byte[] { 0x50, 0x4B, 0x07, 0x08 }
                }
            },
            { ".pptx", new List<byte[]>
                {
                    new byte[] { 0x50, 0x4B, 0x03, 0x04 },
                    new byte[] { 0x50, 0x4B, 0x05, 0x06 },
                    new byte[] { 0x50, 0x4B, 0x07, 0x08 }
                }
            },
            
            // ZIP archives
            { ".zip", new List<byte[]>
                {
                    new byte[] { 0x50, 0x4B, 0x03, 0x04 },
                    new byte[] { 0x50, 0x4B, 0x05, 0x06 },
                    new byte[] { 0x50, 0x4B, 0x07, 0x08 },
                    new byte[] { 0x50, 0x4B, 0x4C, 0x49, 0x54, 0x45 }, // PKLITE
                    new byte[] { 0x50, 0x4B, 0x53, 0x70, 0x58 } // PKSFX
                }
            },
            
            // Text files (no specific signature, but we can check for BOM)
            { ".txt", new List<byte[]>
                {
                    new byte[] { 0xEF, 0xBB, 0xBF },       // UTF-8 BOM
                    new byte[] { 0xFF, 0xFE },             // UTF-16 LE BOM
                    new byte[] { 0xFE, 0xFF },             // UTF-16 BE BOM
                    new byte[] { 0xFF, 0xFE, 0x00, 0x00 }, // UTF-32 LE BOM
                    new byte[] { 0x00, 0x00, 0xFE, 0xFF }  // UTF-32 BE BOM
                }
            },
            
            // CSV (same as text)
            { ".csv", new List<byte[]>
                {
                    new byte[] { 0xEF, 0xBB, 0xBF },
                    new byte[] { 0xFF, 0xFE },
                    new byte[] { 0xFE, 0xFF }
                }
            },
            
            // RTF
            { ".rtf", new List<byte[]>
                {
                    new byte[] { 0x7B, 0x5C, 0x72, 0x74, 0x66 } // {\rtf
                }
            }
        };

        /// <summary>
        /// Validates if a file matches its expected type based on magic bytes
        /// </summary>
        public static bool IsValid(string filePath, string? expectedExtension = null)
        {
            if (!File.Exists(filePath))
                return false;

            try
            {
                using var stream = File.OpenRead(filePath);
                return IsValid(stream, expectedExtension ?? Path.GetExtension(filePath));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Validates if file content matches expected type
        /// </summary>
        public static bool IsValid(byte[] fileBytes, string expectedExtension)
        {
            if (fileBytes == null || fileBytes.Length == 0)
                return false;

            // Normalize extension
            if (!expectedExtension.StartsWith("."))
                expectedExtension = "." + expectedExtension;

            expectedExtension = expectedExtension.ToLowerInvariant();

            // For text files, if no BOM, consider it valid if it's readable text
            if (expectedExtension == ".txt" || expectedExtension == ".csv")
            {
                if (IsLikelyTextFile(fileBytes))
                    return true;
            }

            // Check against known signatures
            if (!FileSignatures.ContainsKey(expectedExtension))
            {
                // Unknown type, do basic validation
                return fileBytes.Length > 0;
            }

            var signatures = FileSignatures[expectedExtension];
            foreach (var signature in signatures)
            {
                if (fileBytes.Length >= signature.Length &&
                    signature.SequenceEqual(fileBytes.Take(signature.Length)))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Validates if a stream contains valid file content
        /// </summary>
        public static bool IsValid(Stream stream, string expectedExtension)
        {
            if (stream == null || !stream.CanRead)
                return false;

            // Read enough bytes for signature detection
            var buffer = new byte[Math.Min(8192, stream.Length)];
            var bytesRead = stream.Read(buffer, 0, buffer.Length);

            // Reset stream position if possible
            if (stream.CanSeek)
                stream.Seek(0, SeekOrigin.Begin);

            if (bytesRead == 0)
                return false;

            Array.Resize(ref buffer, bytesRead);
            return IsValid(buffer, expectedExtension);
        }

        /// <summary>
        /// Detects file type from magic bytes
        /// </summary>
        public static string? DetectFileType(byte[] fileBytes)
        {
            if (fileBytes == null || fileBytes.Length == 0)
                return null;

            foreach (var kvp in FileSignatures)
            {
                foreach (var signature in kvp.Value)
                {
                    if (fileBytes.Length >= signature.Length &&
                        signature.SequenceEqual(fileBytes.Take(signature.Length)))
                    {
                        return kvp.Key;
                    }
                }
            }

            // Check if it's likely a text file
            if (IsLikelyTextFile(fileBytes))
                return ".txt";

            return null;
        }

        /// <summary>
        /// Detects file type from a file path
        /// </summary>
        public static string? DetectFileType(string filePath)
        {
            if (!File.Exists(filePath))
                return null;

            try
            {
                using var stream = File.OpenRead(filePath);
                var buffer = new byte[Math.Min(8192, stream.Length)];
                var bytesRead = stream.Read(buffer, 0, buffer.Length);

                if (bytesRead == 0)
                    return null;

                Array.Resize(ref buffer, bytesRead);
                return DetectFileType(buffer);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Validates specific file types
        /// </summary>
        public static bool IsValidPdf(string filePath) => IsValid(filePath, ".pdf");
        public static bool IsValidWord(string filePath) => IsValid(filePath, ".docx") || IsValid(filePath, ".doc");
        public static bool IsValidExcel(string filePath) => IsValid(filePath, ".xlsx") || IsValid(filePath, ".xls");
        public static bool IsValidPowerPoint(string filePath) => IsValid(filePath, ".pptx") || IsValid(filePath, ".ppt");
        public static bool IsValidZip(string filePath) => IsValid(filePath, ".zip");

        /// <summary>
        /// Checks if file content appears to be text
        /// </summary>
        private static bool IsLikelyTextFile(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return false;

            // Check first 1000 bytes for text patterns
            var sampleSize = Math.Min(1000, bytes.Length);
            var sample = bytes.Take(sampleSize).ToArray();

            // Count printable ASCII characters
            int printableCount = 0;
            int controlCount = 0;

            foreach (byte b in sample)
            {
                // Printable ASCII range (including space)
                if (b >= 0x20 && b <= 0x7E)
                    printableCount++;
                // Tab, newline, carriage return
                else if (b == 0x09 || b == 0x0A || b == 0x0D)
                    printableCount++;
                // NULL or other control characters
                else if (b == 0x00 || (b < 0x20 && b != 0x09 && b != 0x0A && b != 0x0D))
                    controlCount++;
            }

            // If more than 90% printable characters, likely text
            // If has NULL bytes or many control chars, likely binary
            return controlCount == 0 && (printableCount / (double)sampleSize) > 0.90;
        }

        /// <summary>
        /// Get file extension from MIME type
        /// </summary>
        public static string GetExtensionFromMimeType(string mimeType)
        {
            return mimeType?.ToLowerInvariant() switch
            {
                "application/pdf" => ".pdf",
                "application/msword" => ".doc",
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ".docx",
                "application/vnd.ms-excel" => ".xls",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ".xlsx",
                "application/vnd.ms-powerpoint" => ".ppt",
                "application/vnd.openxmlformats-officedocument.presentationml.presentation" => ".pptx",
                "text/plain" => ".txt",
                "text/csv" => ".csv",
                "application/rtf" => ".rtf",
                "application/zip" => ".zip",
                "application/x-zip-compressed" => ".zip",
                _ => ""
            };
        }

        /// <summary>
        /// Get MIME type from file extension
        /// </summary>
        public static string GetMimeTypeFromExtension(string extension)
        {
            if (!extension.StartsWith("."))
                extension = "." + extension;

            return extension.ToLowerInvariant() switch
            {
                ".pdf" => "application/pdf",
                ".doc" => "application/msword",
                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ".xls" => "application/vnd.ms-excel",
                ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                ".ppt" => "application/vnd.ms-powerpoint",
                ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
                ".txt" => "text/plain",
                ".csv" => "text/csv",
                ".rtf" => "application/rtf",
                ".zip" => "application/zip",
                _ => "application/octet-stream"
            };
        }
    }
}
