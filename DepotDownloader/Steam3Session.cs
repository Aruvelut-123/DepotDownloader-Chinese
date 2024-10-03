// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QRCoder;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.CDN;
using SteamKit2.Internal;

namespace DepotDownloader
{
    class Steam3Session
    {
        public bool IsLoggedOn { get; private set; }

        public ReadOnlyCollection<SteamApps.LicenseListCallback.License> Licenses
        {
            get;
            private set;
        }

        public Dictionary<uint, ulong> AppTokens { get; } = [];
        public Dictionary<uint, ulong> PackageTokens { get; } = [];
        public Dictionary<uint, byte[]> DepotKeys { get; } = [];
        public ConcurrentDictionary<(uint, string), TaskCompletionSource<SteamApps.CDNAuthTokenCallback>> CDNAuthTokens { get; } = [];
        public Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo> AppInfo { get; } = [];
        public Dictionary<uint, SteamApps.PICSProductInfoCallback.PICSProductInfo> PackageInfo { get; } = [];
        public Dictionary<string, byte[]> AppBetaPasswords { get; } = [];

        public SteamClient steamClient;
        public SteamUser steamUser;
        public SteamContent steamContent;
        readonly SteamApps steamApps;
        readonly SteamCloud steamCloud;
        readonly SteamUnifiedMessages.UnifiedService<IPublishedFile> steamPublishedFile;

        readonly CallbackManager callbacks;

        readonly bool authenticatedUser;
        bool bConnected;
        bool bConnecting;
        bool bAborted;
        bool bExpectingDisconnectRemote;
        bool bDidDisconnect;
        bool bIsConnectionRecovery;
        int connectionBackoff;
        int seq; // more hack fixes
        DateTime connectTime;
        AuthSession authSession;

        // input
        readonly SteamUser.LogOnDetails logonDetails;

        static readonly TimeSpan STEAM3_TIMEOUT = TimeSpan.FromSeconds(30);


        public Steam3Session(SteamUser.LogOnDetails details)
        {
            this.logonDetails = details;
            this.authenticatedUser = details.Username != null || ContentDownloader.Config.UseQrCode;

            var clientConfiguration = SteamConfiguration.Create(config =>
                config
                    .WithHttpClientFactory(HttpClientFactory.CreateHttpClient)
            );

            this.steamClient = new SteamClient(clientConfiguration);

            this.steamUser = this.steamClient.GetHandler<SteamUser>();
            this.steamApps = this.steamClient.GetHandler<SteamApps>();
            this.steamCloud = this.steamClient.GetHandler<SteamCloud>();
            var steamUnifiedMessages = this.steamClient.GetHandler<SteamUnifiedMessages>();
            this.steamPublishedFile = steamUnifiedMessages.CreateService<IPublishedFile>();
            this.steamContent = this.steamClient.GetHandler<SteamContent>();

            this.callbacks = new CallbackManager(this.steamClient);

            this.callbacks.Subscribe<SteamClient.ConnectedCallback>(ConnectedCallback);
            this.callbacks.Subscribe<SteamClient.DisconnectedCallback>(DisconnectedCallback);
            this.callbacks.Subscribe<SteamUser.LoggedOnCallback>(LogOnCallback);
            this.callbacks.Subscribe<SteamApps.LicenseListCallback>(LicenseListCallback);

            Console.Write("正在连接到 Steam3...");
            Connect();
        }

        public delegate bool WaitCondition();

        private readonly object steamLock = new();

        public bool WaitUntilCallback(Action submitter, WaitCondition waiter)
        {
            while (!bAborted && !waiter())
            {
                lock (steamLock)
                {
                    submitter();
                }

                var seq = this.seq;
                do
                {
                    lock (steamLock)
                    {
                        WaitForCallbacks();
                    }
                } while (!bAborted && this.seq == seq && !waiter());
            }

            return bAborted;
        }

        public bool WaitForCredentials()
        {
            if (IsLoggedOn || bAborted)
                return IsLoggedOn;

            WaitUntilCallback(() => { }, () => IsLoggedOn);

            return IsLoggedOn;
        }

        public void RequestAppInfo(uint appId, bool bForce = false)
        {
            if ((AppInfo.ContainsKey(appId) && !bForce) || bAborted)
                return;

            var completed = false;
            Action<SteamApps.PICSTokensCallback> cbMethodTokens = appTokens =>
            {
                completed = true;
                if (appTokens.AppTokensDenied.Contains(appId))
                {
                    Console.WriteLine("权限不足，无法获取应用 {0} 的访问令牌", appId);
                }

                foreach (var token_dict in appTokens.AppTokens)
                {
                    this.AppTokens[token_dict.Key] = token_dict.Value;
                }
            };

            WaitUntilCallback(() =>
            {
                callbacks.Subscribe(steamApps.PICSGetAccessTokens(new List<uint> { appId }, new List<uint>()), cbMethodTokens);
            }, () => { return completed; });

            completed = false;
            Action<SteamApps.PICSProductInfoCallback> cbMethod = appInfo =>
            {
                completed = !appInfo.ResponsePending;

                foreach (var app_value in appInfo.Apps)
                {
                    var app = app_value.Value;

                    Console.WriteLine("获取到应用 {0} 的信息", app.ID);
                    AppInfo[app.ID] = app;
                }

                foreach (var app in appInfo.UnknownApps)
                {
                    AppInfo[app] = null;
                }
            };

            var request = new SteamApps.PICSRequest(appId);
            if (AppTokens.TryGetValue(appId, out var token))
            {
                request.AccessToken = token;
            }

            WaitUntilCallback(() =>
            {
                callbacks.Subscribe(steamApps.PICSGetProductInfo(new List<SteamApps.PICSRequest> { request }, new List<SteamApps.PICSRequest>()), cbMethod);
            }, () => { return completed; });
        }

        public void RequestPackageInfo(IEnumerable<uint> packageIds)
        {
            var packages = packageIds.ToList();
            packages.RemoveAll(pid => PackageInfo.ContainsKey(pid));

            if (packages.Count == 0 || bAborted)
                return;

            var completed = false;
            Action<SteamApps.PICSProductInfoCallback> cbMethod = packageInfo =>
            {
                completed = !packageInfo.ResponsePending;

                foreach (var package_value in packageInfo.Packages)
                {
                    var package = package_value.Value;
                    PackageInfo[package.ID] = package;
                }

                foreach (var package in packageInfo.UnknownPackages)
                {
                    PackageInfo[package] = null;
                }
            };

            var packageRequests = new List<SteamApps.PICSRequest>();

            foreach (var package in packages)
            {
                var request = new SteamApps.PICSRequest(package);

                if (PackageTokens.TryGetValue(package, out var token))
                {
                    request.AccessToken = token;
                }

                packageRequests.Add(request);
            }

            WaitUntilCallback(() =>
            {
                callbacks.Subscribe(steamApps.PICSGetProductInfo(new List<SteamApps.PICSRequest>(), packageRequests), cbMethod);
            }, () => { return completed; });
        }

        public bool RequestFreeAppLicense(uint appId)
        {
            var success = false;
            var completed = false;
            Action<SteamApps.FreeLicenseCallback> cbMethod = resultInfo =>
            {
                completed = true;
                success = resultInfo.GrantedApps.Contains(appId);
            };

            WaitUntilCallback(() =>
            {
                callbacks.Subscribe(steamApps.RequestFreeLicense(appId), cbMethod);
            }, () => { return completed; });

            return success;
        }

        public void RequestDepotKey(uint depotId, uint appid = 0)
        {
            if (DepotKeys.ContainsKey(depotId) || bAborted)
                return;

            var completed = false;

            Action<SteamApps.DepotKeyCallback> cbMethod = depotKey =>
            {
                completed = true;
                Console.WriteLine("获取到 depot {0} 的密钥，结果为: {1}", depotKey.DepotID, depotKey.Result);

                if (depotKey.Result != EResult.OK)
                {
                    Abort();
                    return;
                }

                DepotKeys[depotKey.DepotID] = depotKey.DepotKey;
            };

            WaitUntilCallback(() =>
            {
                callbacks.Subscribe(steamApps.GetDepotDecryptionKey(depotId, appid), cbMethod);
            }, () => { return completed; });
        }


        public async Task<ulong> GetDepotManifestRequestCodeAsync(uint depotId, uint appId, ulong manifestId, string branch)
        {
            if (bAborted)
                return 0;

            var requestCode = await steamContent.GetManifestRequestCode(depotId, appId, manifestId, branch);

            Console.WriteLine("获取到 {0} 的 manifest {1} 请求代码，结果为: {2}",
                depotId, manifestId,
                requestCode);

            return requestCode;
        }

        public async Task RequestCDNAuthToken(uint appid, uint depotid, Server server)
        {
            var cdnKey = (depotid, server.Host);
            var completion = new TaskCompletionSource<SteamApps.CDNAuthTokenCallback>();

            if (bAborted || !CDNAuthTokens.TryAdd(cdnKey, completion))
            {
                return;
            }

            DebugLog.WriteLine(nameof(Steam3Session), $"正在请求服务器 {server.Host} 的 CDN 验证密钥");

            var cdnAuth = await steamApps.GetCDNAuthToken(appid, depotid, server.Host);

            Console.WriteLine($"获取到服务器 {server.Host} 的 CDN 验证密钥，结果为: {cdnAuth.Result} (过期时间 {cdnAuth.Expiration})");

            if (cdnAuth.Result != EResult.OK)
            {
                return;
            }

            completion.TrySetResult(cdnAuth);
        }

        public void CheckAppBetaPassword(uint appid, string password)
        {
            var completed = false;
            Action<SteamApps.CheckAppBetaPasswordCallback> cbMethod = appPassword =>
            {
                completed = true;

                Console.WriteLine("获取到 {0} 测试密钥，结果为: {1}", appPassword.BetaPasswords.Count, appPassword.Result);

                foreach (var entry in appPassword.BetaPasswords)
                {
                    AppBetaPasswords[entry.Key] = entry.Value;
                }
            };

            WaitUntilCallback(() =>
            {
                callbacks.Subscribe(steamApps.CheckAppBetaPassword(appid, password), cbMethod);
            }, () => { return completed; });
        }

        public PublishedFileDetails GetPublishedFileDetails(uint appId, PublishedFileID pubFile)
        {
            var pubFileRequest = new CPublishedFile_GetDetails_Request { appid = appId };
            pubFileRequest.publishedfileids.Add(pubFile);

            var completed = false;
            PublishedFileDetails details = null;

            Action<SteamUnifiedMessages.ServiceMethodResponse> cbMethod = callback =>
            {
                completed = true;
                if (callback.Result == EResult.OK)
                {
                    var response = callback.GetDeserializedResponse<CPublishedFile_GetDetails_Response>();
                    details = response.publishedfiledetails.FirstOrDefault();
                }
                else
                {
                    throw new Exception($"获取 pubfile {pubFile} 的文件信息时出现错误 {(int)callback.Result} ({callback.Result})。");
                }
            };

            WaitUntilCallback(() =>
            {
                callbacks.Subscribe(steamPublishedFile.SendMessage(api => api.GetDetails(pubFileRequest)), cbMethod);
            }, () => { return completed; });

            return details;
        }


        public SteamCloud.UGCDetailsCallback GetUGCDetails(UGCHandle ugcHandle)
        {
            var completed = false;
            SteamCloud.UGCDetailsCallback details = null;

            Action<SteamCloud.UGCDetailsCallback> cbMethod = callback =>
            {
                completed = true;
                if (callback.Result == EResult.OK)
                {
                    details = callback;
                }
                else if (callback.Result == EResult.FileNotFound)
                {
                    details = null;
                }
                else
                {
                    throw new Exception($"获取 {ugcHandle} 的 UGC 详情时出现错误 {(int)callback.Result} ({callback.Result})。");
                }
            };

            WaitUntilCallback(() =>
            {
                callbacks.Subscribe(steamCloud.RequestUGCDetails(ugcHandle), cbMethod);
            }, () => { return completed; });

            return details;
        }

        private void ResetConnectionFlags()
        {
            bExpectingDisconnectRemote = false;
            bDidDisconnect = false;
            bIsConnectionRecovery = false;
        }

        void Connect()
        {
            bAborted = false;
            bConnected = false;
            bConnecting = true;
            connectionBackoff = 0;
            authSession = null;

            ResetConnectionFlags();

            this.connectTime = DateTime.Now;
            this.steamClient.Connect();
        }

        private void Abort(bool sendLogOff = true)
        {
            Disconnect(sendLogOff);
        }

        public void Disconnect(bool sendLogOff = true)
        {
            if (sendLogOff)
            {
                steamUser.LogOff();
            }

            bAborted = true;
            bConnected = false;
            bConnecting = false;
            bIsConnectionRecovery = false;
            steamClient.Disconnect();

            Ansi.Progress(Ansi.ProgressState.Hidden);

            // flush callbacks until our disconnected event
            while (!bDidDisconnect)
            {
                callbacks.RunWaitAllCallbacks(TimeSpan.FromMilliseconds(100));
            }
        }

        private void Reconnect()
        {
            bIsConnectionRecovery = true;
            steamClient.Disconnect();
        }

        private void WaitForCallbacks()
        {
            callbacks.RunWaitCallbacks(TimeSpan.FromSeconds(1));

            var diff = DateTime.Now - connectTime;

            if (diff > STEAM3_TIMEOUT && !bConnected)
            {
                Console.WriteLine("连接到 Steam3 超时。");
                Abort();
            }
        }

        private async void ConnectedCallback(SteamClient.ConnectedCallback connected)
        {
            Console.WriteLine(" 完成！");
            bConnecting = false;
            bConnected = true;

            // Update our tracking so that we don't time out, even if we need to reconnect multiple times,
            // e.g. if the authentication phase takes a while and therefore multiple connections.
            connectTime = DateTime.Now;
            connectionBackoff = 0;

            if (!authenticatedUser)
            {
                Console.Write("使用匿名账户登录到 Steam3 中...");
                steamUser.LogOnAnonymous();
            }
            else
            {
                if (logonDetails.Username != null)
                {
                    Console.WriteLine("使用账户 '{0}' 登录到 Steam3 中...", logonDetails.Username);
                }

                if (authSession is null)
                {
                    if (logonDetails.Username != null && logonDetails.Password != null && logonDetails.AccessToken is null)
                    {
                        try
                        {
                            _ = AccountSettingsStore.Instance.GuardData.TryGetValue(logonDetails.Username, out var guarddata);
                            authSession = await steamClient.Authentication.BeginAuthSessionViaCredentialsAsync(new SteamKit2.Authentication.AuthSessionDetails
                            {
                                Username = logonDetails.Username,
                                Password = logonDetails.Password,
                                IsPersistentSession = ContentDownloader.Config.RememberPassword,
                                GuardData = guarddata,
                                Authenticator = new UserConsoleAuthenticator(),
                            });
                        }
                        catch (TaskCanceledException)
                        {
                            return;
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine("无法和 Steam 进行验证: " + ex.Message);
                            Abort(false);
                            return;
                        }
                    }
                    else if (logonDetails.AccessToken is null && ContentDownloader.Config.UseQrCode)
                    {
                        Console.WriteLine("使用二维码登录中...");

                        try
                        {
                            var session = await steamClient.Authentication.BeginAuthSessionViaQRAsync(new AuthSessionDetails
                            {
                                IsPersistentSession = ContentDownloader.Config.RememberPassword,
                                Authenticator = new UserConsoleAuthenticator(),
                            });

                            authSession = session;

                            // Steam will periodically refresh the challenge url, so we need a new QR code.
                            session.ChallengeURLChanged = () =>
                            {
                                Console.WriteLine();
                                Console.WriteLine("二维码已变更，请扫描新的二维码:");

                                DisplayQrCode(session.ChallengeURL);
                            };

                            // Draw initial QR code immediately
                            DisplayQrCode(session.ChallengeURL);
                        }
                        catch (TaskCanceledException)
                        {
                            return;
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine("无法和 Steam 进行验证: " + ex.Message);
                            Abort(false);
                            return;
                        }
                    }
                }

                if (authSession != null)
                {
                    try
                    {
                        var result = await authSession.PollingWaitForResultAsync();

                        logonDetails.Username = result.AccountName;
                        logonDetails.Password = null;
                        logonDetails.AccessToken = result.RefreshToken;

                        if (result.NewGuardData != null)
                        {
                            AccountSettingsStore.Instance.GuardData[result.AccountName] = result.NewGuardData;
                        }
                        else
                        {
                            AccountSettingsStore.Instance.GuardData.Remove(result.AccountName);
                        }
                        AccountSettingsStore.Instance.LoginTokens[result.AccountName] = result.RefreshToken;
                        AccountSettingsStore.Save();
                    }
                    catch (TaskCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine("无法和 Steam 进行验证: " + ex.Message);
                        Abort(false);
                        return;
                    }

                    authSession = null;
                }

                steamUser.LogOn(logonDetails);
            }
        }

        private void DisconnectedCallback(SteamClient.DisconnectedCallback disconnected)
        {
            bDidDisconnect = true;

            DebugLog.WriteLine(nameof(Steam3Session), $"已断开连接: bIsConnectionRecovery = {bIsConnectionRecovery}, UserInitiated = {disconnected.UserInitiated}, bExpectingDisconnectRemote = {bExpectingDisconnectRemote}");

            // When recovering the connection, we want to reconnect even if the remote disconnects us
            if (!bIsConnectionRecovery && (disconnected.UserInitiated || bExpectingDisconnectRemote))
            {
                Console.WriteLine("已从 Steam 断开连接");

                // Any operations outstanding need to be aborted
                bAborted = true;
            }
            else if (connectionBackoff >= 10)
            {
                Console.WriteLine("10 次重试后仍无法连接到 Steam");
                Abort(false);
            }
            else if (!bAborted)
            {
                if (bConnecting)
                {
                    Console.WriteLine("无法连接到 Steam。请重试");
                }
                else
                {
                    Console.WriteLine("与 Steam 的连接已终端。重连中");
                }

                Thread.Sleep(1000 * ++connectionBackoff);

                // Any connection related flags need to be reset here to match the state after Connect
                ResetConnectionFlags();
                steamClient.Connect();
            }
        }

        private void LogOnCallback(SteamUser.LoggedOnCallback loggedOn)
        {
            var isSteamGuard = loggedOn.Result == EResult.AccountLogonDenied;
            var is2FA = loggedOn.Result == EResult.AccountLoginDeniedNeedTwoFactor;
            var isAccessToken = ContentDownloader.Config.RememberPassword && logonDetails.AccessToken != null &&
                loggedOn.Result is EResult.InvalidPassword
                or EResult.InvalidSignature
                or EResult.AccessDenied
                or EResult.Expired
                or EResult.Revoked;

            if (isSteamGuard || is2FA || isAccessToken)
            {
                bExpectingDisconnectRemote = true;
                Abort(false);

                if (!isAccessToken)
                {
                    Console.WriteLine("此账户被 Steam Guard 保护。");
                }

                if (is2FA)
                {
                    do
                    {
                        Console.Write("请输入来自你的验证应用的 2 步验证码: ");
                        logonDetails.TwoFactorCode = Console.ReadLine();
                    } while (string.Empty == logonDetails.TwoFactorCode);
                }
                else if (isAccessToken)
                {
                    AccountSettingsStore.Instance.LoginTokens.Remove(logonDetails.Username);
                    AccountSettingsStore.Save();

                    // TODO: Handle gracefully by falling back to password prompt?
                    Console.WriteLine($"访问密钥已被撤回 ({loggedOn.Result}).");
                    Abort(false);
                    return;
                }
                else
                {
                    do
                    {
                        Console.Write("请输入发送到你邮箱里的验证码: ");
                        logonDetails.AuthCode = Console.ReadLine();
                    } while (string.Empty == logonDetails.AuthCode);
                }

                Console.Write("正在重试 Steam3 连接...");
                Connect();

                return;
            }

            if (loggedOn.Result == EResult.TryAnotherCM)
            {
                Console.Write("正在重试 Steam3 连接 (TryAnotherCM)...");

                Reconnect();

                return;
            }

            if (loggedOn.Result == EResult.ServiceUnavailable)
            {
                Console.WriteLine("无法登录到 Steam3: {0}", loggedOn.Result);
                Abort(false);

                return;
            }

            if (loggedOn.Result != EResult.OK)
            {
                Console.WriteLine("无法登录到 Steam3: {0}", loggedOn.Result);
                Abort();

                return;
            }

            Console.WriteLine(" 完成！");

            this.seq++;
            IsLoggedOn = true;

            if (ContentDownloader.Config.CellID == 0)
            {
                Console.WriteLine("使用 Steam3 推荐的 CellID: " + loggedOn.CellID);
                ContentDownloader.Config.CellID = (int)loggedOn.CellID;
            }
        }

        private void LicenseListCallback(SteamApps.LicenseListCallback licenseList)
        {
            if (licenseList.Result != EResult.OK)
            {
                Console.WriteLine("无法获取到证书列表: {0} ", licenseList.Result);
                Abort();

                return;
            }

            Console.WriteLine("从账户获取到 {0} 个证书！", licenseList.LicenseList.Count);
            Licenses = licenseList.LicenseList;

            foreach (var license in licenseList.LicenseList)
            {
                if (license.AccessToken > 0)
                {
                    PackageTokens.TryAdd(license.PackageID, license.AccessToken);
                }
            }
        }

        private static void DisplayQrCode(string challengeUrl)
        {
            // Encode the link as a QR code
            using var qrGenerator = new QRCodeGenerator();
            var qrCodeData = qrGenerator.CreateQrCode(challengeUrl, QRCodeGenerator.ECCLevel.L);
            using var qrCode = new AsciiQRCode(qrCodeData);
            var qrCodeAsAsciiArt = qrCode.GetGraphic(1, drawQuietZones: false);

            Console.WriteLine("使用 Steam 手机应用扫码此二维码登录:");
            Console.WriteLine(qrCodeAsAsciiArt);
        }
    }
}
