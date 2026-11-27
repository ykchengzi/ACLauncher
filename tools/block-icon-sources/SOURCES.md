# 方块图标素材来源

本目录是 `tools/build_block_icons.py` 的输入素材，不参与程序编译，只用于重新生成
`Resources/Blocks/*.png`。所有素材统一为 300×300 透明画布，构建时按 2 倍最近邻放大到
600×600（不插值、不变形）。

## 方块（9 个）

来自 [中文 Minecraft Wiki](https://zh.minecraft.wiki/w/%E6%96%B9%E5%9D%97) 的方块渲染图
（Java 版渲染，300×300，透明底，45°/30° 等距投影，可见区域 268×296 ≈ 0.905 长宽比）：

| 文件 | Wiki 文件名 |
| --- | --- |
| `cobblestone.png` | `Cobblestone_JE7_BE3.png` |
| `command_block.png` | `Impulse_Command_Block_JE5_BE2.png`（脉冲命令方块，橙色） |
| `gold_block.png` | `Block_of_Gold_JE6_BE3.png` |
| `grass_block.png` | `Grass_Block_JE7_BE6.png` |
| `dirt_path.png` | `Dirt_Path_JE4_BE3.png` |
| `anvil.png` | `Anvil_(N)_JE3.png` |
| `redstone_block.png` | `Block_of_Redstone_JE4_BE2.png` |
| `redstone_lamp_on.png` | `Lit_Redstone_Lamp_JE5.png` |
| `redstone_lamp_off.png` | `Redstone_Lamp_JE5_BE2.png` |

这些渲染图包含 **Mojang Studios** 的方块美术素材，版权归 Mojang 所有，随包分发仅用于
图标展示，不随本项目许可证授予任何权利（同 `Resources/Skins/` 的处理方式）。

## 加载器图标（4 个）

| 文件 | 来源 |
| --- | --- |
| `fabric.png` | <https://fabricmc.net/assets/logo.png>，Fabric 官方毛线球 logo（52×56，整数 5 倍放大保像素锐利） |
| `quilt.png` / `quilt.svg` | <https://quiltmc.org/assets/img/logo.svg>，Quilt 官方彩色补丁 logo（矢量渲染） |
| `forge.png` / `forge.svg` | <https://minecraftforge.net/> 页头 logo 中的砧形标志；`forge.svg` 已把文字设为 `fill:none`，仅留砧形并填入官方色 `#DFA86A` |
| `neoforge.png` / `neoforge.svg` | `neoforged/Documentation` 仓库 `static/img/logo.svg`，NeoForge 官方狐狸 logo（灰底板保留） |

Fabric、Quilt、Forge、NeoForge 的名称与图标为其各自项目所有，本项目与其无隶属关系。
