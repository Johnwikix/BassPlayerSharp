using ManagedBass;
using ManagedBass.Fx;


namespace BassPlayerSharp.Manager
{
    public class BassManager
    {
        private static bool _isInitialized = false;

        private static readonly string[] PluginPaths = new[]
        {
            "bassape.dll",
            "basscd.dll",
            "bassdsd.dll",
            "bassflac.dll",
            "basshls.dll",
            "bassmidi.dll",
            "bassopus.dll",
            "basswebm.dll",
            "basswv.dll",
            "bassalac.dll"
        };

        private static readonly string AppPath = AppContext.BaseDirectory;

        private static readonly char[] PathBuffer = new char[260]; // MAX_PATH

        public static void Initialize()
        {
            if (_isInitialized) return;

            if (!Bass.Init())
            {
                return;
            }

            _isInitialized = true;
            LoadBassPlugins();
        }

        private static void LoadBassPlugins()
        {
            _ = BassFx.Version;
            var appPathLength = AppPath.Length;

            for (int i = 0; i < PluginPaths.Length; i++)
            {
                var pluginPath = PluginPaths[i];

                var fullPathLength = appPathLength + 1 + pluginPath.Length;

                if (fullPathLength >= PathBuffer.Length)
                    continue;

                AppPath.AsSpan().CopyTo(PathBuffer);
                PathBuffer[appPathLength] = Path.DirectorySeparatorChar;
                pluginPath.AsSpan().CopyTo(PathBuffer.AsSpan(appPathLength + 1));

                var fullPathSpan = PathBuffer.AsSpan(0, fullPathLength);

                if (!File.Exists(fullPathSpan.ToString()))
                {
                    continue;
                }
                var fullPath = new string(fullPathSpan);
                var pluginHandle = Bass.PluginLoad(fullPath);
            }
        }

        public static void Free()
        {
            if (!_isInitialized) return;

            Bass.Free();
            _isInitialized = false;
        }
    }
}