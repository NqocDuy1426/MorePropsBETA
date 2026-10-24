using System;
using System.IO;
using ColossalFramework.IO;
using UnityEngine;

namespace MorePropsBETA
{
    public static class Log
    {
        private const string Prefix = "[MorePropsBETA] ";
        private static readonly object s_lock = new object();
        private static string s_logFilePath;

        public static string LogFilePath
        {
            get
            {
                if (string.IsNullOrEmpty(s_logFilePath))
                {
                    try
                    {
                        string dir = DataLocation.localApplicationData;
                        if (!string.IsNullOrEmpty(dir))
                        {
                            s_logFilePath = Path.Combine(dir, "MorePropsBETA.log");
                        }
                    }
                    catch
                    {
                        s_logFilePath = "MorePropsBETA.log";
                    }
                }
                return s_logFilePath;
            }
        }

        public static void Info(string text)
        {
            Debug.Log(Prefix + text);
            WriteToFile("INFO", text);
        }

        public static void Warning(string text)
        {
            Debug.LogWarning(Prefix + text);
            WriteToFile("WARN", text);
        }

        public static void Error(string text)
        {
            Debug.LogError(Prefix + text);
            WriteToFile("ERROR", text);
        }

        public static void Exception(string context, Exception ex)
        {
            if (ex == null) return;
            string message = $"{context} caught exception: {ex.GetType().Name} - {ex.Message}\nStack trace:\n{ex.StackTrace}";
            Debug.LogError(Prefix + message);
            WriteToFile("EXCEPTION", message);
        }

        private static void WriteToFile(string level, string message)
        {
            try
            {
                string path = LogFilePath;
                if (string.IsNullOrEmpty(path)) return;

                lock (s_lock)
                {
                    string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    string line = $"[{timestamp}] [{level}] {message}{Environment.NewLine}";
                    File.AppendAllText(path, line);
                }
            }
            catch
            {
                // Đảm bảo không bao giờ ném lỗi nếu quá trình ghi file tạm thời bị khóa
            }
        }
    }
}
