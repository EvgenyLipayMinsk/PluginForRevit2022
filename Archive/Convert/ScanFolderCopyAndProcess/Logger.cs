using System;
using System.Collections.Generic;
using System.Text;
using System.IO;


namespace ScanFolderCopyAndProcess
{
    abstract public class Logger
    {
        public bool Info { get; set; } = true;
        public bool Warning { get; set; } = true;
        public bool Error { get; set; } = true;
        public bool Debug { get; set; } = true;

        protected List<Logger> loggers = new List<Logger>(); // chained loggers 

        virtual public void Log(string s) { } // main log function, depends on log mechanism (console, buffer, file) 

        public void LogInfo(string s)
        {
            if (Info == true) this.Log($"INFO {s}");
            foreach (var log in this.loggers) log.LogInfo(s);
        }
        public void LogWarning(string s)
        {
            if (Warning == true) this.Log($"WARNING {s}");
            foreach (var log in this.loggers) log.LogWarning(s);
        }
        public void LogError(string s)
        {
            if (Error == true) this.Log($"ERROR {s}");
            foreach (var log in this.loggers) log.LogError(s);
        }
        public void LogDebug(string s)
        {
            if (Debug == true) this.Log($"DEBUG {s}");
            foreach (var log in this.loggers) log.LogDebug(s);
        }

        public bool ChainLogger(Logger log) 
        {
            if (loggers.Contains(log) == false) { loggers.Add(log); return true; }
            else { return false; }
        }
        public bool RemoveLoger(Logger log)
        {
            if (loggers.Contains(log) == true) { loggers.Remove(log); return true; }
            else { return false; }
        }
    }


    public class ConsoleLogger : Logger
    { 
        override public void Log(string s) 
        { 
            Console.WriteLine(s);
        }
    }

    public class StringBuilderLogger : Logger
    {
        private StringBuilder sb = new StringBuilder();

        override public void Log(string s) 
        {
            sb.Append(s + "\n");
        }

        public override string ToString()
        {
            return this.sb.ToString();
        }
    }


    public class FileLogger : Logger
    {
        private string output_path;
        public FileLogger(string path, bool delete_existing)
        { 
            output_path= path;
            if (delete_existing == true && File.Exists(path)) 
            { 
                File.Delete(path);
            }
        }

        override public void Log(string s)
        {
            var writer = File.AppendText(output_path);
            writer.WriteLine(s); 
            writer.Close();
        }

    }


}
