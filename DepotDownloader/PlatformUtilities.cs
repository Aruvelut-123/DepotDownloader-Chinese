// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DepotDownloader
{
    static class PlatformUtilities
    {
        public static void SetExecutable(string path, bool value)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return;
            }

            const UnixFileMode ModeExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

            var mode = File.GetUnixFileMode(path);
            var hasExecuteMask = (mode & ModeExecute) == ModeExecute;
            if (hasExecuteMask != value)
            {
                File.SetUnixFileMode(path, value
                    ? mode | ModeExecute
                    : mode & ~ModeExecute);
            }
        }

        [SupportedOSPlatform("windows5.0")]
        public static void VerifyConsoleLaunch()
        {
            // Reference: https://devblogs.microsoft.com/oldnewthing/20160125-00/?p=92922
            var processList = new uint[2];
            var processCount = Windows.Win32.PInvoke.GetConsoleProcessList(processList);

            if (processCount != 1)
            {
                return;
            }

            _ = Windows.Win32.PInvoke.MessageBox(
                Windows.Win32.Foundation.HWND.Null,
                "Depot 下载器是一个控制台程序; 没有对应的GUI。\n\n如果你不传递任何命令行参数，它就会输出使用方法。\n\n你必须在控制台或终端内使用此工具。",
                "Depot 下载器",
                Windows.Win32.UI.WindowsAndMessaging.MESSAGEBOX_STYLE.MB_OK | Windows.Win32.UI.WindowsAndMessaging.MESSAGEBOX_STYLE.MB_ICONWARNING
            );
        }
    }
}
