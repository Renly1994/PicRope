# 图绳发版约定

每次发布新版本，必须先完成夸克网盘上传，再发布 GitHub Release。

上传目标文件夹：`Windows晾衣绳（图绳PicPope）最新版本安装包`。

上传目录地址：<https://pan.quark.cn/list#/list/all/333c7eab173b4614a50a5d614b327852-Windows%E6%99%BE%E8%A1%A3%E7%BB%B3%EF%BC%88%E5%9B%BE%E7%BB%B3PicPope%EF%BC%89%E6%9C%80%E6%96%B0%E7%89%88%E6%9C%AC%E5%AE%89%E8%A3%85%E5%8C%85>。

固定分享链接：[夸克网盘](https://pan.quark.cn/s/d1bdfe7b8d19)，与 `src/AppUpdates.cs` 中的 `BackupDownloadUrl` 保持一致。

1. 完成构建、更新测试和打包，使用生成的 `releases/PicRope-版本号-windows-x64.zip` 及同名 `.sha256` 文件，准备对应版本的安装与更新说明。
2. 上传到用户指定的夸克网盘文件夹。更新窗口中的「网盘下载」必须指向该文件夹的分享链接。
3. 上传完成后核对网盘中的文件名和文件大小，并从分享链接确认本版本文件可见。未上传成功或未完成核验时，不发布正式 Release。
4. 将同一份 ZIP 和 SHA256 文件上传到对应版本的 GitHub Release，再发布并设为最新版本。上传网盘后不重新打包，确保两个渠道提供相同文件。
5. GitHub 发布说明包含夸克网盘备用下载链接；如分享链接或提取码变更，同步更新应用入口与 README。

夸克登录失效、分享链接不可用或目标文件夹不明确时，先解决对应问题，再继续发版。
