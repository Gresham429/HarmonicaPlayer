# v0.5.0 发布验证

日期：2026-10-17（Asia/Tokyo）。开发基线为v0.4.0提交`a24e6565ef01ba3fdad7ae21afd2b442c0df29d3`；MIDI尽力转换与播放列表的开发阶段记录见[2026-10-12验证](docs/VALIDATION-2026-10-12-MIDI-PLAYLIST.md)。

## 维护者反馈与授权

维护者原话：“测试了大致功能，可以发布了，先确定下版本和git状态”。随后明确授权：“就按 v0.5.0 发布，完整后台核心测试通过后提交、推送并创建 Release。”本记录不将概括反馈扩写为逐项Windows窗口、热键、真实音频或游戏输入验收全部通过。

本轮按要求只运行后台核心入口、编译与打包；不启动播放器或WindowsSmoke，不注册全局热键，不发声，不发送键鼠或操作游戏。

## 提交前实际检查

| 检查 | 结果 |
|---|---|
| 完整ParserTests核心入口 | **817项通过** |
| 其中MIDI导入 | **117项通过**，含52ms边界扫描 |
| 其中播放列表/四键设置 | **47项通过** |
| 主程序Release编译（独立输出目录） | **0警告、0错误** |
| WindowsSmoke Release编译（独立输出目录） | **0警告、0错误，仅编译，未运行** |
| 工作区差异空白检查 | 通过 |

总数：99基础/设置/异步＋54文档＋24gap＋30音频时间线/MIDI＋49采样＋88释放段＋65短音＋72播放时间＋15调度保护＋117MIDI导入＋157音区输入计划＋47播放列表＝817。核心音频检查为内存计算，输入计划检查不调用NativeInput或SendInput。

```powershell
dotnet run --project .\Tests\ParserTests.csproj -c Release
dotnet build .\Tests\WindowsSmoke\WindowsSmokeTests.csproj -c Release -o .\bin\release-0.5.0-20261017-0a10ba0f\windows-smoke
dotnet build .\HarmonicaPlayer.csproj -c Release -o .\bin\Development\20261017-v0.5.0
git -c core.autocrlf=false diff --check
```

## 发布产物与文件范围

发布使用`Build-Release.ps1 -Background`，该入口再次运行完整核心检查并仅编译WindowsSmoke，通过后生成win-x64 / self-contained / single-file程序。最终发布构建在发布提交之后进行，使EXE产品版本包含对应源码提交。版本、窗口标题、脚本和当前说明统一为0.5.0，TXT格式仍为HarmonicaPlayer/1。

发布包为`HarmonicaPlayer-v0.5.0-win-x64.zip`，配套提供`.sha256`。保留三个格式/试听示例、音色对比WAV、正式说明、标准AI模板与授权；**不包含简谱目录或模板草案**。旧版本文档和标签保留，远端历史曲谱树不变。

发布提交只含本次功能、测试和配套文档，使用独立Git暂存区。原有`UPDATE-0.1.4.md`、`UPDATE-0.3.3.md`和`VALIDATION-0.3.3.md`暂存改动保持原状；本地曲谱新增/删除与`ai转谱模板-完善草案.txt`不纳入提交。

上传前核对ZIP完整性和文件清单、EXE版本与源码提交、包内文档、SHA256，以及排除文件和原暂存改动是否保留。最终附件校验值随Release发布，不将旧开发EXE作为本次发行包。后台日志、原索引备份和产物核对记录保存在本机`bin/release-0.5.0-20261017-0a10ba0f/`。

WindowsSmoke运行、真实音频主观听感、游戏输入及自包含EXE启动均未由本轮自动执行；编译、核心测试和文件校验不能替代这些运行验收。
