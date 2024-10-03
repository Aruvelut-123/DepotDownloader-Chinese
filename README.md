DepotDownloader
===============

Steam depot 下载器使用 SteamKit2 库。支持 .NET 8.0

此程序必须在控制台运行，因为它没有 GUI。

## 安装

### 从 GitHub 下载

从[每日构建页面](https://github.com/Aruvelut-123/DepotDownloader-Chinese/actions)下载。

### 通过 Windows 包管理器 CLI (又叫 winget) (仅限未汉化版)

在 Windows 上，[winget](https://github.com/microsoft/winget-cli) 用户可以通过安装 `SteamRE.DepotDownloader` 来下载和安装最新版的终端版本，具体指令如下:

```powershell
winget install --exact --id SteamRE.DepotDownloader
```

### 通过 Homebrew (仅限未汉化版)

在 macOS 上，[Homebrew](https://brew.sh) 用户可以通过运行以下指令下载和安装最新版本:

```shell
brew tap steamre/tools
brew install depotdownloader
```

## 用法

### 下载一个应用的一个或所有的 depot
```powershell
./DepotDownloader -app <id> [-depot <id> [-manifest <id>]]
                 [-username <用户名> [-password <密码>]] [其他选项]
```

例如: `./DepotDownloader -app 730 -depot 731 -manifest 7617088375292372759`

默认会使用匿名用户登录 ([查看哪些应用可以使用匿名用户下载](https://steamdb.info/sub/17906/))。

如想使用你的账户，在命令中指定 `-username <用户名>` 参数。如果你没有指定 `-password` 参数的话，程序会自动询问账号的登录密码。

### 使用 pubfile id 来下载一个创意工坊物品
```powershell
./DepotDownloader -app <id> -pubfile <id> [-username <用户名> [-password <密码>]]
```

例如: `./DepotDownloader -app 730 -pubfile 1885082371`

### 使用 ugc id 来下载一个创意工坊物品
```powershell
./DepotDownloader -app <id> -ugc <id> [-username <用户名> [-password <密码>]]
```

例如: `./DepotDownloader -app 730 -ugc 770604181014286929`

## 参数

参数               | 描述
----------------------- | -----------
`-app <#>`				| 预下载的 AppID。
`-depot <#>`			| 预下载的 DepotID。
`-manifest <id>`		| 预下载的内容的 manifest id (需要 `-depot` 参数，默认: 默认分支)。
`-ugc <#>`				| 预下载的 UGC ID。
`-beta <分支名>`	| 如果可用则从特定分支下载 (默认: Public分支)。
`-betapassword <密码>`	| 如果需要的话则填写分支密码。
`-all-platforms`		| 当 `-app` 参数使用时下载所有仅限对应平台的 depot。
`-os <系统>`				| 要下载游戏的操作系统（windows、macos 或 linux，默认：程序当前运行的操作系统）
`-osarch <架构>`		| 要下载游戏的架构（32 或 64，默认：主机的架构）
`-all-languages`		| 当使用 `-app` 参数时，下载所有特定语言的仓库。
`-language <语言>`		| 要下载游戏的语言（默认：英语）
`-lowviolence`			| 当使用 `-app` 参数时，下载低暴力的仓库。
`-pubfile <#>`			| 要下载的 PublishedFileId。 （将自动解析为 UGC id）
`-username <用户名>`		| 要登录以访问受限制内容的帐户的用户名。
`-password <密码>`		| 要登录以访问受限制内容的帐户的密码。
`-remember-password`	| 如果设置，将记住此用户的后续登录密码。 （使用 `-username <用户名> -remember-password` 作为登录凭据）
`-dir <安装路径>`     | 要放置下载文件的目录。
`-filelist <file.txt>`	| 要下载的文件列表（来自清单）。如果要以正则表达式进行匹配，请在文件路径前加上 `regex:` 前缀。
`-validate`				| 包含已下载文件的校验和验证
`-manifest-only`		| 下载任何要下载的仓库的人类可读清单。
`-cellid <#>`			| 要从中下载内容的覆盖 CellID 的服务器。
`-max-servers <#>`		| 要使用的最大内容服务器数量。 （默认：20）。
`-max-downloads <#>`	| 同时下载的最大块数。 （默认：8）。
`-loginid <#>`			| 一个唯一的32位十进制Steam LogonID，如果同时运行多个DepotDownloader实例，则必需。
`-V` 或 `--version`     | 打印版本和运行库

## FAQ

### 为什么我每次运行此应用时被要求输入 2 步验证码？
你的 2 步验证码验证了一个 Steam 会话。你需要使用 `-remember-password` 参数来“记住”你的会话，通常是生成一个登录密钥给你的 Steam 会话。

### 我可以在账号已经登录到 Steam 之后运行 DepotDownloader 吗？
如果它们使用同一个登录 ID 的话，所有的 Steam 的连接都会被断开。你可以使用 `-loginid` 参数指定一个不同的登录 ID。

### 为什么我的包含特殊字符的密码不能使用？我必须在命令行上面定义密码吗？
在命令行中传递包含特殊字符的密码时，您需要根据您使用的shell适当地转义命令。只要您包含一个 `-username` 参数，您就不需要在命令行上包含 `-password` 参数。您将被提示输入密码。
