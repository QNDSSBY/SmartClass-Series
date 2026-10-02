# 智慧课堂系统 · GitHub 上传目录

> 编制：金华一中信息部　|　版本：1.2.0
> 本目录已按「源码 + 工程配置 + 文档 + 小体积素材」筛选完毕，可直接作为 Git 仓库内容。

## 目录结构

| 目录 | 内容 |
|---|---|
| `*.cs` / `*.xaml` | 应用源码（根目录平铺，与 Visual Studio 工程一致） |
| `Properties/` | 启动配置与发布配置文件（已排除 `.user`） |
| `Assets/` | 随程序分发的素材：提示音、内置壁纸、应用 Logo |
| `installer/` | WiX 安装包源码（`generate.ps1` + `product.wxs`） |
| `docs/` | 开发文档 |
| `promo/` | 宣传片页面与素材（可选，占本目录一半体积） |

## 开始使用

```bash
cd github-upload
git init
git add .
git commit -m "智慧课堂系统 1.2.0 源码与文档"
git remote add origin <你的仓库地址>
git push -u origin main
```

提交前核对：`git status --short` 中不应出现 `*.msi`、`dist/`、`WebView2Runtime/`。

## 未纳入的文件（及原因）

| 排除项 | 体积 | 原因 |
|---|---|---|
| `installer/*.msi`（10 个） | 2960 MB | 单个 328–330 MB，超出 GitHub 100 MB/文件硬上限 |
| `bin/` `obj/` | 978 MB | 构建产物 |
| `dist/` `publish-output/` | 1690 MB | 发布产物（含调试符号） |
| `WebView2Runtime/` | 647 MB | 内置浏览器运行时，应由官方分发包提供 |
| `.wix/` | 0.9 MB | WiX 扩展缓存 |
| `Smart_Class/` | 0 | 空目录（安装脚本试跑残留） |
| `*.wixpdb` | 0.8 MB | 打包调试符号 |
| `金华一中科技校园套件.csproj.user` | 0.6 KB | 含本机绝对路径 |
| `*/*.pubxml.user` | — | 含本机路径与操作历史 |
| `jaww6-e15x7-001.ico` `wodhe-3zqyu-001.ico` | 81 KB | 未被任何工程文件引用的孤立文件 |

体积较大的素材如需分发，建议走 **GitHub Releases 附件**（单文件可达 2 GB），不要塞进仓库。

## 安全提示

`docs/智慧课堂系统网页开发文档.md` 记录了**网页端源码中的明文凭据**（数据库口令、钉钉密钥、内部令牌、加密密钥、API Key、默认管理员口令）。

- 若仓库为 **Public**：请先将该文档移出版本控制（`git rm --cached`）并**轮换全部密钥**；
- 若为 **Private**：可保留，但仍建议后续改为引用外部密钥管理，不要把凭据写进文档。

桌面端源码（本目录根部的 `.cs` / `.xaml`）已扫描，**不含明文密码或令牌**。