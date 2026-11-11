using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace VideoViewer.Services
{
    public static class AppPaths
    {
        public static readonly string Root = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoViewer");
        public static readonly string Thumbs = System.IO.Path.Combine(Root, "thumbs");
        public static readonly string SettingsFile = System.IO.Path.Combine(Root, "settings.json");
        public static readonly string LibraryFile = System.IO.Path.Combine(Root, "library.json");

        static AppPaths()
        {
            try { Directory.CreateDirectory(Thumbs); } catch { }
        }
    }

    [DataContract]
    public sealed class Settings
    {
        [DataMember] public List<string> Folders { get; set; } = new List<string>();
        [DataMember] public double CardWidth { get; set; } = 260;
        [DataMember] public string Sort { get; set; } = "Newest";
        [DataMember] public int Volume { get; set; } = 80;
        [DataMember] public bool Muted { get; set; }
        [DataMember] public bool Loop { get; set; }
        [DataMember] public double WindowWidth { get; set; }
        [DataMember] public double WindowHeight { get; set; }
        [DataMember] public bool WindowMaximized { get; set; }

        [OnDeserialized]
        void Fix(StreamingContext c)
        {
            if (Folders == null) Folders = new List<string>();
            if (string.IsNullOrEmpty(Sort)) Sort = "Newest";
            if (CardWidth < 150 || CardWidth > 520) CardWidth = 260;
        }
    }

    [DataContract]
    public sealed class IndexEntry
    {
        [DataMember(Name = "p")] public string Path { get; set; }
        [DataMember(Name = "s")] public long Size { get; set; }
        [DataMember(Name = "m")] public long ModifiedTicks { get; set; }
        [DataMember(Name = "c")] public long DateTicks { get; set; }
        [DataMember(Name = "d")] public long DurationMs { get; set; } = -1;
        [DataMember(Name = "w")] public int Width { get; set; }
        [DataMember(Name = "h")] public int Height { get; set; }
    }

    [DataContract]
    public sealed class LibraryIndex
    {
        [DataMember] public List<IndexEntry> Items { get; set; } = new List<IndexEntry>();
    }

    public static class Json
    {
        public static T Load<T>(string file) where T : class, new()
        {
            try
            {
                if (!File.Exists(file)) return new T();
                using (var fs = File.OpenRead(file))
                {
                    var ser = new DataContractJsonSerializer(typeof(T));
                    return (ser.ReadObject(fs) as T) ?? new T();
                }
            }
            catch { return new T(); }
        }

        public static void Save<T>(string file, T value)
        {
            try
            {
                var tmp = file + ".tmp";
                using (var fs = File.Create(tmp))
                {
                    var ser = new DataContractJsonSerializer(typeof(T));
                    ser.WriteObject(fs, value);
                }
                if (File.Exists(file)) File.Replace(tmp, file, null);
                else File.Move(tmp, file);
            }
            catch { }
        }
    }
}
