using ManagedBass;
using ManagedBass.Fx;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace BassPlayerSharp.Manager
{
    public class BassManager
    {
        private static bool _isInitialized = false;

        public static void Initialize()
        {
            if (_isInitialized) return;

            if (!Bass.Init())
            {
                Console.WriteLine($"Bass初始化失败: {Bass.LastError}");
                return;
            }
            _isInitialized = true;
            LoadBassPlugins();
        }

        private static void LoadBassPlugins()
        {
            var appPath = AppContext.BaseDirectory;
            var pluginPaths = new[]
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
            var version = BassFx.Version;
            Console.WriteLine($"BassFx: {version}");
            foreach (var pluginPath in pluginPaths)
            {
                var fullPath = Path.Combine(appPath, pluginPath);
                if (!File.Exists(fullPath))
                {
                    Console.WriteLine($"插件文件不存在: {fullPath}");
                    continue;
                }

                var pluginHandle = Bass.PluginLoad(fullPath);
                if (pluginHandle != 0)
                {
                    Console.WriteLine($"成功加载插件: {pluginPath}，句柄: {pluginHandle}");
                }
                else
                {
                    Console.WriteLine($"加载插件失败: {pluginPath}，错误: {Bass.LastError}");
                }
                var plugins = Bass.PluginGetInfo(pluginHandle);

                foreach (var plugin in plugins.Formats)
                {
                    Console.WriteLine($"  支持格式: {plugin.Name} ({plugin.FileExtensions})");
                }
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
