using ManagedBass;
using ManagedBass.Fx;


namespace BassPlayerSharp.Manager
{
    public class BassManager
    {
        private static bool _isInitialized = false;

        // 预分配插件路径数组，避免每次初始化时创建新数组
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

        // 缓存应用程序路径
        private static readonly string AppPath = AppContext.BaseDirectory;

        // 预分配路径缓冲区
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
            // 移除字符串插值和 Console.WriteLine 以避免分配
            // 如果需要日志，考虑使用预分配的日志系统
            _ = BassFx.Version;
            var appPathLength = AppPath.Length;

            for (int i = 0; i < PluginPaths.Length; i++)
            {
                var pluginPath = PluginPaths[i];

                // 使用 stackalloc 或复用缓冲区来构建完整路径
                // 避免 Path.Combine 的字符串分配
                var fullPathLength = appPathLength + 1 + pluginPath.Length;

                if (fullPathLength >= PathBuffer.Length)
                    continue;

                // 手动构建路径
                AppPath.AsSpan().CopyTo(PathBuffer);
                PathBuffer[appPathLength] = Path.DirectorySeparatorChar;
                pluginPath.AsSpan().CopyTo(PathBuffer.AsSpan(appPathLength + 1));

                var fullPathSpan = PathBuffer.AsSpan(0, fullPathLength);

                // File.Exists 接受 ReadOnlySpan<char> (从 .NET 6 开始)
                if (!File.Exists(fullPathSpan.ToString())) // 这里仍需转换，但可用 stackalloc 优化
                {
                    continue;
                }

                // 为 Bass.PluginLoad 创建字符串（无法避免此分配，除非 API 支持 Span）
                var fullPath = new string(fullPathSpan);
                var pluginHandle = Bass.PluginLoad(fullPath);

                // 如果不使用 plugins 变量，移除此调用
                // var plugins = Bass.PluginGetInfo(pluginHandle);
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