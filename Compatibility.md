# BatteryBar Device Edition 0.1.3 兼容清单

下表列出本版实现的读取路径。新增路径依据协议资料或其他项目的实机记录接入，均待本版 Windows 实机验证。ATK 烈空 ZERO 的指定接收器已在 Gear Pair 0.1.4 实测成功，本版沿用该路径。

## 既有与新增读取路径

| 品牌 | 型号和范围 | USB VID:PID 或识别条件 |
| --- | --- | --- |
| 罗技 G | G915 X LIGHTSPEED TKL；可发现的 HID++ 2.0 鼠标、键盘；具备 HID++ ADC 功能的部分 G 系列耳机。G502 LIGHTSPEED 可尝试电压估算，PRO X SUPERLIGHT 系列取决于对应接收器及功能响应 | HID++ 0x1004、0x1000、0x1001、0x1F20；常见接收器 046D:C539、C53A、C53D、C53F、C541、C545、C547、C548、C54D、C52B、C532；兼容的直连接口也会检查 |
| 雷蛇 Razer | Viper V4 Pro、V3 Pro、V2 Pro、Viper Ultimate、DeathAdder V4 Pro、V3 Pro、V3 HyperSpeed、V2 Pro、Basilisk V3 Pro、Basilisk Ultimate、Cobra Pro、Orochi V2、Mamba Wireless、Naga Pro、Naga V2 HyperSpeed | 1532:00E5/00E6、00C2、00A5、007A/007B、00BF、00B6、00B8、007C、00AA、0088、00AF、0094、0072、008F、00B3。仅列出的接口路径 |
| 华硕 ROG | Harpe II Ace 及共享接收器的 Harpe II，Gladius III Wireless AimPoint、Keris Wireless AimPoint、Keris II Ace、Keris II Origin、ROG OMNI 的鼠标电量通道 | 0B05:1AD0/1C69、1A72、1A68/1A6A、1B1A、1C0E、1ACE。OMNI 读取鼠标通道并显示系列名称 |
| 赛睿 SteelSeries | Arctis Nova 7/7X/7P、Nova 7 Gen 2、Nova 3P/3X、Nova Pro Wireless；Aerox 3/5/9 Wireless | 1038:2202/2206/220A、22A1/22A5/22A7、227E、2269/226D、12E0/12E5；Aerox 1838/1878、1852/185C/1860、1858/1874 |
| 雷柏 Rapoo | VT9 Pro V2、VT7 Gen 2、VT3 Max Gen 2、VT9 Gen 2、V700DIY-98 | 24AE:14A9、1413/4613、1417/4617、1414/4614、1924/4824；鼠标和键盘按各自输入报告读取，设备需要主动上报 |
| Pulsar | X2H 及共用已知 17 字节接收器的部分鼠标；X2 CrazyLight Mini | 3554:F507/F508/F509；3710:5406/3414 |
| ATK / VXE | 烈空 ZERO、ATK X1 Pro、Fire X 8K 接收器、VXE R1 Pro Max、R1 SE+ | 373B:1154/1155/12CC、101A、101B、1085；3554:F58A/F58C/F58F。实测沿用：373B:12CC |
| 迈从 MCHOSE | M7 Ultra、L7 Pro、A7、A7 V2 Ultra、K7 Ultra；同接收器上可识别的 Realtek 鼠标 | 5253:1020/0031、1021；3837:100B、1001。共享 1020 时按回包中的型号决定名称 |
| 樱桃 CHERRY | MX 3.0S Wireless、PIXIU 75、使用指定接收器的 XTRFY 无线鼠标 | 046A:00EA/00E3、01E4、0330 |
| HyperX | Cloud III S Wireless、Cloud III Wireless、Cloud II Wireless／Core Wireless、Cloud Alpha 2 Wireless、Pulsefire Haste 2 Wireless／Core Wireless | 03F0:02CC/06BE、05B7/0C9D、0696/018B、0995/0795、08BE、0AB5/0F98/0D97 |
| 索尼 Sony | INZONE H5、INZONE H9 | 054C:0EBF 的 HID 接收器；054C:0E53 的 USB 串口接收器 |
| 阿米洛 Varmilo、森海塞尔 Sennheiser | 通用蓝牙电量及设备图标 | Windows 提供电量时可显示 |

斜杠分隔同一路径的不同 ID 或连接形式，实际与设备、接收器和固件有关。标为电量估算的是电压换算或离散电量等级。不同协议的 Logitech Centurion 耳机、Pulsar X2 V3 64 字节系列、INZONE H9 II／Buds 和其他接收器仍需单独验证和适配。USB 读取只匹配当前已知的协议入口，兼容清单不代表整个品牌的实机覆盖率。

## 协议参考

- [HaloBattery 协议文档](https://github.com/HeyOkay/HaloBattery/blob/main/docs/protocols.md)
- [Solaar 设备与接收器](https://pwr-solaar.github.io/Solaar/devices/) 和 [Linux HID++ 读取逻辑](https://github.com/torvalds/linux/blob/master/drivers/hid/hid-logitech-hidpp.c)
- [Razer 鼠标接口和事务号](https://github.com/openrazer/openrazer/blob/master/driver/razermouse_driver.c)
- [G-Helper ROG 型号及接口](https://github.com/seerge/g-helper) 和 [OpenChatMix 赛睿状态报告](https://github.com/Swiftlyx/OpenChatMix)
- [Rapoo 协议及实机记录](https://github.com/krisgfx9/Wireless-Device-Battery-Monitor) 和 [VT 系列状态报告](https://github.com/HeyOkay/HaloBattery/pull/215)
- [MCHOSE Realtek 协议](https://github.com/alexfrih/mchose-linux/blob/main/PROTOCOL.md) 和 [CHERRY BatteryWatch](https://github.com/B1Chat/BatteryWatch)
- [HyperX Haste 2 协议](https://github.com/krisgfx9/Wireless-Device-Battery-Monitor/blob/main/docs/hyperx_haste_2.md) 和 [Sony INZONE H9 电量读取](https://github.com/janit88/InZoneH9Tray)
