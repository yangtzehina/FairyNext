# .fui 样例包（M1-12 前端读取器的字段级对照物）

四个**未经修改**的已发布 .fui 包，逐字节拷自 `oracle.lock` 钉死的 fork：

| 文件 | 来源（oracle @ 08a2d56） | sha256 | 为什么选它 |
|---|---|---|---|
| `VirtualList.fui` | `Assets/Examples/Resources/UI/VirtualList_fui.bytes` | `6dac8916…` | 控制器 / gear / 关系 / transition / 嵌套组件全在一个 3.4KB 包里 |
| `Cooldown.fui` | `Assets/Examples/Resources/UI/Cooldown_fui.bytes` | `6228d854…` | 位图字体条目、依赖表非空、sprite 裁白 offset 与旋转入图集 |
| `ScrollPane.fui` | `Assets/Examples/Resources/UI/ScrollPane_fui.bytes` | `e6709ebb…` | 组件级 `overflow=scroll`（块 7 滚动配置）、关系边类型混合 |
| `TextMeshPro.fui` | `Assets/Examples/Resources/UI/TextMeshPro_fui.bytes` | `3fa963e9…` | 描述符 **version 7**（走 `version >= 5` 的尾字段分支）、RichText/InputText |

改名只去掉了 Unity 的 `_fui.bytes` 后缀（那是 `TextAsset` 的加载约定，与格式无关）。

## 断言里的常量从哪来

用例断言的组件数 / 名字 / 尺寸 / 孩子数 / 关系边 / 滚动 flags **不是**本读取器自己跑出来的
（那样等于拿实现验实现），而是抄自 FairyGUI 编辑器工程的**授权 XML**——.fui 的上游源文件：

```
~/ECS/FairyGUI-unity/UIProject/assets/<包名>/package.xml     ← 条目 id / 类型 / 名字 / 九宫格
~/ECS/FairyGUI-unity/UIProject/assets/<包名>/<组件>.xml      ← 组件尺寸 / 显示列表 / 关系 / gear / 控制器页
```

每条断言的注释里写了它抄的是哪个文件的哪一行属性。授权 XML 与发布产物是两套独立表示，
两边对得上才说明读取器读对了。

已知的两处**正常**差异（不是 bug，别去"修"）：

1. 发布产物比 `package.xml` 的 `<resources>` **少**未被引用的资源（Cooldown 的 `ltiqn`/15.png
   没有任何 sprite 引用它，发布时被丢弃），**多**一条发布期生成的 `atlas0` 图集条目。
2. 组件 XML 里的 `selected=` 是编辑器当前选中页，与 .fui 里的 homePage 不是一回事。

## 重新取样

fork 换 SHA 后这四个文件**不需要**重新拷贝，除非 fork 真的重发布了这些包
（`Assets/Examples/Resources/UI/*_fui.bytes` 的内容变了）。真变了就重拷 + 更新上表 sha256 +
重跑 `dotnet run --project tests/FairyNext.Tests`；断言常量随授权 XML 一起改。
