// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SteamKit2;

namespace DepotDownloader
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            if (args.Length == 0)
            {
                PrintVersion();
                PrintUsage();

                if (OperatingSystem.IsWindowsVersionAtLeast(5, 0))
                {
                    PlatformUtilities.VerifyConsoleLaunch();
                }

                return 0;
            }

            Ansi.Init();

            DebugLog.Enabled = false;

            AccountSettingsStore.LoadFromFile("account.config");

            #region Common Options

            // Not using HasParameter because it is case insensitive
            if (args.Length == 1 && (args[0] == "-V" || args[0] == "--version"))
            {
                PrintVersion(true);
                return 0;
            }

            if (HasParameter(args, "-debug"))
            {
                PrintVersion(true);

                DebugLog.Enabled = true;
                DebugLog.AddListener((category, message) =>
                {
                    Console.WriteLine("[{0}] {1}", category, message);
                });

                var httpEventListener = new HttpDiagnosticEventListener();
            }

            var username = GetParameter<string>(args, "-username") ?? GetParameter<string>(args, "-user");
            var password = GetParameter<string>(args, "-password") ?? GetParameter<string>(args, "-pass");
            ContentDownloader.Config.RememberPassword = HasParameter(args, "-remember-password");
            ContentDownloader.Config.UseQrCode = HasParameter(args, "-qr");

            ContentDownloader.Config.DownloadManifestOnly = HasParameter(args, "-manifest-only");

            var cellId = GetParameter(args, "-cellid", -1);
            if (cellId == -1)
            {
                cellId = 0;
            }

            ContentDownloader.Config.CellID = cellId;

            var fileList = GetParameter<string>(args, "-filelist");

            if (fileList != null)
            {
                const string RegexPrefix = "regex:";

                try
                {
                    ContentDownloader.Config.UsingFileList = true;
                    ContentDownloader.Config.FilesToDownload = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    ContentDownloader.Config.FilesToDownloadRegex = [];

                    var files = await File.ReadAllLinesAsync(fileList);

                    foreach (var fileEntry in files)
                    {
                        if (string.IsNullOrWhiteSpace(fileEntry))
                        {
                            continue;
                        }

                        if (fileEntry.StartsWith(RegexPrefix))
                        {
                            var rgx = new Regex(fileEntry[RegexPrefix.Length..], RegexOptions.Compiled | RegexOptions.IgnoreCase);
                            ContentDownloader.Config.FilesToDownloadRegex.Add(rgx);
                        }
                        else
                        {
                            ContentDownloader.Config.FilesToDownload.Add(fileEntry.Replace('\\', '/'));
                        }
                    }

                    Console.WriteLine("使用文件列表: '{0}'.", fileList);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("警告: 无法读取文件列表: {0}", ex);
                }
            }

            ContentDownloader.Config.InstallDirectory = GetParameter<string>(args, "-dir");

            ContentDownloader.Config.VerifyAll = HasParameter(args, "-verify-all") || HasParameter(args, "-verify_all") || HasParameter(args, "-validate");
            ContentDownloader.Config.MaxServers = GetParameter(args, "-max-servers", 20);
            ContentDownloader.Config.MaxDownloads = GetParameter(args, "-max-downloads", 8);
            ContentDownloader.Config.MaxServers = Math.Max(ContentDownloader.Config.MaxServers, ContentDownloader.Config.MaxDownloads);
            ContentDownloader.Config.LoginID = HasParameter(args, "-loginid") ? GetParameter<uint>(args, "-loginid") : null;

            #endregion

            var appId = GetParameter(args, "-app", ContentDownloader.INVALID_APP_ID);
            if (appId == ContentDownloader.INVALID_APP_ID)
            {
                Console.WriteLine("错误: -app 未指定！");
                return 1;
            }

            var pubFile = GetParameter(args, "-pubfile", ContentDownloader.INVALID_MANIFEST_ID);
            var ugcId = GetParameter(args, "-ugc", ContentDownloader.INVALID_MANIFEST_ID);
            if (pubFile != ContentDownloader.INVALID_MANIFEST_ID)
            {
                #region Pubfile Downloading

                if (InitializeSteam(username, password))
                {
                    try
                    {
                        await ContentDownloader.DownloadPubfileAsync(appId, pubFile).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (
                        ex is ContentDownloaderException
                        || ex is OperationCanceledException)
                    {
                        Console.WriteLine(ex.Message);
                        return 1;
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("由于未处理的异常而下载失败: {0}", e.Message);
                        throw;
                    }
                    finally
                    {
                        ContentDownloader.ShutdownSteam3();
                    }
                }
                else
                {
                    Console.WriteLine("错误: Steam 初始化失败");
                    return 1;
                }

                #endregion
            }
            else if (ugcId != ContentDownloader.INVALID_MANIFEST_ID)
            {
                #region UGC Downloading

                if (InitializeSteam(username, password))
                {
                    try
                    {
                        await ContentDownloader.DownloadUGCAsync(appId, ugcId).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (
                        ex is ContentDownloaderException
                        || ex is OperationCanceledException)
                    {
                        Console.WriteLine(ex.Message);
                        return 1;
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("由于未处理的异常而下载失败: {0}", e.Message);
                        throw;
                    }
                    finally
                    {
                        ContentDownloader.ShutdownSteam3();
                    }
                }
                else
                {
                    Console.WriteLine("错误: Steam 初始化失败");
                    return 1;
                }

                #endregion
            }
            else
            {
                #region App downloading

                var branch = GetParameter<string>(args, "-branch") ?? GetParameter<string>(args, "-beta") ?? ContentDownloader.DEFAULT_BRANCH;
                ContentDownloader.Config.BetaPassword = GetParameter<string>(args, "-betapassword");

                ContentDownloader.Config.DownloadAllPlatforms = HasParameter(args, "-all-platforms");
                var os = GetParameter<string>(args, "-os");

                if (ContentDownloader.Config.DownloadAllPlatforms && !string.IsNullOrEmpty(os))
                {
                    Console.WriteLine("错误: 当指定了 -all-platforms 时不能指定 -os。");
                    return 1;
                }

                var arch = GetParameter<string>(args, "-osarch");

                ContentDownloader.Config.DownloadAllLanguages = HasParameter(args, "-all-languages");
                var language = GetParameter<string>(args, "-language");

                if (ContentDownloader.Config.DownloadAllLanguages && !string.IsNullOrEmpty(language))
                {
                    Console.WriteLine("错误: 当指定了 -all-languages 时不能指定 -language。");
                    return 1;
                }

                var lv = HasParameter(args, "-lowviolence");

                var depotManifestIds = new List<(uint, ulong)>();
                var isUGC = false;

                var depotIdList = GetParameterList<uint>(args, "-depot");
                var manifestIdList = GetParameterList<ulong>(args, "-manifest");
                if (manifestIdList.Count > 0)
                {
                    if (depotIdList.Count != manifestIdList.Count)
                    {
                        Console.WriteLine("错误: -manifest 需要一个给每个 -depot 定义的 ID");
                        return 1;
                    }

                    var zippedDepotManifest = depotIdList.Zip(manifestIdList, (depotId, manifestId) => (depotId, manifestId));
                    depotManifestIds.AddRange(zippedDepotManifest);
                }
                else
                {
                    depotManifestIds.AddRange(depotIdList.Select(depotId => (depotId, ContentDownloader.INVALID_MANIFEST_ID)));
                }

                if (InitializeSteam(username, password))
                {
                    try
                    {
                        await ContentDownloader.DownloadAppAsync(appId, depotManifestIds, branch, os, arch, language, lv, isUGC).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (
                        ex is ContentDownloaderException
                        || ex is OperationCanceledException)
                    {
                        Console.WriteLine(ex.Message);
                        return 1;
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("由于未处理的异常而下载失败: {0}", e.Message);
                        throw;
                    }
                    finally
                    {
                        ContentDownloader.ShutdownSteam3();
                    }
                }
                else
                {
                    Console.WriteLine("错误: Steam 初始化失败");
                    return 1;
                }

                #endregion
            }

            return 0;
        }

        static bool InitializeSteam(string username, string password)
        {
            if (!ContentDownloader.Config.UseQrCode)
            {
                if (username != null && password == null && (!ContentDownloader.Config.RememberPassword || !AccountSettingsStore.Instance.LoginTokens.ContainsKey(username)))
                {
                    do
                    {
                        Console.Write("请输入账号 \"{0}\" 的密码: ", username);
                        if (Console.IsInputRedirected)
                        {
                            password = Console.ReadLine();
                        }
                        else
                        {
                            // Avoid console echoing of password
                            password = Util.ReadPassword();
                        }

                        Console.WriteLine();
                    } while (string.Empty == password);
                }
                else if (username == null)
                {
                    Console.WriteLine("未给予用户名。使用匿名账户和专用服务器订阅。");
                }
            }

            return ContentDownloader.InitializeSteam3(username, password);
        }

        static int IndexOfParam(string[] args, string param)
        {
            for (var x = 0; x < args.Length; ++x)
            {
                if (args[x].Equals(param, StringComparison.OrdinalIgnoreCase))
                    return x;
            }

            return -1;
        }

        static bool HasParameter(string[] args, string param)
        {
            return IndexOfParam(args, param) > -1;
        }

        static T GetParameter<T>(string[] args, string param, T defaultValue = default)
        {
            var index = IndexOfParam(args, param);

            if (index == -1 || index == (args.Length - 1))
                return defaultValue;

            var strParam = args[index + 1];

            var converter = TypeDescriptor.GetConverter(typeof(T));
            if (converter != null)
            {
                return (T)converter.ConvertFromString(strParam);
            }

            return default;
        }

        static List<T> GetParameterList<T>(string[] args, string param)
        {
            var list = new List<T>();
            var index = IndexOfParam(args, param);

            if (index == -1 || index == (args.Length - 1))
                return list;

            index++;

            while (index < args.Length)
            {
                var strParam = args[index];

                if (strParam[0] == '-') break;

                var converter = TypeDescriptor.GetConverter(typeof(T));
                if (converter != null)
                {
                    list.Add((T)converter.ConvertFromString(strParam));
                }

                index++;
            }

            return list;
        }

        static void PrintUsage()
        {
            // Do not use tabs to align parameters here because tab size may differ
            Console.WriteLine();
            Console.WriteLine("用法: 下载一个应用的一个或所有 depot:");
            Console.WriteLine("       depotdownloader -app <id> [-depot <id> [-manifest <id>]]");
            Console.WriteLine("                       [-username <用户名> [-password <密码>]] [其他选项]");
            Console.WriteLine();
            Console.WriteLine("用法: 使用 pubfile id 下载一个创意工坊物品");
            Console.WriteLine("       depotdownloader -app <id> -pubfile <id> [-username <用户名> [-password <密码>]]");
            Console.WriteLine("用法: 使用 ugc id 下载一个创意工坊物品");
            Console.WriteLine("       depotdownloader -app <id> -ugc <id> [-username <用户名> [-password <密码>]]");
            Console.WriteLine();
            Console.WriteLine("参数:");
            Console.WriteLine("  -app <#>                 - 想要下载的应用的 AppID。");
            Console.WriteLine("  -depot <#>               - 想要下载的应用的 DepotID。");
            Console.WriteLine("  -manifest <id>           - 想要下载的内容的 manifest id (需要 -depot, 默认: 目前分支)。");
            Console.WriteLine("  -beta <分支名>           - 如果可用则从特定分支下载 (默认: 公共).");
            Console.WriteLine("  -betapassword <密码>     - 如果需要则填写的分支密码。");
            Console.WriteLine("  -all-platforms           - 当使用 -app 参数时下载所有的系统特定 depot。");
            Console.WriteLine("  -os <操作系统>            - 下载游戏对应的操作系统的版本 (windows, macos 或 linux, 默认: 此程序所运行在的操作系统)");
            Console.WriteLine("  -osarch <架构>           - 下载游戏对应的架构的版本 (32 或 64, 默认: 主机的架构)");
            Console.WriteLine("  -all-languages           - 当使用 -app 参数时下载所有的语言特定 depot。");
            Console.WriteLine("  -language <语言>         - 下载游戏对应的语言的版本 (默认: 英语)");
            Console.WriteLine("  -lowviolence             - 当使用 -app 参数时下载低暴力 depot。");
            Console.WriteLine();
            Console.WriteLine("  -ugc <#>                 - 想要下载的文件的 UGC ID。");
            Console.WriteLine("  -pubfile <#>             - 想要下载的文件的 PublishedFileId。 (会自动解析为 UGC id)");
            Console.WriteLine();
            Console.WriteLine("  -username <用户名>         - 要登录以访问受限制内容的帐户的用户名。");
            Console.WriteLine("  -password <密码>         - 要登录以访问受限制内容的帐户的密码。");
            Console.WriteLine("  -remember-password       - 如果设置，将记住此用户的后续登录密码。 （使用 `-username <用户名> -remember-password` 作为登录凭据）");
            Console.WriteLine();
            Console.WriteLine("  -dir <安装目录>        - 要放置下载文件的目录。");
            Console.WriteLine("  -filelist <文件.txt>     - 要下载的文件列表（来自清单）。如果要以正则表达式进行匹配，请在文件路径前加上 `regex:` 前缀。");
            Console.WriteLine("  -validate                - 包含已下载文件的校验和验证");
            Console.WriteLine();
            Console.WriteLine("  -manifest-only           - 下载任何要下载的仓库的人类可读清单。");
            Console.WriteLine("  -cellid <#>              - 要从中下载内容的覆盖 CellID 的服务器。");
            Console.WriteLine("  -max-servers <#>         - 要使用的最大内容服务器数量。 （默认：20）。");
            Console.WriteLine("  -max-downloads <#>       - 同时下载的最大块数。 （默认：8）。");
            Console.WriteLine("  -loginid <#>             - 一个唯一的32位十进制Steam LogonID，如果同时运行多个DepotDownloader实例，则必需。");
        }

        static void PrintVersion(bool printExtra = false)
        {
            var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion;
            Console.WriteLine($"DepotDownloader v{version}");

            if (!printExtra)
            {
                return;
            }

            Console.WriteLine($"运行库: {RuntimeInformation.FrameworkDescription} 运行在 {RuntimeInformation.OSDescription} 系统上");
        }
    }
}
