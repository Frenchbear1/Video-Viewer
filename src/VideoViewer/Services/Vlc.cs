using System;
using System.Threading.Tasks;
using LibVLCSharp.Shared;

namespace VideoViewer.Services
{
    /// <summary>
    /// One shared LibVLC engine, warmed up in the background at startup so the first
    /// click on a video plays instantly.
    /// </summary>
    public static class Vlc
    {
        static readonly Lazy<Task<LibVLC>> _engine = new Lazy<Task<LibVLC>>(() => Task.Run(() =>
        {
            Core.Initialize();
            return new LibVLC(
                "--no-video-title-show",
                "--no-snapshot-preview",
                "--no-stats",
                "--quiet");
        }));

        public static Task<LibVLC> Engine => _engine.Value;

        public static void WarmUp() { var _ = Engine; }

        public static LibVLC Get() => Engine.GetAwaiter().GetResult();
    }
}
